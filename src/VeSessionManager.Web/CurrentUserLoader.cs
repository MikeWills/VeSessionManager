using System.Security.Claims;
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
}
