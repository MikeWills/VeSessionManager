using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Web;

/// <summary>
/// UserManager.GetUserAsync(ClaimsPrincipal) does a plain lookup with no eager-loading, so neither
/// User.UserTeams (needed by SessionAccessScope/AdminAccessScope's Contains-based checks for a
/// TeamAdmin/SessionManager, since issue #19 made team membership a set instead of a single
/// User.TeamId) nor User.ManagedByUser (needed for a TeamLead, whose effective teams resolve
/// transitively through their manager's own UserTeams) come back loaded. Every page that calls into
/// SessionAccessScope/AdminAccessScope must load through here instead of userManager.GetUserAsync
/// directly, or a TeamAdmin/SessionManager/TeamLead silently sees zero teams/sessions instead of
/// their real assignment.
/// </summary>
public static class CurrentUserLoader
{
    public static async Task<User?> GetUserWithManagerAsync(this UserManager<User> userManager, AppDbContext dbContext, ClaimsPrincipal principal)
    {
        var id = userManager.GetUserId(principal);
        if (id is null || !int.TryParse(id, out var userId))
        {
            return null;
        }

        return await dbContext.Users
            .Include(u => u.UserTeams)
            .Include(u => u.ManagedByUser).ThenInclude(m => m!.UserTeams)
            .FirstOrDefaultAsync(u => u.Id == userId);
    }

    /// <summary>
    /// The same load, for the ~22 call sites that cannot proceed without a user and were each
    /// spelling out an identical <c>?? throw new InvalidOperationException(...)</c> (#307, DUP-06).
    ///
    /// <para>Null here means the cookie names a user who is no longer in the database — an account
    /// deleted, or a database restored, beneath a still-valid cookie. On an <c>[Authorize]</c>d page
    /// that is not a state any handler can do anything sensible with, so it throws rather than
    /// returning null and inviting a null-check nobody writes.</para>
    ///
    /// <para><b>It should be unreachable.</b> <c>StaleAuthCookieFilter</c> runs on every page and
    /// exists precisely to turn this into a clean sign-out rather than a 500. The throw is the
    /// backstop for a route that somehow bypasses it, which is why the message names the condition
    /// rather than apologising.</para>
    /// </summary>
    public static async Task<User> GetRequiredUserAsync(
        this UserManager<User> userManager, AppDbContext dbContext, ClaimsPrincipal principal) =>
        await userManager.GetUserWithManagerAsync(dbContext, principal)
            ?? throw new InvalidOperationException("No authenticated user for an [Authorize]d page.");

    /// <summary>Key for the per-request cache below. Scoped to this class; nothing else writes it.</summary>
    private const string CachedUserItemKey = "VeSessionManager.CurrentUser";

    /// <summary>
    /// Same load, cached for the lifetime of one request.
    ///
    /// <para>A signed-in page render loads the user two to three times: once in the page handler and
    /// once or twice in <c>_AppLayout</c> (which needs the team name and the nav badge counts). Each
    /// load is a three-table include, and every authenticated page pays it.</para>
    ///
    /// <para><b>Use this only for reads.</b> The cached instance comes from the same scoped
    /// DbContext, so it is the same tracked entity a handler would get — fine for authorization
    /// checks and display, which is all any caller does today. A handler that wanted to *modify* the
    /// current user should load it itself and be explicit about that.</para>
    ///
    /// <para>Deliberately still routes through <see cref="GetUserWithManagerAsync"/> rather than a
    /// bare <c>GetUserAsync</c>: a user loaded without <c>UserTeams</c> silently gives every scoped
    /// role an empty team set (see CLAUDE.md's Known Constraints), and a cache is exactly the place
    /// that mistake would spread from.</para>
    /// </summary>
    public static async Task<User?> GetCachedUserWithManagerAsync(
        this UserManager<User> userManager, AppDbContext dbContext, HttpContext httpContext, ClaimsPrincipal principal)
    {
        if (httpContext.Items.TryGetValue(CachedUserItemKey, out var cached))
        {
            // Stored even when null, so an anonymous request does not re-query on every layout read.
            return (User?)cached;
        }

        var user = await userManager.GetUserWithManagerAsync(dbContext, principal);
        httpContext.Items[CachedUserItemKey] = user;
        return user;
    }

    /// <summary>
    /// <see cref="GetRequiredUserAsync"/> against the per-request cache — for the read-only pages
    /// that use <see cref="GetCachedUserWithManagerAsync"/> and equally cannot proceed without a
    /// user. Same "should be unreachable" reasoning.
    /// </summary>
    public static async Task<User> GetRequiredCachedUserAsync(
        this UserManager<User> userManager, AppDbContext dbContext, HttpContext httpContext, ClaimsPrincipal principal) =>
        await userManager.GetCachedUserWithManagerAsync(dbContext, httpContext, principal)
            ?? throw new InvalidOperationException("No authenticated user for an [Authorize]d page.");
}
