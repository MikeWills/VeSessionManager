using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Notifications;
using VeSessionManager.Core.Payments;

namespace VeSessionManager.Core.Sessions;

/// <summary>
/// Phase 9b: the two session-level Session Manager actions that aren't already covered by an
/// existing Phase 8 service (VecSubmissionService.MarkSubmittedAsync is reused directly, no
/// wrapper needed here).
/// </summary>
public class SessionActionService(
    AppDbContext dbContext,
    CandidateNotificationService candidateNotificationService,
    SquarePaymentMatchingService squarePaymentMatchingService,
    TimeProvider timeProvider,
    ILogger<SessionActionService> logger)
{
    /// <summary>
    /// "Mark session as completed" — sets Session.TestingCompletedUtc/TestingCompletedByUserId, and
    /// bulk-flips Candidate.Tested = true for every candidate still in a non-terminal ApplicationStatus
    /// (Unmatched or Received) — candidates already marked Failed/NotTested before this point are left
    /// alone, per spec. For each candidate whose Tested just flipped true (not previously Failed) and
    /// who has HasFelonyDisclosure = true, automatically sends FelonyDisclosureInstructions.
    /// </summary>
    public async Task<SessionCompletionResult> MarkCompletedAsync(int sessionId, int userId, CancellationToken cancellationToken)
    {
        var session = await dbContext.Sessions
            .Include(s => s.Candidates)
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
        if (session is null)
        {
            return new SessionCompletionResult(SessionActionResult.NotFound, 0, 0);
        }

        if (session.TestingCompletedUtc is not null)
        {
            return new SessionCompletionResult(SessionActionResult.AlreadyDone, 0, 0);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        session.TestingCompletedUtc = now;
        session.TestingCompletedByUserId = userId;

        var candidatesJustTested = session.Candidates
            .Where(c => !c.ApplicationStatus.IsTerminal())
            .ToList();

        foreach (var candidate in candidatesJustTested)
        {
            candidate.Tested = true;
        }

        dbContext.AddAuditLog(userId, "SessionMarkedCompleted", nameof(Session), session.Id,
            $"Session {session.ExamToolsSessionId} marked completed; {candidatesJustTested.Count} candidate(s) flipped to Tested.", now);
        await dbContext.SaveChangesAsync(cancellationToken);

        // Completes the Square order for any already-Paid payment on this session that arrived
        // before the session was marked done (the other direction — payment arrives after — is
        // handled by SquarePaymentMatchingService itself right when the match happens).
        await squarePaymentMatchingService.CompleteEligibleOrdersForSessionAsync(session.Id, cancellationToken);

        // Each send is isolated — one candidate's SMTP failure must not stop the rest of the batch,
        // nor bubble up and make the whole "mark completed" action look like it failed when the
        // status flip above already succeeded and was saved.
        var felonyDisclosuresSent = 0;
        foreach (var candidate in candidatesJustTested.Where(c => c.HasFelonyDisclosure == true))
        {
            try
            {
                var result = await candidateNotificationService.SendFelonyDisclosureInstructionsAsync(candidate.Id, cancellationToken);
                if (result == CandidateEmailSendResult.Sent)
                {
                    felonyDisclosuresSent++;
                }
                else
                {
                    logger.LogWarning("FelonyDisclosureInstructions not sent for candidate {CandidateId} after session {SessionId} completion: {Result}",
                        candidate.Id, session.Id, result);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to send FelonyDisclosureInstructions for candidate {CandidateId} after session {SessionId} completion", candidate.Id, session.Id);
            }
        }

        logger.LogInformation("Session {SessionId} marked completed by user {UserId}: {TestedCount} candidate(s) tested, {FelonyCount} felony disclosure email(s) sent",
            session.Id, userId, candidatesJustTested.Count, felonyDisclosuresSent);
        return new SessionCompletionResult(SessionActionResult.Success, candidatesJustTested.Count, felonyDisclosuresSent);
    }

    /// <summary>"review and clear a session's RescheduleFlaggedForReview flag once they've manually communicated the change to candidates and confirmed the new date."</summary>
    public async Task<SessionActionResult> ClearRescheduleFlagAsync(int sessionId, int userId, CancellationToken cancellationToken)
    {
        var session = await dbContext.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
        if (session is null)
        {
            return SessionActionResult.NotFound;
        }

        if (!session.RescheduleFlaggedForReview)
        {
            return SessionActionResult.AlreadyDone;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        session.RescheduleFlaggedForReview = false;

        dbContext.AddAuditLog(userId, "RescheduleFlagCleared", nameof(Session), session.Id, $"Reschedule review flag cleared for session {session.ExamToolsSessionId}.", now);
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Reschedule flag cleared for session {SessionId} by user {UserId}", session.Id, userId);
        return SessionActionResult.Success;
    }
}

public enum SessionActionResult
{
    Success,
    NotFound,
    AlreadyDone
}

public record SessionCompletionResult(SessionActionResult Result, int CandidatesTested, int FelonyDisclosureEmailsSent);
