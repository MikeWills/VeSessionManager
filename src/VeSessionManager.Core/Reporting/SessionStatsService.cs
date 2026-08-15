using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Sessions;
using VeSessionManager.Core.Uls;

namespace VeSessionManager.Core.Reporting;

/// <summary>
/// The stats page's numbers (#63): VE testing activity alongside applicant volume, per team.
///
/// <para><b>Not a breakdown by VEC.</b> The original ask read "both of VEC's testing and applicants",
/// which turned out to be voice-to-text for <b>VE</b> testing — settled 2026-08-15. The
/// <c>Vec</c> reference table is not involved; scoping is the ordinary per-team kind. Recording it
/// because the other reading was plausible and would have shipped a page nobody asked for: every team
/// here runs under ARRL today, so a by-VEC breakdown would render one row and look fine until it
/// did not.</para>
///
/// <para>Separate from <see cref="VolunteerExaminers.VolunteerExaminerReportService"/>, which answers
/// "how much has each person worked" as a leaderboard. This answers "what did we do, and when", over
/// time, for the whole team.</para>
/// </summary>
public class SessionStatsService(AppDbContext dbContext, TimeProvider timeProvider)
{
    /// <summary>
    /// How far back the VE activity panel looks — <b>fixed, and deliberately not the page's date
    /// filter</b>.
    ///
    /// <para>That panel answers "who has been turning up lately", which is a different question from
    /// the rest of the page, and the all-time version of it already exists on the VE Roster screen.
    /// Settled with Mike 2026-08-15. The heading has to say "last 30 days" out loud, because a panel
    /// that ignores the filter above it reads as a bug otherwise.</para>
    /// </summary>
    public static readonly TimeSpan VeActivityWindow = TimeSpan.FromDays(30);

    /// <summary>Rows in the VE activity panel. A leaderboard, not a roster — 176 VEs is the roster, and VeRoster is where it lives.</summary>
    public const int VeActivityTopCount = 10;

    /// <param name="teamIds">Null means every team merged — the convention every scoped read here uses.</param>
    public async Task<SessionStatsReport> GetAsync(
        IReadOnlyList<int>? teamIds, DateTime? fromUtc, DateTime? toUtc, CancellationToken cancellationToken)
    {
        // Only sessions that actually happened. Status is not that signal — it only ever means "not
        // cancelled" — which is the trap CLAUDE.md records and which shipped twice. SessionCompletion
        // is the one definition.
        var sessions = dbContext.Sessions.Where(SessionCompletion.SessionIsCompleted);

        if (teamIds is not null)
        {
            sessions = sessions.Where(s => teamIds.Contains(s.TeamId));
        }

        if (fromUtc is { } from)
        {
            sessions = sessions.Where(s => s.ScheduledStartUtc >= from);
        }

        if (toUtc is { } to)
        {
            sessions = sessions.Where(s => s.ScheduledStartUtc <= to);
        }

        // One query, correlated counts per session, rather than loading candidates. Every figure the
        // page shows is a count, so nothing below needs a candidate row.
        //
        // Passed is "tested, and not Failed, and not withdrawn" rather than a stored flag: there is
        // no Passed status. NotTested is the withdrawal state (see Candidate.IsWithdrawn), so
        // excluding it keeps someone who never sat the exam out of the denominator.
        var rows = await sessions
            .Select(s => new
            {
                s.ScheduledStartUtc,
                Tested = s.Candidates.Count(c => c.Tested),
                Passed = s.Candidates.Count(c => c.Tested
                    && c.ApplicationStatus != CandidateApplicationStatus.Failed
                    && c.ApplicationStatus != CandidateApplicationStatus.NotTested),
                // Gated on Tested exactly like Passed above, so both sides of the rate describe the
                // same population — people who sat an exam. Without it the two are asymmetric and a
                // candidate could land in the denominator while sitting outside "candidates tested",
                // which is a rate over a set the page never shows. Symmetric since 2026-08-15.
                Failed = s.Candidates.Count(c => c.Tested
                    && c.ApplicationStatus == CandidateApplicationStatus.Failed),

                // A license class is only set once a candidate passed something this sitting, so
                // these two together are "people who walked out with a license". Walking in with
                // None (or nothing recorded) makes it a first license; anything else is an upgrade.
                NewLicenses = s.Candidates.Count(c => c.NewLicenseClass != null
                    && (c.InitialLicenseClass == null || c.InitialLicenseClass == LicenseClass.None)),
                Upgrades = s.Candidates.Count(c => c.NewLicenseClass != null
                    && c.InitialLicenseClass != null && c.InitialLicenseClass != LicenseClass.None),

                // The class each candidate walked out holding — first license and upgrade alike, so
                // Technicians + Generals + Extras always equals NewLicenses + Upgrades. Someone who
                // upgraded Technician -> General is counted once, under General: this answers "what
                // licenses did we produce", not "how many people hold each class", which is an FCC
                // question this app has no data for.
                Technicians = s.Candidates.Count(c => c.NewLicenseClass == LicenseClass.Technician),
                Generals = s.Candidates.Count(c => c.NewLicenseClass == LicenseClass.General),
                Extras = s.Candidates.Count(c => c.NewLicenseClass == LicenseClass.Extra)
            })
            .ToListAsync(cancellationToken);

        // Grouped in memory, and deliberately: a month bucket has to be an EASTERN month. 697 of 867
        // stored sessions start between 23:00 and 04:00 UTC — evening ET is simply when volunteer-run
        // sessions happen — so grouping on the UTC month puts a large share of them in the wrong one.
        // Same reasoning as UlsSchedule.ToEasternDate, and the arithmetic is over a few hundred rows.
        var periods = rows
            .GroupBy(r => new
            {
                UlsSchedule.ToEastern(r.ScheduledStartUtc).Year,
                UlsSchedule.ToEastern(r.ScheduledStartUtc).Month
            })
            .Select(g => new StatsPeriod(
                new DateTime(g.Key.Year, g.Key.Month, 1, 0, 0, 0, DateTimeKind.Utc),
                g.Count(),
                g.Sum(r => r.Tested),
                g.Sum(r => r.Passed),
                g.Sum(r => r.Failed),
                g.Sum(r => r.NewLicenses),
                g.Sum(r => r.Upgrades),
                g.Sum(r => r.Technicians),
                g.Sum(r => r.Generals),
                g.Sum(r => r.Extras)))
            .OrderBy(p => p.MonthUtc)
            .ToList();

        // Two separate questions, and they must not share an answer. The panel is a fixed-window top
        // 10; the summary tile counts everyone who worked in the *filtered* range. Deriving the tile
        // from the panel's list — which is what it used to do — would pin it at 10 forever the moment
        // the list was capped.
        var veActivity = await GetVeActivityAsync(teamIds, cancellationToken);
        var activeVeCount = await CountActiveVolunteerExaminersAsync(teamIds, fromUtc, toUtc, cancellationToken);

        return new SessionStatsReport(
            periods,
            rows.Count,
            rows.Sum(r => r.Tested),
            rows.Sum(r => r.Passed),
            rows.Sum(r => r.Failed),
            rows.Sum(r => r.NewLicenses),
            rows.Sum(r => r.Upgrades),
            rows.Sum(r => r.Technicians),
            rows.Sum(r => r.Generals),
            rows.Sum(r => r.Extras),
            // The earliest session actually counted, so the page can say what the numbers cover
            // rather than leaving the reader to assume they cover everything. Computed from the same
            // rows every other figure comes from, so it moves with the team and date filters instead
            // of quietly reporting the deployment's oldest session under every filter.
            rows.Count == 0 ? null : rows.Min(r => r.ScheduledStartUtc),
            activeVeCount,
            veActivity);
    }

    /// <summary>
    /// Who has been turning up lately — the busiest <see cref="VeActivityTopCount"/> VEs over
    /// <see cref="VeActivityWindow"/>.
    ///
    /// <para><b>Deliberately ignores the page's date filter</b>, unlike everything else here. The
    /// all-time, everyone version is the VE Roster screen's job; this is a recent-activity snapshot,
    /// and it needs a heading that says so. See the constants above.</para>
    ///
    /// <para>Counted from roster links on sessions that actually finished, so a VE rostered onto next
    /// week's session is not credited for it — the <c>Status == Active</c> trap CLAUDE.md records,
    /// which has already produced this exact bug twice.</para>
    /// </summary>
    private async Task<IReadOnlyList<VeActivityRow>> GetVeActivityAsync(
        IReadOnlyList<int>? teamIds, CancellationToken cancellationToken)
    {
        var since = timeProvider.GetUtcNow().UtcDateTime - VeActivityWindow;

        var links = dbContext.SessionVolunteerExaminers
            .Where(SessionCompletion.RosterLinkIsCompleted)
            .Where(sve => sve.Session.ScheduledStartUtc >= since);

        if (teamIds is not null)
        {
            links = links.Where(sve => teamIds.Contains(sve.Session.TeamId));
        }

        var counts = await links
            .GroupBy(sve => new { sve.VolunteerExaminerId, sve.VolunteerExaminer.Name, sve.VolunteerExaminer.CallSign })
            .Select(g => new { g.Key.VolunteerExaminerId, g.Key.Name, g.Key.CallSign, Sessions = g.Count() })
            .ToListAsync(cancellationToken);

        // Ordered and truncated in memory: EF InMemory cannot translate an OrderBy chained onto a
        // GroupBy/Select projection (CLAUDE.md's Known Constraint, hit building
        // VolunteerExaminerReportService). Thirty days of roster links is a small set regardless.
        return [.. counts
            .OrderByDescending(c => c.Sessions)
            .ThenBy(c => c.Name)
            .Take(VeActivityTopCount)
            .Select(c => new VeActivityRow(c.VolunteerExaminerId, c.Name, c.CallSign, c.Sessions))];
    }

    /// <summary>
    /// How many distinct VEs worked at least one completed session in the <b>filtered</b> range —
    /// the summary tile, which belongs with the other range-scoped figures beside it.
    ///
    /// <para>Its own query rather than <c>VolunteerExaminers.Count</c> precisely because that list is
    /// now a fixed-window top 10: deriving the tile from it would report "10" for every team and
    /// every range, and look entirely plausible doing it.</para>
    /// </summary>
    private async Task<int> CountActiveVolunteerExaminersAsync(
        IReadOnlyList<int>? teamIds, DateTime? fromUtc, DateTime? toUtc, CancellationToken cancellationToken)
    {
        var links = dbContext.SessionVolunteerExaminers.Where(SessionCompletion.RosterLinkIsCompleted);

        if (teamIds is not null)
        {
            links = links.Where(sve => teamIds.Contains(sve.Session.TeamId));
        }

        if (fromUtc is { } from)
        {
            links = links.Where(sve => sve.Session.ScheduledStartUtc >= from);
        }

        if (toUtc is { } to)
        {
            links = links.Where(sve => sve.Session.ScheduledStartUtc <= to);
        }

        return await links.Select(sve => sve.VolunteerExaminerId).Distinct().CountAsync(cancellationToken);
    }
}

/// <param name="MonthUtc">First of the month, as an Eastern calendar month — see the grouping remarks.</param>
/// <param name="Technicians">Walked out holding Technician — first licenses and upgrades together, so these three always sum to NewLicenses + Upgrades.</param>
public record StatsPeriod(
    DateTime MonthUtc, int Sessions, int CandidatesTested, int Passed, int Failed, int NewLicenses, int Upgrades,
    int Technicians, int Generals, int Extras);

public record VeActivityRow(int VolunteerExaminerId, string Name, string? CallSign, int SessionsWorked);

/// <param name="Passed">Tested, not Failed, and not withdrawn. There is no stored "passed" flag.</param>
public record SessionStatsReport(
    IReadOnlyList<StatsPeriod> Periods,
    int TotalSessions,
    int TotalCandidatesTested,
    int TotalPassed,
    int TotalFailed,
    int TotalNewLicenses,
    int TotalUpgrades,
    int TotalTechnicians,
    int TotalGenerals,
    int TotalExtras,
    DateTime? EarliestSessionUtc,
    int ActiveVolunteerExaminers,
    /// <summary>The busiest few VEs over a fixed recent window — NOT the filtered range, and not everyone. See SessionStatsService.VeActivityWindow.</summary>
    IReadOnlyList<VeActivityRow> VolunteerExaminers)
{
    /// <summary>
    /// Licenses earned in range, by the class the candidate walked out holding.
    ///
    /// <para><b>Reads low for anything before 2026 and that is a data gap, not a quiet year.</b> The
    /// historical import never fetched graded exam elements — <c>ExamResultSyncService</c> only scans
    /// sessions started within <c>ResultSyncWindow</c> (14 days), and every imported session was
    /// already outside it — so ~1,699 candidates carry no <c>NewLicenseClass</c> at all. The rows are
    /// intact and still hold their <c>ExamToolsApplicantId</c>; the results just have not been pulled
    /// yet. Until they are, treat this as "licenses we have results for", not "licenses issued".</para>
    /// </summary>
    public int TotalLicensesEarned => TotalTechnicians + TotalGenerals + TotalExtras;

    /// <summary>
    /// Of the candidates who sat an exam: passed over passed-plus-failed.
    ///
    /// <para><b>A tested candidate who is not recorded as failed counts as a pass immediately</b> —
    /// including while <c>Unmatched</c> or <c>Received</c>, i.e. still waiting on the FCC. That is
    /// deliberate: whether someone passed the exam is settled on the day, and the FCC grant is a
    /// downstream administrative step, not a second verdict. Waiting for <c>Granted</c> would make a
    /// recent session's rate climb by itself for a fortnight.</para>
    ///
    /// <para><i>This comment previously claimed the opposite — that candidates awaiting an FCC
    /// outcome were excluded from the calculation. They never were. Corrected 2026-08-15; the code
    /// was right and the comment had been describing a different design since #63 shipped.</i></para>
    /// </summary>
    public double? PassRate => TotalPassed + TotalFailed == 0
        ? null
        : (double)TotalPassed / (TotalPassed + TotalFailed);
}
