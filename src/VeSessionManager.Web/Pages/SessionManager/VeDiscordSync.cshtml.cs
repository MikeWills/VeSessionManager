using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Authorization;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Jobs;
using VeSessionManager.Core.VolunteerExaminers;

namespace VeSessionManager.Web.Pages.SessionManager;

/// <summary>
/// What the team's VE tags would become if its Discord roles were applied to them, and the button that
/// applies it (#519) — plus everything the check could not account for.
///
/// <para><b>This page changes nothing.</b> It runs on demand, reads Discord, and shows a plan —
/// applying it is step 3. That split is deliberate rather than incremental: tag <i>removal</i> is in
/// scope, so a mismatched display name can take a real tag off a real person, and the first runs
/// against a live server need to be looked at by a human before anything writes. See
/// docs/discord-tag-sync.md.</para>
///
/// <para>One team at a time, like the VE Tags screen it mirrors: the map, the roles and the roster all
/// belong to one team, and there is nothing coherent to render across several.</para>
/// </summary>
[Authorize(Roles = RoleGroups.Admins)]
[RemembersFilters]
public class VeDiscordSyncModel(
    AppDbContext dbContext,
    UserManager<User> userManager,
    AdminAccessScope adminAccessScope,
    SessionAccessScope accessScope,
    DiscordTagSyncService syncService) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public int? TeamId { get; set; }

    public IReadOnlyList<(int Id, string Name)> AvailableTeams { get; private set; } = [];
    public int? ResolvedTeamId { get; private set; }

    /// <summary>Null until the check is actually run — this page does not call Discord just because someone opened it.</summary>
    public DiscordTagSyncPlan? Plan { get; private set; }

    /// <summary>
    /// Whether the daily job is turned on for this team, and what it did last time — the only place
    /// an unattended run surfaces outside the logs and the Job Run History page.
    /// </summary>
    public bool ScheduledSyncEnabled { get; private set; }
    public JobRunHistory? LastAutomaticRun { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        await LoadAsync();
        return Page();
    }

    /// <summary>
    /// A POST rather than a GET even though it writes nothing: it calls an external service, and a
    /// link that hits Discord every time a browser prefetches it is the kind of thing that gets a bot
    /// rate-limited. The result is rendered directly instead of redirecting, since there is nothing to
    /// re-read it from.
    /// </summary>
    public async Task<IActionResult> OnPostCheckAsync()
    {
        await LoadAsync();
        if (ResolvedTeamId is not { } teamId)
        {
            return Forbid();
        }

        Plan = await syncService.BuildPreviewAsync(teamId, HttpContext.RequestAborted);
        return Page();
    }

    /// <summary>
    /// Applies everything the check found — after re-reading Discord, so a role revoked between looking
    /// and clicking is not applied as though it were still held. <paramref name="fingerprint"/> is what
    /// was on screen; it only decides whether the result says "Discord changed while you were looking",
    /// never whether the write happens.
    /// </summary>
    public async Task<IActionResult> OnPostApplyAsync(string? fingerprint)
    {
        await LoadAsync();
        if (ResolvedTeamId is not { } teamId)
        {
            return Forbid();
        }

        var user = await userManager.GetRequiredUserAsync(dbContext, User);
        var result = await syncService.ApplyAsync(teamId, user.Id, fingerprint, HttpContext.RequestAborted);

        // The plan that comes back is the one actually applied, so the page re-renders against fresh
        // truth rather than the picture the button was clicked on.
        Plan = result.Plan;
        Applied = result;
        return Page();
    }

    public DiscordTagSyncApplyResult? Applied { get; private set; }

    private async Task LoadAsync()
    {
        var user = await userManager.GetRequiredUserAsync(dbContext, User);
        AvailableTeams = await accessScope.GetAvailableTeamsAsync(dbContext, user);
        ResolvedTeamId = adminAccessScope.TryResolveManageableTeamId(user, TeamId, [.. AvailableTeams.Select(t => t.Id)]);

        if (ResolvedTeamId is not { } teamId)
        {
            return;
        }

        ScheduledSyncEnabled = await dbContext.Teams
            .Where(t => t.Id == teamId)
            .Select(t => t.DiscordTagSyncEnabled)
            .FirstOrDefaultAsync(HttpContext.RequestAborted);

        // Read from the job's own history rather than a field of its own: the row is already written
        // for every run, per team, and a second copy would be one more thing to keep in step.
        LastAutomaticRun = await dbContext.JobRunHistories
            .AsNoTracking()
            .Where(h => h.JobName == JobSchedules.DiscordTagSync && h.TeamId == teamId)
            .OrderByDescending(h => h.StartedUtc)
            .FirstOrDefaultAsync(HttpContext.RequestAborted);
    }
}
