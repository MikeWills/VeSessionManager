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
    TeamSettingsService teamSettingsService,
    TeamDeletionService teamDeletionService) : PageModel
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
        // Projected to the raw flags rather than materializing Team entities — each of those costs
        // five EncryptedStringConverter decryptions, and this page only needs a name and a date.
        var rows = await adminAccessScope.ScopeTeams(dbContext.Teams, user)
            .OrderBy(t => t.Name)
            .Select(t => new
            {
                t.Id,
                t.Name,
                t.CreatedUtc,
                t.IntegrationOverridesEnabled,
                t.ZoomEnabled,
                t.DiscordEnabled,
                t.SquareEnabled,
                t.EmailEnabled,
                t.DeactivatedUtc
            })
            .ToListAsync(HttpContext.RequestAborted);

        // How much each team holds, for the delete confirmation. Two grouped queries rather than a
        // per-row count: this list is short but the counts are only ever read by one modal, and N+1
        // queries to fill in a dialog most visits never open is the wrong trade.
        var teamIds = rows.Select(t => t.Id).ToList();
        var sessionCounts = await dbContext.Sessions
            .Where(x => teamIds.Contains(x.TeamId))
            .GroupBy(x => x.TeamId)
            .Select(g => new { TeamId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.TeamId, g => g.Count, HttpContext.RequestAborted);
        var candidateCounts = await dbContext.Candidates
            .Where(c => teamIds.Contains(c.Session.TeamId))
            .GroupBy(c => c.Session.TeamId)
            .Select(g => new { TeamId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.TeamId, g => g.Count, HttpContext.RequestAborted);

        // Muted set resolved in memory: Team.MutedIntegrations is C# and EF cannot translate it, and
        // restating the rule here in a form it could translate is exactly the duplication #305 was
        // about.
        Teams = [.. rows.Select(t => new TeamRow(t.Id, t.Name, t.CreatedUtc, t.DeactivatedUtc,
            sessionCounts.GetValueOrDefault(t.Id), candidateCounts.GetValueOrDefault(t.Id), new Team
        {
            Name = t.Name,
            IntegrationOverridesEnabled = t.IntegrationOverridesEnabled,
            ZoomEnabled = t.ZoomEnabled,
            DiscordEnabled = t.DiscordEnabled,
            SquareEnabled = t.SquareEnabled,
            EmailEnabled = t.EmailEnabled
        }.MutedIntegrations))];

        return Page();
    }

    /// <summary>
    /// Stops or resumes the app acting for a team. Reversible, and deliberately leaves the team on
    /// this list — see <see cref="Team.DeactivatedUtc"/>.
    /// </summary>
    public async Task<IActionResult> OnPostSetActiveAsync(int teamId, bool active)
    {
        var user = await userManager.GetUserWithManagerAsync(dbContext, User);
        if (user is null)
        {
            return Forbid();
        }

        // Same gate as creating one: this is a deployment-shaping action, not a per-team setting.
        if (!adminAccessScope.CanCreateTeam(user))
        {
            return Forbid();
        }

        var result = await teamSettingsService.SetActiveAsync(teamId, active, user.Id, CancellationToken.None);
        TempData[result == TeamActionResult.Success ? "StatusMessage" : "ErrorMessage"] = result switch
        {
            TeamActionResult.Success when active => "Team reactivated — the app will poll and send for it again.",
            TeamActionResult.Success => "Team deactivated — no ingestion, message rules or reconciliation until it is turned back on.",
            TeamActionResult.NotFound => "That team no longer exists.",
            _ => "Could not change that team."
        };
        return RedirectToPage();
    }

    /// <summary>
    /// Deletes a team and everything it owns, permanently.
    ///
    /// <para><b>The typed name is the guard, and it is deliberately not a second "are you sure".</b>
    /// A confirm dialog is answered reflexively; typing the team's name cannot be, and it is the one
    /// check that catches the actual mistake this action invites — pressing delete on the right-looking
    /// row of the wrong team. Checked here rather than only in the browser, because a modal is not a
    /// permission.</para>
    /// </summary>
    public async Task<IActionResult> OnPostDeleteAsync(int teamId, string? confirmName)
    {
        var user = await userManager.GetUserWithManagerAsync(dbContext, User);
        if (user is null)
        {
            return Forbid();
        }

        // Same gate as creating one, and for the same reason: this shapes the deployment rather than
        // configuring a team. A TeamAdmin manages their team; they do not get to remove it.
        if (!adminAccessScope.CanCreateTeam(user))
        {
            return Forbid();
        }

        var team = await dbContext.Teams.AsNoTracking().FirstOrDefaultAsync(t => t.Id == teamId);
        if (team is null)
        {
            TempData["ErrorMessage"] = "That team no longer exists.";
            return RedirectToPage();
        }

        if (!string.Equals(confirmName?.Trim(), team.Name, StringComparison.Ordinal))
        {
            TempData["ErrorMessage"] = $"Nothing was deleted — the name typed did not match \u201c{team.Name}\u201d exactly.";
            return RedirectToPage();
        }

        var (result, summary) = await teamDeletionService.DeleteAsync(teamId, user.Id, CancellationToken.None);
        TempData[result == TeamActionResult.Success ? "StatusMessage" : "ErrorMessage"] = result switch
        {
            TeamActionResult.Success =>
                $"\u201c{team.Name}\u201d deleted, along with {summary!.Sessions} session(s), {summary.Candidates} candidate(s) "
                + $"and {summary.Messages} message(s). Square and ARRL keep their own records.",
            TeamActionResult.NotFound => "That team no longer exists.",
            _ => "Could not delete that team."
        };
        return RedirectToPage();
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

    /// <param name="MutedIntegrations">
    /// Which outbound systems are switched off for this team (#64) — empty for an ordinary team.
    /// Surfaced here so a muted team is recognizable at a glance: its data looks exactly like real
    /// data, and the whole risk of the feature is mistaking one for the other.
    /// </param>
    /// <param name="SessionCount">Shown only in the delete confirmation — a number somebody can check against what they expect, and the last chance to notice the wrong team.</param>
    public record TeamRow(
        int Id,
        string Name,
        DateTime CreatedUtc,
        DateTime? DeactivatedUtc,
        int SessionCount,
        int CandidateCount,
        IReadOnlyList<TeamIntegration> MutedIntegrations)
    {
        /// <summary>See <see cref="Team.DeactivatedUtc"/> — a deactivated team is still listed, deliberately.</summary>
        public bool IsActive => DeactivatedUtc is null;
    }
}
