using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Authorization;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Ingestion;

namespace VeSessionManager.Web.Pages.Admin;

/// <summary>
/// Issues #77 and #73: the operational half of team administration, deliberately separate from
/// Team Settings (which is credentials/configuration). Three things that only make sense together —
/// when this team was last polled and when it's next due, a button to poll it right now, and a
/// one-off historical import.
///
/// The gap this closes: ManualCandidateRefreshService's only trigger used to be the "Refresh
/// candidates" button on a session's Detail page, which fails exactly when it is most needed — a
/// team with no ingested sessions has no session page, so there was no way to trigger ingestion
/// from the UI at all. Hit live 2026-07-31 (WX0MIK had 0 sessions locally while two existed
/// upstream); the only recourse was setting Team.LastIngestionRunUtc = NULL by hand in the database.
///
/// Team-picker/authorization shape mirrors TeamSettings.cshtml.cs exactly: SystemAdmin picks,
/// TeamAdmin is locked to their own team regardless of a tampered ?teamId=, and the POST handlers
/// re-resolve through AdminAccessScope rather than trusting the form.
/// </summary>
[Authorize(Roles = "SystemAdmin,TeamAdmin")]
public class TeamMaintenanceModel(
    AppDbContext dbContext,
    UserManager<User> userManager,
    AdminAccessScope adminAccessScope,
    IngestionStatusService ingestionStatusService,
    TeamRefreshThrottle refreshThrottle,
    ManualCandidateRefreshService manualRefreshService,
    HistoricalImportService historicalImportService) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public int? TeamId { get; set; }

    public bool IsSystemAdmin { get; private set; }

    /// <summary>"Select a team…" rather than "All teams" — every action here targets one team, so there is no merged view to fall back to (same reasoning as TeamSettings).</summary>
    public string TeamSummaryLabel { get; private set; } = "Select a team…";

    public IReadOnlyList<(int Id, string Name)> AvailableTeams { get; private set; } = [];
    public Team? Team { get; private set; }

    /// <summary>This team's row only. The deployment-wide health signal comes from the same report — see IngestionStatusService.</summary>
    public TeamIngestionStatus? Status { get; private set; }
    public IngestionStatusReport? Report { get; private set; }

    /// <summary>Non-null when a refresh ran too recently; the view disables the button and says how long to wait.</summary>
    public int? RefreshBlockedForSeconds { get; private set; }

    /// <summary>This team's import history, newest first — the in-flight one (if any) is the first row.</summary>
    public IReadOnlyList<HistoricalImportRequest> ImportRequests { get; private set; } = [];

    /// <summary>True while a Pending/Running import exists for this team, which is what blocks queueing another.</summary>
    public bool ImportInFlight => ImportRequests.Any(r => r.Status is HistoricalImportStatus.Pending or HistoricalImportStatus.Running);

    /// <summary>Defaults for the import form: January 1 of the current year through yesterday — the motivating case from issue #67 ("a full year for the stats page").</summary>
    public DateOnly DefaultImportStart { get; private set; }
    public DateOnly DefaultImportEnd { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await userManager.GetUserWithManagerAsync(dbContext, User);
        if (user is null)
        {
            return Forbid();
        }

        await LoadTeamPickerAsync(user);

        var effectiveTeamId = adminAccessScope.TryResolveManageableTeamId(user, TeamId);
        if (effectiveTeamId is null)
        {
            // SystemAdmin hasn't picked yet, or a TeamAdmin has no team — benign empty state, not a
            // permission failure. Same call as TeamSettings makes.
            return Page();
        }

        Team = await dbContext.Teams.FirstOrDefaultAsync(t => t.Id == effectiveTeamId.Value);
        if (Team is null)
        {
            return Page();
        }

        TeamId = Team.Id;
        TeamSummaryLabel = Team.Name;
        Report = await ingestionStatusService.GetAsync([Team.Id], CancellationToken.None);
        Status = Report.Teams.FirstOrDefault();
        RefreshBlockedForSeconds = await refreshThrottle.SecondsUntilAllowedAsync(Team.Id, CancellationToken.None);

        ImportRequests = await dbContext.HistoricalImportRequests
            .Where(r => r.TeamId == Team.Id)
            .OrderByDescending(r => r.RequestedUtc)
            .Take(10)
            .ToListAsync();

        var today = DateOnly.FromDateTime(Report.NowUtc);
        DefaultImportStart = new DateOnly(today.Year, 1, 1);
        DefaultImportEnd = today.AddDays(-1);
        return Page();
    }

    public async Task<IActionResult> OnPostQueueImportAsync(DateOnly startDate, DateOnly endDate)
    {
        var auth = await AuthorizeAsync();
        if (auth is null) return Forbid();

        var team = auth.Value.Team;
        var result = await historicalImportService.QueueAsync(
            team.Id, startDate, endDate, auth.Value.User.Id, CancellationToken.None);

        TempData[result == HistoricalImportQueueResult.Queued ? "StatusMessage" : "ErrorMessage"] = result switch
        {
            HistoricalImportQueueResult.Queued =>
                $"Import queued for {team.Name}, {startDate:MMM d, yyyy} to {endDate:MMM d, yyyy}. The Worker picks it up within a minute — progress appears below.",
            HistoricalImportQueueResult.AlreadyRunning =>
                "This team already has an import queued or running — wait for it to finish before starting another.",
            _ => "That date range isn't valid: the end date must be on or after the start date, and the start date can't be in the future."
        };

        return RedirectToPage(new { teamId = team.Id });
    }

    /// <summary>
    /// The team-level entry point to the pipeline (issue #77) — the same
    /// ManualCandidateRefreshService.RunAsync the per-session button calls, with no new pipeline
    /// logic, just a second way in that doesn't require already having a session.
    ///
    /// Deliberately does NOT touch Team.LastIngestionRunUtc: a manual run is extra work on top of
    /// the schedule, not a substitute for it, so pressing this must not push the next scheduled poll
    /// an hour further out. That was already the behaviour — it is now a decision rather than an
    /// accident (see the field's own comment).
    /// </summary>
    public async Task<IActionResult> OnPostRefreshNowAsync()
    {
        var auth = await AuthorizeAsync();
        if (auth is null) return Forbid();

        var team = auth.Value.Team;

        if (!team.IsExamToolsConfigured)
        {
            TempData["ErrorMessage"] = "This team has no ExamTools credentials yet — set them on Team Settings first, and the next poll will pick the team up automatically.";
            return RedirectToPage(new { teamId = team.Id });
        }

        var blockedFor = await refreshThrottle.SecondsUntilAllowedAsync(team.Id, CancellationToken.None);
        if (blockedFor is not null)
        {
            TempData["ErrorMessage"] = $"A refresh for this team just ran — try again in {blockedFor} second(s).";
            return RedirectToPage(new { teamId = team.Id });
        }

        var result = await manualRefreshService.RunAsync(team, CancellationToken.None);
        TempData["StatusMessage"] =
            $"Refreshed {team.Name} — {result.CandidatesAdded} new candidate(s), {result.CandidatesUpdated} updated, {result.ConfirmationEmailsSent} confirmation email(s) sent.";
        return RedirectToPage(new { teamId = team.Id });
    }

    private async Task LoadTeamPickerAsync(User user)
    {
        IsSystemAdmin = user.Role == UserRole.SystemAdmin;
        AvailableTeams = await adminAccessScope.ScopeTeams(dbContext.Teams, user)
            .OrderBy(t => t.Name).Select(t => new ValueTuple<int, string>(t.Id, t.Name)).ToListAsync();
    }

    private async Task<(User User, Team Team)?> AuthorizeAsync()
    {
        var user = await userManager.GetUserWithManagerAsync(dbContext, User);
        if (user is null)
        {
            return null;
        }

        await LoadTeamPickerAsync(user);

        var effectiveTeamId = adminAccessScope.TryResolveManageableTeamId(user, TeamId);
        if (effectiveTeamId is null)
        {
            return null;
        }

        var team = await dbContext.Teams.FirstOrDefaultAsync(t => t.Id == effectiveTeamId.Value);
        return team is null ? null : (user, team);
    }
}
