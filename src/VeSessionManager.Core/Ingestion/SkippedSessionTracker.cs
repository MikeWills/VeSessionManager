using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Ingestion;

/// <summary>
/// Keeps <see cref="SkippedSession"/> in step with what ingestion is currently refusing (#440).
///
/// <para>Follows the app's scan-based rule rather than reacting to a one-shot event: every run
/// re-stamps what it still refuses, and anything it did not re-stamp is swept. That makes the table a
/// statement about the present rather than a log of the past, which is what lets the alert clear
/// itself — nobody dismisses these, because a dismiss button would let somebody silence a live
/// misconfiguration.</para>
///
/// <para>Callers save; nothing here calls <c>SaveChangesAsync</c>. Ingestion already saves per session
/// as it goes, and a tracker that saved on its own would commit at a point the caller did not choose.</para>
/// </summary>
public static class SkippedSessionTracker
{
    /// <summary>
    /// Records that this session was refused, or re-stamps the existing row.
    ///
    /// <para><see cref="SkippedSession.FirstSeenUtc"/> is deliberately never moved. It is what the
    /// alert reports, because "how long has this been broken" is the question that matters for a fault
    /// nothing else surfaces — the beta case ran five days. Re-stamping it every hour would make a
    /// week-old misconfiguration look like it started this morning.</para>
    /// </summary>
    public static async Task RecordAsync(
        AppDbContext dbContext, int teamId, string examToolsSessionId, string vecCode,
        string? title, DateTime? scheduledStartUtc, SkippedSessionReason reason, DateTime now,
        CancellationToken cancellationToken)
    {
        var existing = await dbContext.SkippedSessions
            .FirstOrDefaultAsync(s => s.TeamId == teamId && s.ExamToolsSessionId == examToolsSessionId, cancellationToken);

        if (existing is null)
        {
            dbContext.SkippedSessions.Add(new SkippedSession
            {
                TeamId = teamId,
                ExamToolsSessionId = examToolsSessionId,
                VecCode = vecCode,
                Title = title,
                ScheduledStartUtc = scheduledStartUtc,
                Reason = reason,
                FirstSeenUtc = now,
                LastSeenUtc = now
            });
            return;
        }

        // Everything except FirstSeenUtc is refreshed: the feed is the authority on the session's
        // title and date, and the reason can genuinely change — fixing the VEC code moves a row from
        // NoMatchingVec to NoFeeConfiguration, which is progress and points at a different page.
        existing.VecCode = vecCode;
        existing.Title = title;
        existing.ScheduledStartUtc = scheduledStartUtc;
        existing.Reason = reason;
        existing.LastSeenUtc = now;
    }

    /// <summary>Clears the skip for a session that has now been created — the configuration was fixed, which is the whole resolution.</summary>
    public static async Task ClearAsync(AppDbContext dbContext, int teamId, string examToolsSessionId, CancellationToken cancellationToken)
    {
        var existing = await dbContext.SkippedSessions
            .FirstOrDefaultAsync(s => s.TeamId == teamId && s.ExamToolsSessionId == examToolsSessionId, cancellationToken);

        if (existing is not null)
        {
            dbContext.SkippedSessions.Remove(existing);
        }
    }

    /// <summary>
    /// Drops rows this run did not re-stamp — the feed has stopped reporting those sessions, so they
    /// are no longer a configuration fault and an alert about one would be permanently unresolvable.
    ///
    /// <para>⚠️ <b>Per team, always.</b> Ingestion runs team by team, so one team's run says nothing
    /// about whether another team's skips are still current. A global sweep would clear every other
    /// team's live faults on every run and the alert would flicker instead of persisting.</para>
    /// </summary>
    public static async Task SweepAsync(AppDbContext dbContext, int teamId, DateTime runStartedUtc, CancellationToken cancellationToken)
    {
        var stale = await dbContext.SkippedSessions
            .Where(s => s.TeamId == teamId && s.LastSeenUtc < runStartedUtc)
            .ToListAsync(cancellationToken);

        dbContext.SkippedSessions.RemoveRange(stale);
    }
}
