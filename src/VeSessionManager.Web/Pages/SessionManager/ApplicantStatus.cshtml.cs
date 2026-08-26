using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Authorization;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Messaging;
using VeSessionManager.Core.Navigation;
using VeSessionManager.Core.Payments;

namespace VeSessionManager.Web.Pages.SessionManager;

/// <summary>
/// "Applicant Status" — team-wide (not per-session) rolling worklist of every candidate who passed
/// but hasn't yet been confirmed Granted by the FCC watcher (Pending), plus a short "Recently
/// issued" section for anyone Granted in the last <see cref="RecentlyIssuedWindowDays"/> days —
/// requested 2026-07-29 so a Session Manager can confirm a given person's license/upgrade actually
/// came through before they age out of Pending entirely. See docs/session-manager-ui.md.
///
/// Deliberately narrow: Pending is Tested + not Failed/NotTested/Granted — the same "already earned
/// a license class this sitting" candidates ExamResultSyncService computes InitialLicenseClass/
/// NewLicenseClass for (see docs/exam-result-license-class.md), so no new backing fields were
/// needed. A candidate drops off Pending the instant UlsWatcherService flips them to Granted; the
/// point of this page is "who's still waiting," not a permanent audit trail — PII purge and the
/// candidate detail page remain the source of truth for anything older.
/// </summary>
[Authorize(Roles = RoleGroups.AllRoles)]
[RemembersFilters]
public class ApplicantStatusModel(
    AppDbContext dbContext,
    UserManager<User> userManager,
    SessionAccessScope accessScope,
    NavBadgeCountService badgeCounts,
    MessageThresholdService thresholds,
    TimeProvider timeProvider) : PageModel
{
    internal const int RecentlyIssuedWindowDays = 7;

    /// <summary>
    /// The days-pending column is a countdown against what this team's own rules actually do, not an
    /// arbitrary UI scale — amber once its FCC-fee reminder is due. Anchored on
    /// ApplicationDateEnteredUtc, the same field this page counts from, so the boundary lines up
    /// exactly.
    ///
    /// <para><b>Read per team, not from a constant (#401 PR2).</b> This was
    /// <c>PaymentReminderService.ReminderThresholdDays</c>, referenced rather than re-declared
    /// precisely so the colour could not drift from the behaviour. Once a team sets its own hours, a
    /// constant *is* the drift: it would show an amber row on a day nothing happens.</para>
    ///
    /// <para><b>A team with no enabled rule gets no colour at all</b>, which is the honest answer:
    /// nothing is going to happen on any particular day, so there is no boundary to warn about. The
    /// page merges teams, so this is resolved per row rather than once.</para>
    ///
    /// <para>There used to be a second, red "critical" tier keyed to the <c>PaymentUnpaid</c>
    /// trigger's hours. Removed 2026-08-25 along with that trigger and the <c>Payment.ExpiredUnpaid</c>
    /// write it coloured for — see <c>PaymentReminderService</c>'s own summary and CLAUDE.md's Known
    /// Constraints ("No fee, no test"). The condition it warned about — this team's own exam fee still
    /// unpaid once an FCC application exists — can't legitimately arise.</para>
    /// </summary>
    private IReadOnlyDictionary<int, int> reminderHoursByTeam = new Dictionary<int, int>();

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
        var user = await userManager.GetRequiredUserAsync(dbContext, User);

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
            .AsNoTracking()
            .Include(c => c.Session)
            .Where(c => teamIds == null || teamIds.Contains(c.Session.TeamId))
            .AwaitingFccGrant()
            // Session date, oldest first (Mike, 2026-08-24). This is a working queue: the session
            // waiting longest is the one to chase, so it belongs at the top.
            //
            // ⚠️ It used to order by the date the FCC received the application, falling back to
            // registration — close enough to look right where everyone sits in a similar window,
            // and wrong exactly where it matters. An application the FCC never received has no
            // received date, so it sorted by registration instead: the candidate nobody has heard
            // anything about, who is the most worth chasing, could land anywhere in the list.
            //
            // Name breaks the tie so a session with several people pending renders in a stable
            // order rather than whatever the database returns; without it the rows can swap
            // places between refreshes and read as though something changed.
            .OrderBy(c => c.Session.ScheduledStartUtc)
            .ThenBy(c => c.Name)
            .ToListAsync();

        // DaysPendingCssClass needs one boolean per candidate — "does this person owe money" — which
        // used to be paid for with Include(c => c.Payments): every payment row of every pending
        // candidate, materialized so that Any() could be called on it. One id query instead.
        var pendingIds = pending.Select(c => c.Id).ToList();
        var candidatesWithUnpaid = (await dbContext.Payments
            .AsNoTracking()
            .Where(p => pendingIds.Contains(p.CandidateId) && p.Status == PaymentStatus.Unpaid)
            .Select(p => p.CandidateId)
            .Distinct()
            .ToListAsync()).ToHashSet();

        // Only the teams actually on screen, so a SystemAdmin viewing one team does not read every
        // team's rules to colour it.
        var pendingTeamIds = pending.Select(c => c.Session.TeamId).Distinct().ToList();
        reminderHoursByTeam = await thresholds.ConfiguredHoursByTeamAsync(
            pendingTeamIds, MessageTrigger.FccFeeOutstanding, HttpContext.RequestAborted);

        Pending = pending.Select(c => ToPendingRow(c, now, candidatesWithUnpaid.Contains(c.Id))).ToList();

        var cutoffUtc = now.AddDays(-RecentlyIssuedWindowDays);
        var recentlyIssued = await dbContext.Candidates
            .AsNoTracking()
            .Include(c => c.Session)
            .Where(c => (teamIds == null || teamIds.Contains(c.Session.TeamId))
                && c.ApplicationStatus == CandidateApplicationStatus.Granted
                && c.LicenseGrantDateUtc != null
                && c.LicenseGrantDateUtc >= cutoffUtc)
            .OrderByDescending(c => c.LicenseGrantDateUtc)
            .ToListAsync();
        RecentlyIssued = recentlyIssued.Select(ToRecentlyIssuedRow).ToList();
    }

    private PendingRow ToPendingRow(Candidate c, DateTime now, bool hasUnpaidPayment)
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
            // Formatted raw, NOT through EasternTimeFormatter like the session date above — and the
            // difference is not stylistic. Every FCC date arrives date-only and is stamped at UTC
            // midnight by ExamToolsUlsLookupClient.AsUtcDate, so it already *is* a wall-clock date;
            // converting it to Eastern renders 8pm the previous day, i.e. every application would
            // read as received a day early. The session date beside it is a real instant, so it
            // must be converted. Same distinction daysPending relies on by comparing .Date.
            c.ApplicationDateEnteredUtc?.ToString("MMM d, yyyy", CultureInfo.InvariantCulture) ?? "—",
            c.ApplicationDateEnteredUtc?.ToString("o", CultureInfo.InvariantCulture) ?? "",
            daysPending,
            DaysPendingCssClass(daysPending, hasUnpaidPayment, c.Session.TeamId),
            FccUlsLinks.License(c.FccUlsLicenseKey));
    }

    /// <summary>Team name for a row, shown only when the picker offers more than one team — see the view.</summary>
    private string TeamNameFor(Candidate c) =>
        AvailableTeams.FirstOrDefault(t => t.Id == c.Session.TeamId).Name ?? "—";

    /// <summary>
    /// Escalates the days-pending cell in step with PaymentReminderService's reminder pass — see the
    /// threshold lookup above.
    ///
    /// <para>**Only escalates while an Unpaid payment actually exists**, because that is the precise
    /// condition the reminder pass requires: no unpaid payment means no reminder will be sent, so an
    /// amber row would be warning about an event that cannot happen. Mirrors the reminder query's own
    /// `Status == PaymentStatus.Unpaid` filter (Paid and NotApplicable both correctly drop out). A
    /// candidate with no payment rows at all — a fee-free session — likewise never escalates.</para>
    ///
    /// <para>Takes the unpaid flag rather than reading candidate.Payments — the page no longer loads
    /// them, see the id query in OnGetAsync — and the team id, because the boundary is that team's own
    /// rule now rather than a constant.</para>
    ///
    /// <para>Compared in hours rather than converting a rule to whole days: a team is free to set 36
    /// hours, and rounding that to a day would put the colour on the wrong side of the boundary for
    /// half of every such rule.</para>
    /// </summary>
    private string DaysPendingCssClass(int? daysPending, bool hasUnpaidPayment, int teamId)
    {
        if (daysPending is not { } days || !hasUnpaidPayment)
        {
            return string.Empty;
        }

        var hoursPending = days * 24;
        return reminderHoursByTeam.TryGetValue(teamId, out var reminderHours) && hoursPending >= reminderHours
            ? "days-warning"
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
    /// <param name="ApplicationReceivedLine">The date FCC entered the application — what "days pending" counts from, so the reader can see the anchor and not just the elapsed number. "—" while still Unmatched, because FCC has nothing on file yet.</param>
    public record PendingRow(int CandidateId, int SessionId, string TeamName, string Name, string Frn, string SessionDateLine, string SessionDateSortValue, string LicenseClassLine, string StatusLabel, string ApplicationReceivedLine, string ApplicationReceivedSortValue, int? DaysPending, string DaysPendingCssClass, string? LicenseUrl);

    public record RecentlyIssuedRow(int CandidateId, int SessionId, string TeamName, string Name, string SessionDateLine, string SessionDateSortValue, string CallSign, string LicenseClassLine, string GrantDateLine, string GrantDateSortValue, string? LicenseUrl);
}
