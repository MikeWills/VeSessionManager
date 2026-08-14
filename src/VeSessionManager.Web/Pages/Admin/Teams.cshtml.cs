using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Authorization;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Web.Pages.Admin;

/// <summary>
/// Team list — and, since 2026-08-06, the way into everything configured per team (settings, users,
/// email templates, maintenance) via each row's menu. Credential editing itself still lives on
/// TeamSettings, not here.
///
/// <para><b>TeamAdmin can view this page, scoped to their own teams.</b> Opening it up is what lets
/// the per-team pages come off the nav: a nav link lands on "Select a team…", whereas arriving from a
/// row picks the team for you.</para>
///
/// <para><b>The list is scoped rather than the row actions being gated.</b> Both would work, but
/// scoping is the stronger of the two and leaves nothing to get wrong later: every row a viewer can
/// see is one they may manage, so the row menu needs no per-row condition. Showing all teams read-only
/// was the first cut; it meant a TeamAdmin could click into a row whose child page would then quietly
/// resolve to a *different* team — the child pages fall back to a team the viewer does manage rather
/// than refusing — which reads as a bug even though nothing leaks.</para>
///
/// <para>Creating a team stays SystemAdmin, enforced in the handler and not merely by hiding the
/// button: the page is reachable by TeamAdmin now, so the handler is the only real guard.</para>
/// </summary>
[Authorize(Roles = RoleGroups.Admins)]
public class TeamsModel(
    AppDbContext dbContext,
    UserManager<User> userManager,
    AdminAccessScope adminAccessScope,
    TeamSettingsService teamSettingsService) : PageModel
{
    public IReadOnlyList<TeamRow> Teams { get; private set; } = [];

    /// <summary>SystemAdmin only — drives whether the "New Team" button renders at all.</summary>
    public bool CanCreateTeam { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        // GetWithManager, not the bare GetUserAsync: AdminAccessScope reads UserTeams and
        // ManagedByUser, which the bare call leaves unloaded — a TeamAdmin would silently manage
        // nothing and see an empty list. See CLAUDE.md's note on this.
        var user = await userManager.GetUserWithManagerAsync(dbContext, User);
        if (user is null)
        {
            return Forbid();
        }

        CanCreateTeam = adminAccessScope.CanCreateTeam(user);

        // ScopeTeams, not a hand-rolled filter: it is the existing definition of "which teams may this
        // user see" (SystemAdmin: all; everyone else: their own) and is already covered by
        // AdminAccessScopeTests. Re-deriving the same predicate here is how two answers to one
        // question start drifting.
        Teams = await adminAccessScope.ScopeTeams(dbContext.Teams, user)
            .OrderBy(t => t.Name)
            .Select(t => new TeamRow(t.Id, t.Name, t.CreatedUtc))
            .ToListAsync();

        return Page();
    }

    public async Task<IActionResult> OnPostCreateAsync(string name)
    {
        var user = await userManager.GetUserWithManagerAsync(dbContext, User);
        if (user is null)
        {
            return Forbid();
        }

        if (!adminAccessScope.CanCreateTeam(user))
        {
            return Forbid();
        }

        var (result, _) = await teamSettingsService.CreateAsync(name, user.Id, CancellationToken.None);

        // Was a two-outcome message that reported every failure as "already exists" — so a blank or
        // tampered post would have claimed a duplicate of a team with no name (issue #275).
        TempData[result == TeamActionResult.Success ? "StatusMessage" : "ErrorMessage"] = result switch
        {
            TeamActionResult.Success => $"Team '{name}' created.",
            TeamActionResult.NameRequired => "Enter a team name.",
            TeamActionResult.DuplicateName => $"A team named '{name}' already exists.",
            _ => "Could not create that team."
        };
        return RedirectToPage();
    }

    public record TeamRow(int Id, string Name, DateTime CreatedUtc);
}
