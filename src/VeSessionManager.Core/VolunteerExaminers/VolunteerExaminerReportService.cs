using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

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
    public async Task<IReadOnlyList<VeSessionCount>> GetSessionCountsAsync(
        IReadOnlyList<int>? teamIds, DateTime? fromUtc, DateTime? toUtc, CancellationToken cancellationToken)
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

        // Materialize the grouped counts first, then order client-side — the InMemory provider
        // can't translate OrderBy chained directly onto this GroupBy/Select projection.
        var counts = await query
            .GroupBy(sve => new { sve.VolunteerExaminerId, sve.VolunteerExaminer.Name, sve.VolunteerExaminer.CallSign, TeamName = sve.VolunteerExaminer.Team.Name })
            .Select(g => new VeSessionCount(g.Key.VolunteerExaminerId, g.Key.Name, g.Key.CallSign, g.Key.TeamName, g.Count()))
            .ToListAsync(cancellationToken);

        return counts
            .OrderByDescending(c => c.SessionCount)
            .ThenBy(c => c.Name)
            .ToList();
    }
}

public record VeSessionCount(int VolunteerExaminerId, string Name, string? CallSign, string TeamName, int SessionCount);
