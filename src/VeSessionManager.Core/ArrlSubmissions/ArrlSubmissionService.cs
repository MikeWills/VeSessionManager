using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.ArrlSubmissions;

/// <summary>
/// Phase 8: the "toggle Not Submitted -> Submitted" action from a session's detail view. Manual,
/// user-triggered — no job/worker involvement, Phase 9's UI calls this directly once it exists.
/// One-way: the spec only describes Not Submitted -> Submitted, no "un-submit" action. Marking an
/// already-Submitted session again is a no-op that preserves the original
/// ArrlSubmittedDate/ArrlSubmittedByUserId rather than overwriting that audit trail (e.g. a
/// double-click from the future UI must not silently reassign credit for the submission to
/// whoever clicked second).
/// </summary>
public class ArrlSubmissionService(AppDbContext dbContext, TimeProvider timeProvider)
{
    public async Task<ArrlSubmissionMarkResult> MarkSubmittedAsync(int sessionId, int userId, CancellationToken cancellationToken)
    {
        var session = await dbContext.Sessions.FindAsync([sessionId], cancellationToken);
        if (session is null)
        {
            return ArrlSubmissionMarkResult.SessionNotFound;
        }

        if (session.ArrlSubmissionStatus == ArrlSubmissionStatus.Submitted)
        {
            return ArrlSubmissionMarkResult.AlreadySubmitted;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        session.ArrlSubmissionStatus = ArrlSubmissionStatus.Submitted;
        session.ArrlSubmittedDate = now;
        session.ArrlSubmittedByUserId = userId;

        dbContext.AuditLogs.Add(new AuditLog
        {
            UserId = userId,
            Action = "ArrlSubmissionMarked",
            EntityType = nameof(Session),
            EntityId = session.Id,
            TimestampUtc = now,
            Details = $"Session {session.ExamToolsSessionId} marked as submitted to ARRL."
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        return ArrlSubmissionMarkResult.Marked;
    }
}

public enum ArrlSubmissionMarkResult
{
    Marked,
    AlreadySubmitted,
    SessionNotFound
}
