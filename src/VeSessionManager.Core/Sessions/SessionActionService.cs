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
/// wrapper needed here). DeleteAsync (added 2026-07-29) is TeamAdmin/SystemAdmin-only, not a
/// Session Manager action — the caller (Detail.cshtml.cs) gates it via AdminAccessScope.CanManageTeam,
/// not SessionAccessScope.CanEdit, since it's a destructive cleanup action out of scope for routine
/// session management — see CLAUDE.md's "Executing actions with care" guidance.
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

    /// <summary>
    /// Feature request (2026-07-29, prompted by orphaned walk-in-candidate rows found while
    /// verifying the license-class backfill — see TODO.md/docs/exam-result-license-class.md): a
    /// genuine hard delete of a Session and everything attached to it, unlike the rest of this
    /// app's usual "PII nulled in place, row kept for stats" pattern — the whole point here is
    /// removing a stale/orphaned local row outright, not retaining it. Candidate.SessionId and
    /// SessionVolunteerExaminer.SessionId are both DeleteBehavior.Restrict (see AppDbContext), so
    /// this removes Payments, then Candidates, then SessionVolunteerExaminer rows, then the Session
    /// itself, in that FK-safe order, inside SaveChangesAsync's own transaction.
    ///
    /// Deliberately does NOT block just because candidates are attached — the motivating case *is*
    /// a session with orphaned candidate rows still on it — but does block if any of this session's
    /// Payments are still referenced by an UnmatchedSquarePayment.MatchedPaymentId (that FK is also
    /// Restrict), since silently orphaning a manual-match record would be more confusing than making
    /// the caller resolve it first.
    /// </summary>
    public async Task<SessionDeleteResult> DeleteAsync(int sessionId, int userId, CancellationToken cancellationToken)
    {
        var session = await dbContext.Sessions
            .Include(s => s.Candidates).ThenInclude(c => c.Payments)
            .Include(s => s.SessionVolunteerExaminers)
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
        if (session is null)
        {
            return new SessionDeleteResult(SessionActionResult.NotFound, 0, 0, 0);
        }

        var payments = session.Candidates.SelectMany(c => c.Payments).ToList();
        var candidateCount = session.Candidates.Count;
        var veCount = session.SessionVolunteerExaminers.Count;

        var paymentIds = payments.Select(p => p.Id).ToList();
        var blockedByUnmatchedPayments = paymentIds.Count > 0 &&
            await dbContext.UnmatchedSquarePayments.AnyAsync(u => u.MatchedPaymentId != null && paymentIds.Contains(u.MatchedPaymentId.Value), cancellationToken);
        if (blockedByUnmatchedPayments)
        {
            return new SessionDeleteResult(SessionActionResult.Blocked, candidateCount, payments.Count, veCount);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;

        // Written before the rows themselves are removed — EntityId is a plain int column (no FK
        // to Session), so it stays a valid forensic record after the delete goes through.
        dbContext.AddAuditLog(userId, "SessionDeleted", nameof(Session), session.Id,
            $"Session {session.ExamToolsSessionId} deleted, along with {candidateCount} candidate(s), {payments.Count} payment(s), and {veCount} VE roster assignment(s).", now);

        dbContext.Payments.RemoveRange(payments);
        dbContext.Candidates.RemoveRange(session.Candidates);
        dbContext.SessionVolunteerExaminers.RemoveRange(session.SessionVolunteerExaminers);
        dbContext.Sessions.Remove(session);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogWarning("Session {SessionId} ({ExamToolsSessionId}) deleted by user {UserId} — {CandidateCount} candidate(s), {PaymentCount} payment(s), {VeCount} VE roster assignment(s) removed with it",
            session.Id, session.ExamToolsSessionId, userId, candidateCount, payments.Count, veCount);
        return new SessionDeleteResult(SessionActionResult.Success, candidateCount, payments.Count, veCount);
    }
}

public enum SessionActionResult
{
    Success,
    NotFound,
    AlreadyDone,
    Blocked
}

public record SessionCompletionResult(SessionActionResult Result, int CandidatesTested, int FelonyDisclosureEmailsSent);

public record SessionDeleteResult(SessionActionResult Result, int CandidatesRemoved, int PaymentsRemoved, int VeAssignmentsRemoved);
