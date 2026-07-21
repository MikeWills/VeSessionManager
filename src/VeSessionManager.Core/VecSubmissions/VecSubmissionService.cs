using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.VecSubmissions;

/// <summary>
/// Phase 8: the "toggle Not Submitted -> Submitted" action from a session's detail view. Manual,
/// user-triggered — no job/worker involvement, Phase 9's UI calls this directly once it exists.
/// Named generically (Vec, not Arrl) because submission goes to whichever VEC a given session is
/// actually under (Session.VecId) — ARRL is the common case for this deployment today, but not the
/// only one this data model supports. One-way: the spec only describes Not Submitted -> Submitted,
/// no "un-submit" action. Marking an already-Submitted session again is a no-op that preserves the
/// original VecSubmittedDate/VecSubmittedByUserId rather than overwriting that audit trail (e.g. a
/// double-click from the future UI must not silently reassign credit for the submission to
/// whoever clicked second).
/// </summary>
public class VecSubmissionService(AppDbContext dbContext, TimeProvider timeProvider)
{
    public async Task<VecSubmissionMarkResult> MarkSubmittedAsync(int sessionId, int userId, CancellationToken cancellationToken)
    {
        var session = await dbContext.Sessions.Include(s => s.Vec).FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
        if (session is null)
        {
            return VecSubmissionMarkResult.SessionNotFound;
        }

        if (session.VecSubmissionStatus == VecSubmissionStatus.Submitted)
        {
            return VecSubmissionMarkResult.AlreadySubmitted;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        session.VecSubmissionStatus = VecSubmissionStatus.Submitted;
        session.VecSubmittedDate = now;
        session.VecSubmittedByUserId = userId;

        dbContext.AddAuditLog(userId, "VecSubmissionMarked", nameof(Session), session.Id, $"Session {session.ExamToolsSessionId} marked as submitted to {session.Vec.Name}.", now);

        await dbContext.SaveChangesAsync(cancellationToken);
        return VecSubmissionMarkResult.Marked;
    }
}

public enum VecSubmissionMarkResult
{
    Marked,
    AlreadySubmitted,
    SessionNotFound
}
