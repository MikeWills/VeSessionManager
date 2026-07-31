using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Authorization;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Navigation;
using VeSessionManager.Core.Payments;

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
/// needed. A candidate drops off Pending the instant UlsWatcherService flips them to Granted; the
/// point of this page is "who's still waiting," not a permanent audit trail — PII purge and the
/// candidate detail page remain the source of truth for anything older.
/// </summary>
[Authorize(Roles = "SystemAdmin,TeamAdmin,SessionManager,TeamLead")]
public class ApplicantStatusModel(
    AppDbContext dbContext,
    UserManager<User> userManager,
    SessionAccessScope accessScope,
    NavBadgeCountService badgeCounts,
    TimeProvider timeProvider) : PageModel
{
    internal const int RecentlyIssuedWindowDays = 7;

    /// <summary>
    /// The days-pending column is a countdown against PaymentReminderService's own two passes, not
    /// an arbitrary UI scale — both are anchored on ApplicationDateEnteredUtc, the same field this
    /// page counts from, so the boundaries line up exactly:
    ///
    ///   - <see cref="PaymentReminderService.ReminderThresholdDays"/> (5): the nightly job sends the
    ///     candidate a PaymentReminder5Day email.
    ///   - <see cref="PaymentReminderService.ExpirationThresholdDays"/> (10): the nightly job sets
    ///     Payment.ExpiredUnpaid and notifies the Session Manager.
    ///
    /// Deliberately referenced rather than re-declared — a local copy would drift and start
    /// colouring rows on days when nothing actually happens.
    /// </summary>
    internal const int DaysPendingWarningThreshold = PaymentReminderService.ReminderThresholdDays;
    internal const int DaysPendingCriticalThreshold = PaymentReminderService.ExpirationThresholdDays;

    [BindProperty(SupportsGet = true)]
    public int? TeamId { get; set; }

    /// <summary>False only when the account belongs to no team at all — a null TeamId now means "all teams merged", not "no context" (2026-07-30, matching the session list).</summary>
    public bool HasTeamContext { get; private set; }
    public IReadOnlyList<(int Id, string Name)> AvailableTeams { get; private set; } = [];

    /// <summary>Label for the team-picker trigger, same shape as the session list's.</summary>
    public string TeamSummaryLabel { get; private set; } = "All teams";

    /// <summary>
    /// Pending-FCC-grant count per team, for the team picker — so a multi-team user can see which
    /// team actually has work waiting without clicking through each pill. Uses the same predicate
    /// as the Pending table below (both come from NavBadgeCountService), so a pill's number always
    /// equals the row count you get after clicking it.
    /// </summary>
    public IReadOnlyDictionary<int, int> PendingCountsByTeam { get; private set; } = new Dictionary<int, int>();
    public IReadOnlyList<PendingRow> Pending { get; private set; } = [];
    public IReadOnlyList<RecentlyIssuedRow> RecentlyIssued { get; private set; } = [];

    public async Task OnGetAsync()
    {
        var user = await userManager.GetUserWithManagerAsync(dbContext, User) ?? throw new InvalidOperationException("No authenticated user for an [Authorize]d page.");

        AvailableTeams = await accessScope.GetAvailableTeamsAsync(dbContext, user);

        // Only worth querying when a picker will actually render (see the Count > 1 guard in the view).
        if (AvailableTeams.Count > 1)
        {
            PendingCountsByTeam = await badgeCounts.GetApplicantsPendingGrantByTeamAsync(
                [.. AvailableTeams.Select(t => t.Id)], HttpContext.RequestAborted);
        }

        // null TeamId == every team this user can see, merged — same convention as the session
        // list. Only an account with no teams at all has nothing to render.
        var teamIds = accessScope.ResolveViewableTeamIds(user, TeamId);
        HasTeamContext = teamIds is null || teamIds.Count > 0;
        TeamSummaryLabel = TeamId is not null
            ? AvailableTeams.FirstOrDefault(t => t.Id == TeamId).Name ?? "All teams"
            : "All teams";

        if (!HasTeamContext)
        {
            return;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;

        var pending = await dbContext.Candidates
            .Include(c => c.Session)
            .Include(c => c.Payments)   // needed by DaysPendingCssClass's unpaid-payment gate
            .Where(c => (teamIds == null || teamIds.Contains(c.Session.TeamId))
                && c.Tested
                && (c.ApplicationStatus == CandidateApplicationStatus.Unmatched || c.ApplicationStatus == CandidateApplicationStatus.Received))
            .OrderBy(c => c.ApplicationDateEnteredUtc ?? c.DateRegisteredUtc)
            .ToListAsync();
        Pending = pending.Select(c => ToPendingRow(c, now)).ToList();

        var cutoffUtc = now.AddDays(-RecentlyIssuedWindowDays);
        var recentlyIssued = await dbContext.Candidates
            .Include(c => c.Session)
            .Where(c => (teamIds == null || teamIds.Contains(c.Session.TeamId))
                && c.ApplicationStatus == CandidateApplicationStatus.Granted
                && c.LicenseGrantDateUtc != null
                && c.LicenseGrantDateUtc >= cutoffUtc)
            .OrderByDescending(c => c.LicenseGrantDateUtc)
            .ToListAsync();
        RecentlyIssued = recentlyIssued.Select(ToRecentlyIssuedRow).ToList();
    }

    private PendingRow ToPendingRow(Candidate c, DateTime now)
    {
        // Counts only from ApplicationDateEnteredUtc — FCC's own Last Action Date on the matched
        // application, i.e. the moment FCC actually had something to work on. Null (still Unmatched /
        // "VEC Processing") means FCC has no application on file yet, so there is no FCC clock to
        // report and the column shows "—" rather than a number.
        //
        // Two earlier anchors were both wrong for the same underlying reason — they measured time
        // FCC wasn't responsible for. DateRegisteredUtc counted from sign-up, so a candidate whose
        // session was literally today already showed several days pending. Falling back to
        // Session.ScheduledStartUtc fixed that case but still started the clock at the exam, which
        // runs during the VEC's own processing window before FCC ever receives the paperwork.
        int? daysPending = c.ApplicationDateEnteredUtc is { } enteredUtc
            ? Math.Max(0, (int)(now.Date - enteredUtc.Date).TotalDays)
            : null;

        return new PendingRow(
            c.Id,
            c.Session.Id,
            TeamNameFor(c),
            c.Name ?? "—",
            c.Frn ?? "—",
            EasternTimeFormatter.Format(c.Session.ScheduledStartUtc, "MMM d, yyyy"),
            c.Session.ScheduledStartUtc.ToString("o", CultureInfo.InvariantCulture),
            LicenseClassFormatter.FormatTransition(c.InitialLicenseClass, c.NewLicenseClass) ?? "—",
            FccStatusLabel(c),
            FccFeeLabel(c),
            daysPending,
            DaysPendingCssClass(daysPending, c),
            FccUlsLinks.License(c.FccUlsLicenseKey));
    }

    /// <summary>
    /// Escalates the days-pending cell in step with PaymentReminderService's reminder/expiration
    /// passes — see the threshold constants.
    ///
    /// <para>**Only escalates while an Unpaid payment actually exists**, because that is the precise
    /// condition both of those passes require: no unpaid payment means no reminder will be sent and
    /// nothing will ever be marked ExpiredUnpaid, so a red row would be warning about an event that
    /// cannot happen. Mirrors the reminder query's own `Status == PaymentStatus.Unpaid` filter
    /// (Paid and NotApplicable both correctly drop out). A candidate with no payment rows at all —
    /// a fee-free session — likewise never escalates.</para>
    /// </summary>
    /// <summary>Team name for a row, shown only when the picker offers more than one team — see the view.</summary>
    private string TeamNameFor(Candidate c) =>
        AvailableTeams.FirstOrDefault(t => t.Id == c.Session.TeamId).Name ?? "—";

    private static string DaysPendingCssClass(int? daysPending, Candidate candidate)
    {
        var hasUnpaidPayment = candidate.Payments.Any(p => p.Status == PaymentStatus.Unpaid);
        if (daysPending is not { } days || !hasUnpaidPayment)
        {
            return string.Empty;
        }

        return days >= DaysPendingCriticalThreshold ? "days-critical"
            : days >= DaysPendingWarningThreshold ? "days-warning"
            : string.Empty;
    }

    /// <summary>
    /// The real FCC-side status, not just "did our own matching find it yet": Unmatched means FCC
    /// has no application on file at all — still with the VEC, not FCC's problem to report on — so
    /// "VEC Processing" rather than an internal-sounding "Awaiting FCC match". Once Received, FCC has
    /// it; FccHoldReason (from FCC's own HS.dat history codes, refreshed every watcher run — see
    /// UlsWatcherService) reports whether it's currently held for Red Light (usually just an
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

    private RecentlyIssuedRow ToRecentlyIssuedRow(Candidate c) =>
        new(
            c.Id,
            c.Session.Id,
            TeamNameFor(c),
            c.Name ?? "—",
            EasternTimeFormatter.Format(c.Session.ScheduledStartUtc, "MMM d, yyyy"),
            c.Session.ScheduledStartUtc.ToString("o", CultureInfo.InvariantCulture),
            c.CallSign ?? "—",
            LicenseClassFormatter.FormatTransition(c.InitialLicenseClass, c.NewLicenseClass) ?? "—",
            // Date-only FCC field (see ToPendingRow's anchor comment / EasternTimeFormatter's own
            // doc remarks) — not run through EasternTimeFormatter, same reasoning as
            // CandidateDetail.cshtml.cs's LicenseGrantDateLine.
            c.LicenseGrantDateUtc!.Value.ToString("MMM d, yyyy", CultureInfo.InvariantCulture),
            c.LicenseGrantDateUtc!.Value.ToString("o", CultureInfo.InvariantCulture),
            FccUlsLinks.License(c.FccUlsLicenseKey));

    // The ...SortValue members carry the raw date behind each formatted *Line, for the table's
    // click-to-sort headers (see app.js). "MMM d, yyyy" sorts alphabetically as text — Apr before
    // Mar — so a date column has to sort on something round-trippable instead of what it displays.
    public record PendingRow(int CandidateId, int SessionId, string TeamName, string Name, string Frn, string SessionDateLine, string SessionDateSortValue, string LicenseClassLine, string StatusLabel, string FeeLabel, int? DaysPending, string DaysPendingCssClass, string? LicenseUrl);

    public record RecentlyIssuedRow(int CandidateId, int SessionId, string TeamName, string Name, string SessionDateLine, string SessionDateSortValue, string CallSign, string LicenseClassLine, string GrantDateLine, string GrantDateSortValue, string? LicenseUrl);
}
