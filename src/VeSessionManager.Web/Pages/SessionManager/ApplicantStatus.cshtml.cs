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
        // Falls back to the session date, not DateRegisteredUtc — a candidate can register for an
        // exam days before actually taking it, and FCC has nothing to process until the exam itself
        // happens. Using DateRegisteredUtc here made an Unmatched candidate whose session was
        // literally today show several "days pending" already, counting time before the exam ever
        // occurred. ApplicationDateEnteredUtc (once Received) remains the accurate anchor — that's
        // FCC's own Last Action Date on the matched application.
        var anchor = c.ApplicationDateEnteredUtc ?? c.Session.ScheduledStartUtc;
        var daysPending = Math.Max(0, (int)(now.Date - anchor.Date).TotalDays);

        return new PendingRow(
            c.Id,
            c.Session.Id,
            c.Name ?? "—",
            c.Frn ?? "—",
            EasternTimeFormatter.Format(c.Session.ScheduledStartUtc, "MMM d, yyyy"),
            LicenseClassFormatter.FormatTransition(c.InitialLicenseClass, c.NewLicenseClass) ?? "—",
            FccStatusLabel(c),
            FccFeeLabel(c),
            daysPending);
    }

    /// <summary>
    /// The real FCC-side status, not just "did our own matching find it yet": Unmatched means FCC
    /// has no application on file at all — still with the VEC, not FCC's problem to report on — so
    /// "VEC Processing" rather than an internal-sounding "Awaiting FCC match". Once Received, FCC has
    /// it; FccHoldReason (from FCC's own HS.dat history codes, refreshed every watcher run — see
    /// FccUlsWatcherService) reports whether it's currently held for Red Light (usually just an
    /// unpaid-fee window, not itself a problem) or Basic Qualification (character) review, straight
    /// from FCC rather than a proxy like Candidate.HasFelonyDisclosure.
    /// </summary>
    private static string FccStatusLabel(Candidate c) =>
        c.ApplicationStatus == CandidateApplicationStatus.Unmatched
            ? "VEC Processing"
            : c.FccHoldReason switch
            {
                FccApplicationHoldReason.RedLight => "Held — Red Light",
                FccApplicationHoldReason.BasicQualification => "Held — Basic Qualification",
                FccApplicationHoldReason.RedLightAndBasicQualification => "Held — Red Light + Basic Qualification",
                _ => "Application Received/Processing"
            };

    /// <summary>Separate from FccStatusLabel — FccPaymentStatus (also from HS.dat) answers the fee question specifically, since a candidate can be Application Received/Processing with the fee either confirmed or still unverified.</summary>
    private static string FccFeeLabel(Candidate c) =>
        c.ApplicationStatus == CandidateApplicationStatus.Unmatched
            ? "—"
            : c.FccPaymentStatus switch
            {
                FccApplicationPaymentStatus.Paid => "Paid",
                FccApplicationPaymentStatus.PendingVerification => "Pending",
                _ => "—"
            };

    private static RecentlyIssuedRow ToRecentlyIssuedRow(Candidate c) =>
        new(
            c.Id,
            c.Session.Id,
            c.Name ?? "—",
            EasternTimeFormatter.Format(c.Session.ScheduledStartUtc, "MMM d, yyyy"),
            c.CallSign ?? "—",
            LicenseClassFormatter.FormatTransition(c.InitialLicenseClass, c.NewLicenseClass) ?? "—",
            // Date-only FCC field (see ToPendingRow's anchor comment / EasternTimeFormatter's own
            // doc remarks) — not run through EasternTimeFormatter, same reasoning as
            // CandidateDetail.cshtml.cs's LicenseGrantDateLine.
            c.LicenseGrantDateUtc!.Value.ToString("MMM d, yyyy", CultureInfo.InvariantCulture));

    public record PendingRow(int CandidateId, int SessionId, string Name, string Frn, string SessionDateLine, string LicenseClassLine, string StatusLabel, string FeeLabel, int DaysPending);

    public record RecentlyIssuedRow(int CandidateId, int SessionId, string Name, string SessionDateLine, string CallSign, string LicenseClassLine, string GrantDateLine);
}
