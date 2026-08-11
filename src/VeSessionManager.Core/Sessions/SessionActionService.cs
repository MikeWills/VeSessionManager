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
    // CandidateNotificationService was dropped with the automatic felony-disclosure send (#221) —
    // this service sends no email at all now.
    SquarePaymentMatchingService squarePaymentMatchingService,
    TimeProvider timeProvider,
    ILogger<SessionActionService> logger)
{
    /// <summary>
    /// "Mark session as completed" — sets Session.TestingCompletedUtc/TestingCompletedByUserId, and
    /// bulk-flips Candidate.Tested = true for every candidate still in a non-terminal ApplicationStatus
    /// (Unmatched or Received) — candidates already marked Failed/NotTested before this point are left
    /// alone, per spec.
    ///
    /// <para><b>No longer sends FelonyDisclosureInstructions (#221, 2026-08-11).</b> It used to, to
    /// every candidate whose Tested just flipped and who had declared a disclosure — no button, no
    /// confirmation, riding along with this action. Two things were wrong with that. The email tells
    /// someone their felony disclosure means extra FCC paperwork, which is not a thing to send as a
    /// side effect of a bulk status flip. And it arrived <i>after</i> the session, at the point where
    /// they can no longer easily ask anyone about it — the information is worth having beforehand,
    /// while there is still a Session Manager in the room.</para>
    ///
    /// <para>It is a per-candidate button now, offered whenever a disclosure is declared rather than
    /// only once tested. See CandidateNotificationService.SendFelonyDisclosureInstructionsAsync.</para>
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

        // Reported rather than sent (#221). Removing the automatic send means a candidate who should
        // get these instructions can now get nothing at all, so the count of people still awaiting
        // them travels back with the result and the page says so. The session's candidate rows carry
        // the same marker, because a number in a one-off status message is gone on the next click.
        var awaitingFelonyInstructions = session.Candidates
            .Count(c => c.HasFelonyDisclosure == true && c.FelonyDisclosureInstructionsSentUtc is null);

        logger.LogInformation("Session {SessionId} marked completed by user {UserId}: {TestedCount} candidate(s) tested, {AwaitingCount} awaiting felony disclosure instructions",
            session.Id, userId, candidatesJustTested.Count, awaitingFelonyInstructions);
        return new SessionCompletionResult(SessionActionResult.Success, candidatesJustTested.Count, awaitingFelonyInstructions);
    }

    /// <summary>
    /// "Override how much this specific session retains in total, instead of the fee schedule's
    /// default per-candidate max-retention amount summed across every candidate" (requested
    /// 2026-07-30). Real per-session expenses (pencils, paper, postage) are usually a flat session
    /// cost, not a per-candidate one — a team with $20 of real expenses across 50 candidates wants to
    /// type $20 once, not compute/edit a per-candidate figure. Pass null to clear the override and
    /// revert to the per-candidate default (Session.GetFeeSummary). No validation on overrideAmount
    /// here beyond non-negative — the caller (Detail.cshtml.cs) parses/validates the raw form input
    /// first, same division of responsibility as SetFrnAsync's blank-check.
    /// </summary>
    public async Task<SessionActionResult> SetRetainedAmountOverrideAsync(int sessionId, decimal? overrideAmount, int userId, CancellationToken cancellationToken)
    {
        var session = await dbContext.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
        if (session is null)
        {
            return SessionActionResult.NotFound;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        session.RetainedAmountOverride = overrideAmount;
        session.RetainedAmountOverrideByUserId = overrideAmount is null ? null : userId;
        session.RetainedAmountOverrideUtc = overrideAmount is null ? null : now;

        dbContext.AddAuditLog(userId, "SessionRetainedAmountOverrideSet", nameof(Session), session.Id,
            overrideAmount is null
                ? $"Session {session.ExamToolsSessionId} total-retained override cleared — back to the per-candidate fee schedule default."
                : $"Session {session.ExamToolsSessionId} total-retained override set to ${overrideAmount:F2} for the whole session.",
            now);
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Session {SessionId} retained-amount override set to {OverrideAmount} by user {UserId}", session.Id, overrideAmount, userId);
        return SessionActionResult.Success;
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
        // Cleared together with the flag it belongs to: leaving the timestamp behind meant a cleared
        // session still looked flagged to anything reading the column instead of the bool.
        session.RescheduleFlaggedUtc = null;

        dbContext.AddAuditLog(userId, "RescheduleFlagCleared", nameof(Session), session.Id, $"Reschedule review flag cleared for session {session.ExamToolsSessionId}.", now);
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Reschedule flag cleared for session {SessionId} by user {UserId}", session.Id, userId);
        return SessionActionResult.Success;
    }

    /// <summary>
    /// Feature request (2026-07-29, prompted by orphaned walk-in-candidate rows found while
    /// verifying the license-class backfill — see docs/exam-result-license-class.md): a
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

/// <param name="CandidatesAwaitingFelonyInstructions">
/// How many candidates on this session declared a felony disclosure and have not been sent the
/// instructions. Replaces the old "emails sent" count, which counted an automatic send that no longer
/// happens (#221) — reporting zero sends forever would read as a failure rather than a design change.
/// </param>
public record SessionCompletionResult(SessionActionResult Result, int CandidatesTested, int CandidatesAwaitingFelonyInstructions);

public record SessionDeleteResult(SessionActionResult Result, int CandidatesRemoved, int PaymentsRemoved, int VeAssignmentsRemoved);
