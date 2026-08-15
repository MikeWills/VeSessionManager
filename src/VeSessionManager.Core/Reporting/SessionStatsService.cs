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
public class SessionStatsService(AppDbContext dbContext)
{
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
                Failed = s.Candidates.Count(c => c.ApplicationStatus == CandidateApplicationStatus.Failed),

                // A license class is only set once a candidate passed something this sitting, so
                // these two together are "people who walked out with a license". Walking in with
                // None (or nothing recorded) makes it a first license; anything else is an upgrade.
                NewLicenses = s.Candidates.Count(c => c.NewLicenseClass != null
                    && (c.InitialLicenseClass == null || c.InitialLicenseClass == LicenseClass.None)),
                Upgrades = s.Candidates.Count(c => c.NewLicenseClass != null
                    && c.InitialLicenseClass != null && c.InitialLicenseClass != LicenseClass.None)
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
                g.Sum(r => r.Upgrades)))
            .OrderBy(p => p.MonthUtc)
            .ToList();

        var veActivity = await GetVeActivityAsync(teamIds, fromUtc, toUtc, cancellationToken);

        return new SessionStatsReport(
            periods,
            rows.Count,
            rows.Sum(r => r.Tested),
            rows.Sum(r => r.Passed),
            rows.Sum(r => r.Failed),
            rows.Sum(r => r.NewLicenses),
            rows.Sum(r => r.Upgrades),
            veActivity);
    }

    /// <summary>
    /// The VE half of the title — who actually worked, and how much.
    ///
    /// <para>Counted from the roster links on completed sessions, scoped the same way, so a VE's
    /// number here always agrees with the range and teams the rest of the page is showing.</para>
    /// </summary>
    private async Task<IReadOnlyList<VeActivityRow>> GetVeActivityAsync(
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

        var counts = await links
            .GroupBy(sve => new { sve.VolunteerExaminerId, sve.VolunteerExaminer.Name, sve.VolunteerExaminer.CallSign })
            .Select(g => new { g.Key.VolunteerExaminerId, g.Key.Name, g.Key.CallSign, Sessions = g.Count() })
            .ToListAsync(cancellationToken);

        // Ordered in memory: EF InMemory cannot translate an OrderBy chained onto a GroupBy/Select
        // projection (CLAUDE.md's Known Constraint, hit building VolunteerExaminerReportService).
        return [.. counts
            .OrderByDescending(c => c.Sessions)
            .ThenBy(c => c.Name)
            .Select(c => new VeActivityRow(c.VolunteerExaminerId, c.Name, c.CallSign, c.Sessions))];
    }
}

/// <param name="MonthUtc">First of the month, as an Eastern calendar month — see the grouping remarks.</param>
public record StatsPeriod(
    DateTime MonthUtc, int Sessions, int CandidatesTested, int Passed, int Failed, int NewLicenses, int Upgrades);

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
    IReadOnlyList<VeActivityRow> VolunteerExaminers)
{
    /// <summary>
    /// Of those whose result is known. Deliberately excludes candidates still awaiting an FCC
    /// outcome, rather than counting them as failures — a session run last week would otherwise
    /// report a pass rate that climbs for a fortnight afterwards.
    /// </summary>
    public double? PassRate => TotalPassed + TotalFailed == 0
        ? null
        : (double)TotalPassed / (TotalPassed + TotalFailed);

    /// <summary>How many distinct VEs worked at least one session in range.</summary>
    public int ActiveVolunteerExaminers => VolunteerExaminers.Count;
}
