using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Uls;

namespace VeSessionManager.Core.VolunteerExaminers;

/// <summary>
/// Phase 7's "simple report: session count per VE, filterable by date range." Pure read/aggregation
/// logic with no UI yet (Phase 9 hasn't been built) — a future admin view calls this directly.
/// </summary>
public class VolunteerExaminerReportService(AppDbContext dbContext)
{
    /// <summary>
    /// Counts the sessions each VE has actually <b>worked</b> — completed ones only — optionally
    /// restricted to a ScheduledStartUtc range (either bound may be null for an open-ended range).
    ///
    /// <para><b>"Completed" is not <c>Status</c>.</b> `Status` only ever leaves `Active` on
    /// cancellation; it is never set to Completed, so a filter on `Status == Active` means "not
    /// cancelled" and matches every session the team has ever scheduled — including ones still in
    /// the future. That is what this counted until 2026-08-06, so a VE rostered onto next month's
    /// session already had it in their worked total.</para>
    ///
    /// <para>Completion is derived the same way the Sessions list derives its "Completed" chip
    /// (issue #71): finished by either route — a Session Manager marking it
    /// (<see cref="Session.TestingCompletedUtc"/>) or ExamTools closing it upstream
    /// (<see cref="Session.ExamToolsClosedUtc"/>). Kept deliberately identical so a session shown as
    /// Completed on that list is exactly one counted here. Historical imports set
    /// `ExamToolsClosedUtc` at creation, so a backfilled year counts normally.</para>
    ///
    /// <para><see cref="Session.HasEnded"/> is <i>not</i> used as a further backstop, though it is
    /// the documented one elsewhere: its arithmetic is plain C# and won't translate to SQL, and
    /// pulling every row back to filter in memory is the wrong trade for a page that already
    /// aggregates in the database. The gap it would cover is narrow — a session that ran before
    /// `ExamToolsClosedUtc` existed (2026-07-31) and was never marked complete. Those show as Active
    /// on the Sessions list too, so excluding them here keeps the two consistent.</para>
    ///
    /// <para><paramref name="teamIds"/> follows the same convention as everywhere else in this app:
    /// **null means every team**, not "no teams" (see SessionAccessScope.ResolveViewableTeamIds).
    /// Widened from a single teamId 2026-07-30 so the VE Roster page can offer "All teams" like the
    /// session list. A VolunteerExaminer is itself team-scoped, so a merged run still yields one row
    /// per VE-per-team rather than silently combining the same person across teams — hence TeamName
    /// on the result.</para>
    /// </summary>
    /// <param name="search">
    /// Call sign or name, partial and case-insensitive (issue #135). Null or blank means no filter.
    ///
    /// <para>Written as <c>ToLower().Contains(...)</c> rather than <c>EF.Functions.Like</c> on
    /// purpose. <c>Contains</c> alone translates to SQLite's <c>instr()</c>, which is <b>case
    /// sensitive</b> — so "n2spg" would find nothing while "N2SPG" worked, and InMemory would never
    /// show it because plain LINQ <c>Contains</c> is culture-sensitive there. <c>LIKE</c> would be
    /// case-insensitive but makes a literal <c>%</c> or <c>_</c> typed into the box behave as a
    /// wildcard, and EF exposes no escape-character overload. Lowering both sides sidesteps both
    /// problems and translates on either provider.</para>
    /// </param>
    /// <summary>
    /// One person's session history for their detail page: how many they have worked in total, how
    /// many this year, and the most recent few.
    ///
    /// <para><b>"Worked" is not <c>Status == Active</c>.</b> That only ever means "not cancelled" —
    /// it is never set to Completed — so counting on it includes sessions the VE is merely booked
    /// for next month, and reports a future date as their last worked. This app has hit that trap
    /// three times; the answer is the same pair of fields the Sessions list derives its Completed
    /// chip from, and it is why this shares the filter with GetSessionCountsAsync above.</para>
    ///
    /// <para><b>The year boundary is Eastern, not UTC.</b> Sessions run in the evening, so a January
    /// 1st 00:30 UTC session is the previous December 31st to everyone who was at it — and the page
    /// renders every date in ET. Counting on the raw UTC year would put it in the wrong one.</para>
    ///
    /// <para>Scoped to the teams the viewer may see, so a TeamAdmin does not read another team's
    /// session titles off a shared person's page.</para>
    /// </summary>
    public async Task<VeSessionHistory> GetPersonSessionHistoryAsync(
        int volunteerExaminerId, IReadOnlyList<int>? teamIds, DateTime nowUtc, int recentCount, CancellationToken cancellationToken)
    {
        var worked = dbContext.SessionVolunteerExaminers
            .Where(sve => sve.VolunteerExaminerId == volunteerExaminerId
                && (teamIds == null || teamIds.Contains(sve.Session.TeamId))
                && sve.Session.Status == SessionStatus.Active
                && (sve.Session.TestingCompletedUtc != null || sve.Session.ExamToolsClosedUtc != null));

        var yearStartUtc = EasternYearStartUtc(nowUtc);

        // Grouped counts are materialised BEFORE ordering. EF InMemory cannot translate an OrderBy
        // chained straight onto a GroupBy(...).Select(...) projection — the trap CLAUDE.md records
        // from this very service's GetSessionCountsAsync.
        var byTeam = await worked
            .GroupBy(sve => new { sve.Session.TeamId, sve.Session.Team.Name })
            .Select(g => new VeTeamSessionCount(
                g.Key.TeamId,
                g.Key.Name,
                g.Count(),
                g.Count(sve => sve.Session.ScheduledStartUtc >= yearStartUtc)))
            .ToListAsync(cancellationToken);

        // Summed from the per-team rows rather than queried again: two round trips that could
        // disagree if a session landed between them would be worse than one that cannot.
        var total = byTeam.Sum(t => t.Total);
        var thisYear = byTeam.Sum(t => t.ThisYear);

        var recent = await worked
            .OrderByDescending(sve => sve.Session.ScheduledStartUtc)
            .Take(recentCount)
            .Select(sve => new VeWorkedSession(
                sve.Session.Id,
                sve.Session.Title,
                sve.Session.TeamId,
                sve.Session.Team.Name,
                sve.Session.ScheduledStartUtc))
            .ToListAsync(cancellationToken);

        return new VeSessionHistory(total, thisYear, EasternYear(nowUtc), [.. byTeam.OrderBy(t => t.TeamName)], recent);
    }

    /// <summary>
    /// Midnight on January 1st of the current Eastern year, expressed in UTC — the cutoff for
    /// "this year". Converting the boundary once and comparing stored UTC values against it keeps
    /// the whole comparison translatable to SQL; converting each row's date to Eastern would not be.
    /// </summary>
    /// <summary>The Eastern calendar year "this year" refers to. Reported alongside the count so a page states the year rather than leaving the reader to assume their own.</summary>
    public static int EasternYear(DateTime nowUtc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), UlsSchedule.EasternTimeZone).Year;

    internal static DateTime EasternYearStartUtc(DateTime nowUtc)
    {
        var easternNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), UlsSchedule.EasternTimeZone);
        var yearStart = new DateTime(easternNow.Year, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(yearStart, UlsSchedule.EasternTimeZone);
    }

    public async Task<IReadOnlyList<VeSessionCount>> GetSessionCountsAsync(
        IReadOnlyList<int>? teamIds, DateTime? fromUtc, DateTime? toUtc, string? search, CancellationToken cancellationToken)
    {
        var query = dbContext.SessionVolunteerExaminers
            .Where(sve => (teamIds == null || teamIds.Contains(sve.Session.TeamId))
                // Not cancelled...
                && sve.Session.Status == SessionStatus.Active
                // ...and actually finished. Both halves are needed: Status rules out cancellations,
                // and only these two fields distinguish a session that happened from one that is
                // merely scheduled. See the remarks above before changing either.
                && (sve.Session.TestingCompletedUtc != null || sve.Session.ExamToolsClosedUtc != null));

        if (fromUtc is not null)
        {
            query = query.Where(sve => sve.Session.ScheduledStartUtc >= fromUtc);
        }

        if (toUtc is not null)
        {
            query = query.Where(sve => sve.Session.ScheduledStartUtc <= toUtc);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(sve =>
                sve.VolunteerExaminer.Name.ToLower().Contains(term)
                || (sve.VolunteerExaminer.CallSign ?? "").ToLower().Contains(term));
        }

        // Materialize the grouped counts first, then order client-side — the InMemory provider
        // can't translate OrderBy chained directly onto this GroupBy/Select projection.
        var counts = await query
            // The *session's* team, not the VE's — since issue #142 a VE is a person who can serve
            // several teams and has no single one. This keeps the report's shape unchanged (one row
            // per VE per team, exactly as the old per-team VE rows produced) while making the column
            // mean something defensible: whose session they worked, not which copy of them this is.
            .GroupBy(sve => new { sve.VolunteerExaminerId, sve.VolunteerExaminer.Name, sve.VolunteerExaminer.CallSign, TeamName = sve.Session.Team.Name })
            .Select(g => new VeSessionCount(g.Key.VolunteerExaminerId, g.Key.Name, g.Key.CallSign, g.Key.TeamName, g.Count()))
            .ToListAsync(cancellationToken);

        return counts
            .OrderByDescending(c => c.SessionCount)
            .ThenBy(c => c.Name)
            .ToList();
    }
}

public record VeSessionCount(int VolunteerExaminerId, string Name, string? CallSign, string TeamName, int SessionCount);

/// <param name="Total">Sessions actually worked, ever, within the viewer's team scope.</param>
/// <param name="ThisYear">The same, since January 1st Eastern.</param>
/// <param name="ByTeam">The same two numbers per team. A VE who serves several teams is one person with several histories, and the split is the interesting part.</param>
/// <param name="Recent">Most recent first.</param>
public record VeSessionHistory(int Total, int ThisYear, int Year, IReadOnlyList<VeTeamSessionCount> ByTeam, IReadOnlyList<VeWorkedSession> Recent);

public record VeTeamSessionCount(int TeamId, string TeamName, int Total, int ThisYear);

public record VeWorkedSession(int SessionId, string Title, int TeamId, string TeamName, DateTime ScheduledStartUtc);
