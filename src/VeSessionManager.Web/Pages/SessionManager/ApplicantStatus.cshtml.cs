using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Authorization;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Web.Pages.SessionManager;

/// <summary>
/// "Applicant Status" — team-wide (not per-session) rolling worklist of every candidate who passed
/// but hasn't yet been confirmed Granted by the FCC watcher (Pending), plus a short "Recently
/// issued" section for anyone Granted in the last <see cref="RecentlyIssuedWindowDays"/> days —
/// requested 2026-07-29 so a Session Manager can confirm a given person's license/upgrade actually
/// came through before they age out of Pending entirely. See TODO.md's "Feature requests" entry.
///
/// Deliberately narrow: Pending is Tested + not Failed/NotTested/Granted — the same "already earned
/// a license class this sitting" candidates ExamResultSyncService computes InitialLicenseClass/
/// NewLicenseClass for (see docs/exam-result-license-class.md), so no new backing fields were
/// needed. A candidate drops off Pending the instant FccUlsWatcherService flips them to Granted; the
/// point of this page is "who's still waiting," not a permanent audit trail — PII purge and the
/// candidate detail page remain the source of truth for anything older.
/// </summary>
[Authorize(Roles = "SystemAdmin,TeamAdmin,SessionManager,TeamLead")]
public class ApplicantStatusModel(AppDbContext dbContext, UserManager<User> userManager, SessionAccessScope accessScope, TimeProvider timeProvider) : PageModel
{
    internal const int RecentlyIssuedWindowDays = 7;

    [BindProperty(SupportsGet = true)]
    public int? TeamId { get; set; }

    public bool HasTeamContext { get; private set; }
    public IReadOnlyList<(int Id, string Name)> AvailableTeams { get; private set; } = [];
    public IReadOnlyList<PendingRow> Pending { get; private set; } = [];
    public IReadOnlyList<RecentlyIssuedRow> RecentlyIssued { get; private set; } = [];

    public async Task OnGetAsync()
    {
        var user = await userManager.GetUserWithManagerAsync(dbContext, User) ?? throw new InvalidOperationException("No authenticated user for an [Authorize]d page.");

        AvailableTeams = await accessScope.GetAvailableTeamsAsync(dbContext, user);

        var teamId = accessScope.TryResolveViewableTeamId(user, TeamId, AvailableTeams);
        TeamId = teamId;
        HasTeamContext = teamId is not null;

        if (teamId is not int id)
        {
            return;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;

        var pending = await dbContext.Candidates
            .Include(c => c.Session)
            .Where(c => c.Session.TeamId == id
                && c.Tested
                && (c.ApplicationStatus == CandidateApplicationStatus.Unmatched || c.ApplicationStatus == CandidateApplicationStatus.Received))
            .OrderBy(c => c.ApplicationDateEnteredUtc ?? c.DateRegisteredUtc)
            .ToListAsync();
        Pending = pending.Select(c => ToPendingRow(c, now)).ToList();

        var cutoffUtc = now.AddDays(-RecentlyIssuedWindowDays);
        var recentlyIssued = await dbContext.Candidates
            .Include(c => c.Session)
            .Where(c => c.Session.TeamId == id
                && c.ApplicationStatus == CandidateApplicationStatus.Granted
                && c.LicenseGrantDateUtc != null
                && c.LicenseGrantDateUtc >= cutoffUtc)
            .OrderByDescending(c => c.LicenseGrantDateUtc)
            .ToListAsync();
        RecentlyIssued = recentlyIssued.Select(ToRecentlyIssuedRow).ToList();
    }

    private static PendingRow ToPendingRow(Candidate c, DateTime now)
    {
        var anchor = c.ApplicationDateEnteredUtc ?? c.DateRegisteredUtc;
        var daysPending = (int)(now.Date - anchor.Date).TotalDays;

        return new PendingRow(
            c.Id,
            c.Session.Id,
            c.Name ?? "—",
            c.Frn ?? "—",
            c.Session.ScheduledStartUtc.ToString("MMM d, yyyy", CultureInfo.InvariantCulture),
            LicenseClassFormatter.FormatTransition(c.InitialLicenseClass, c.NewLicenseClass) ?? "—",
            c.ApplicationStatus == CandidateApplicationStatus.Received ? "Received" : "Awaiting FCC match",
            daysPending);
    }

    private static RecentlyIssuedRow ToRecentlyIssuedRow(Candidate c) =>
        new(
            c.Id,
            c.Session.Id,
            c.Name ?? "—",
            c.Session.ScheduledStartUtc.ToString("MMM d, yyyy", CultureInfo.InvariantCulture),
            c.CallSign ?? "—",
            LicenseClassFormatter.FormatTransition(c.InitialLicenseClass, c.NewLicenseClass) ?? "—",
            c.LicenseGrantDateUtc!.Value.ToString("MMM d, yyyy", CultureInfo.InvariantCulture));

    public record PendingRow(int CandidateId, int SessionId, string Name, string Frn, string SessionDateLine, string LicenseClassLine, string StatusLabel, int DaysPending);

    public record RecentlyIssuedRow(int CandidateId, int SessionId, string Name, string SessionDateLine, string CallSign, string LicenseClassLine, string GrantDateLine);
}
