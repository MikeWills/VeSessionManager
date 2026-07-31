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
    /// Counts non-cancelled sessions each VE is linked to, optionally restricted to a
    /// ScheduledStartUtc range (either bound may be null for an open-ended range). A cancelled
    /// session never happened, so it's excluded regardless of range.
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
            .Where(sve => (teamIds == null || teamIds.Contains(sve.Session.TeamId)) && sve.Session.Status == SessionStatus.Active);

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
