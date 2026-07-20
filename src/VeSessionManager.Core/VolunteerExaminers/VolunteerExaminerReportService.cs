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
    /// Counts non-cancelled sessions each VE is linked to for one team, optionally restricted to a
    /// ScheduledStartUtc range (either bound may be null for an open-ended range). A cancelled
    /// session never happened, so it's excluded regardless of range.
    /// </summary>
    public async Task<IReadOnlyList<VeSessionCount>> GetSessionCountsAsync(
        int teamId, DateTime? fromUtc, DateTime? toUtc, CancellationToken cancellationToken)
    {
        var query = dbContext.SessionVolunteerExaminers
            .Where(sve => sve.Session.TeamId == teamId && sve.Session.Status == SessionStatus.Active);

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
            .GroupBy(sve => new { sve.VolunteerExaminerId, sve.VolunteerExaminer.Name, sve.VolunteerExaminer.CallSign })
            .Select(g => new VeSessionCount(g.Key.VolunteerExaminerId, g.Key.Name, g.Key.CallSign, g.Count()))
            .ToListAsync(cancellationToken);

        return counts
            .OrderByDescending(c => c.SessionCount)
            .ThenBy(c => c.Name)
            .ToList();
    }
}

public record VeSessionCount(int VolunteerExaminerId, string Name, string? CallSign, int SessionCount);
