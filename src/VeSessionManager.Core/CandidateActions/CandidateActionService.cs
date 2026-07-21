using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Payments;

namespace VeSessionManager.Core.CandidateActions;

/// <summary>
/// Phase 9b: the state-changing half of the Session Manager's per-candidate/per-payment row
/// actions (spec.md's Phase 9 "Session Manager" bullet list) — every action that isn't itself an
/// email send. Email-sending actions (resend confirmation, youth program instructions, felony
/// disclosure instructions) live on CandidateNotificationService instead, alongside the rest of
/// this app's candidate-facing email sends, per the spec's "use this same engine, don't hardcode
/// content" note. Session-level actions (mark completed, clear reschedule flag) live on
/// SessionActionService. VEC submission toggle reuses Phase 8's VecSubmissionService directly —
/// no wrapper needed here.
///
/// Every action here is manually triggered from the admin UI, one candidate/payment at a time —
/// unlike the scan-based background services elsewhere in this app, there's no polling/retry
/// concern, so each method just does its one thing and writes one AuditLog row.
/// </summary>
public class CandidateActionService(
    AppDbContext dbContext,
    PaymentGenerationService paymentGenerationService,
    TimeProvider timeProvider,
    ILogger<CandidateActionService> logger)
{
    /// <summary>"mark a candidate Failed" — only meaningful from a non-terminal status; once Granted/Failed/NotTested, the FCC watcher (and every other job) already treats the row as settled.</summary>
    public async Task<CandidateActionResult> MarkFailedAsync(int candidateId, int userId, CancellationToken cancellationToken)
    {
        var candidate = await dbContext.Candidates.FirstOrDefaultAsync(c => c.Id == candidateId, cancellationToken);
        if (candidate is null)
        {
            return CandidateActionResult.NotFound;
        }

        if (candidate.ApplicationStatus is not (CandidateApplicationStatus.Unmatched or CandidateApplicationStatus.Received))
        {
            return CandidateActionResult.InvalidState;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        candidate.ApplicationStatus = CandidateApplicationStatus.Failed;
        candidate.ResultMarkedByUserId = userId;
        candidate.ResultMarkedUtc = now;

        AddAudit(userId, "CandidateMarkedFailed", candidate.Id, $"Candidate {candidate.Id} marked Failed.", now);
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Candidate {CandidateId} marked Failed by user {UserId}", candidate.Id, userId);
        return CandidateActionResult.Success;
    }

    /// <summary>
    /// "Delete" (withdrew/no-showed) — only available while Tested = false, per spec. Sets
    /// ApplicationStatus = NotTested and immediately nulls PII (distinct from Phase 10's scheduled
    /// purge window — a no-show has no reporting relevance to preserve), keeping the row for stats.
    /// Idempotent: a candidate already NotTested is left alone rather than re-nulling already-null
    /// fields and writing a duplicate audit entry.
    /// </summary>
    public async Task<CandidateActionResult> DeleteAsync(int candidateId, int userId, CancellationToken cancellationToken)
    {
        var candidate = await dbContext.Candidates.FirstOrDefaultAsync(c => c.Id == candidateId, cancellationToken);
        if (candidate is null)
        {
            return CandidateActionResult.NotFound;
        }

        if (candidate.ApplicationStatus == CandidateApplicationStatus.NotTested)
        {
            return CandidateActionResult.AlreadyDone;
        }

        if (candidate.Tested)
        {
            return CandidateActionResult.AlreadyTested;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        candidate.ApplicationStatus = CandidateApplicationStatus.NotTested;
        candidate.Name = null;
        candidate.Email = null;
        candidate.Frn = null;
        candidate.HasFelonyDisclosure = null;
        candidate.PiiPurgedUtc = now;
        candidate.ResultMarkedByUserId = userId;
        candidate.ResultMarkedUtc = now;

        AddAudit(userId, "CandidateDeleted", candidate.Id, $"Candidate {candidate.Id} marked NotTested (withdrew/no-show) — PII cleared immediately.", now);
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Candidate {CandidateId} deleted (no-show/withdrew) by user {UserId}", candidate.Id, userId);
        return CandidateActionResult.Success;
    }

    /// <summary>
    /// "Move" to another session — only while Tested = false, and only between sessions under the
    /// same Vec (cross-VEC moves are rare enough per the spec's own note to be handled manually
    /// instead). Payment rows carry over unchanged since they're keyed to the Candidate, not the
    /// Session — no new charge is generated.
    /// </summary>
    public async Task<CandidateMoveResult> MoveAsync(int candidateId, int targetSessionId, int userId, CancellationToken cancellationToken)
    {
        var candidate = await dbContext.Candidates
            .Include(c => c.Session)
            .FirstOrDefaultAsync(c => c.Id == candidateId, cancellationToken);
        if (candidate is null)
        {
            return CandidateMoveResult.CandidateNotFound;
        }

        if (candidate.SessionId == targetSessionId)
        {
            return CandidateMoveResult.SameSession;
        }

        var targetSession = await dbContext.Sessions.FirstOrDefaultAsync(s => s.Id == targetSessionId, cancellationToken);
        if (targetSession is null)
        {
            return CandidateMoveResult.TargetSessionNotFound;
        }

        if (targetSession.Status != SessionStatus.Active)
        {
            return CandidateMoveResult.TargetSessionNotActive;
        }

        if (candidate.Tested)
        {
            return CandidateMoveResult.AlreadyTested;
        }

        if (candidate.Session.VecId != targetSession.VecId)
        {
            return CandidateMoveResult.DifferentVec;
        }

        var originalSessionId = candidate.SessionId;
        candidate.SessionId = targetSessionId;

        var now = timeProvider.GetUtcNow().UtcDateTime;
        AddAudit(userId, "CandidateMoved", candidate.Id, $"Candidate {candidate.Id} moved from session {originalSessionId} to session {targetSessionId}.", now);
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Candidate {CandidateId} moved from session {FromSessionId} to session {ToSessionId} by user {UserId}",
            candidate.Id, originalSessionId, targetSessionId, userId);
        return CandidateMoveResult.Success;
    }

    /// <summary>
    /// "Add walk-in candidate" — a manual Candidate row with no ExamToolsApplicantId. Its InitialExam
    /// Payment row is not created inline here; PaymentGenerationService's own scan picks up any
    /// candidate with no InitialExam Payment yet on the very next poll, same as an ExamTools-sourced
    /// registration — no need to duplicate that logic.
    /// </summary>
    public async Task<Candidate> AddWalkInAsync(int sessionId, string name, string? firstName, string? email, string? frn, int userId, CancellationToken cancellationToken)
    {
        var session = await dbContext.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken)
            ?? throw new InvalidOperationException($"Session {sessionId} not found.");

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var candidate = new Candidate
        {
            SessionId = sessionId,
            Name = name,
            FirstName = firstName,
            Email = email,
            Frn = string.IsNullOrWhiteSpace(frn) ? null : frn,
            FrnMissingAtRegistration = string.IsNullOrWhiteSpace(frn),
            DateRegisteredUtc = now
        };
        dbContext.Candidates.Add(candidate);
        await dbContext.SaveChangesAsync(cancellationToken);

        AddAudit(userId, "WalkInCandidateAdded", candidate.Id, $"Walk-in candidate added to session {sessionId}.", now);
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Walk-in candidate {CandidateId} added to session {SessionId} by user {UserId}", candidate.Id, sessionId, userId);
        return candidate;
    }

    /// <summary>"add/edit a candidate's FRN if missing at registration" — FrnMissingAtRegistration is left untouched, since it documents a fact about registration time, not current state.</summary>
    public async Task<CandidateActionResult> SetFrnAsync(int candidateId, string frn, int userId, CancellationToken cancellationToken)
    {
        var candidate = await dbContext.Candidates.FirstOrDefaultAsync(c => c.Id == candidateId, cancellationToken);
        if (candidate is null)
        {
            return CandidateActionResult.NotFound;
        }

        candidate.Frn = frn;

        var now = timeProvider.GetUtcNow().UtcDateTime;
        AddAudit(userId, "CandidateFrnUpdated", candidate.Id, $"Candidate {candidate.Id} FRN set to {frn}.", now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return CandidateActionResult.Success;
    }

    /// <summary>"mark paid manually for edge cases" — Unpaid -> Paid only; already-Paid is idempotent, NotApplicable can't be marked paid.</summary>
    public async Task<CandidateActionResult> MarkPaidManuallyAsync(int paymentId, int userId, CancellationToken cancellationToken)
    {
        var payment = await dbContext.Payments.FirstOrDefaultAsync(p => p.Id == paymentId, cancellationToken);
        if (payment is null)
        {
            return CandidateActionResult.NotFound;
        }

        if (payment.Status == PaymentStatus.Paid)
        {
            return CandidateActionResult.AlreadyDone;
        }

        if (payment.Status == PaymentStatus.NotApplicable)
        {
            return CandidateActionResult.InvalidState;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        payment.Status = PaymentStatus.Paid;
        payment.PaidDateUtc = now;

        AddAudit(userId, "PaymentMarkedPaidManually", payment.Id, $"Payment {payment.Id} marked Paid manually.", now);
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Payment {PaymentId} marked paid manually by user {UserId}", payment.Id, userId);
        return CandidateActionResult.Success;
    }

    /// <summary>"flag a payment as 'refund requested' with notes" — tracking-only, the actual refund is processed manually in the Square dashboard.</summary>
    public async Task<CandidateActionResult> FlagRefundRequestedAsync(int paymentId, int userId, string? notes, CancellationToken cancellationToken)
    {
        var payment = await dbContext.Payments.FirstOrDefaultAsync(p => p.Id == paymentId, cancellationToken);
        if (payment is null)
        {
            return CandidateActionResult.NotFound;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        payment.RefundRequested = true;
        payment.RefundRequestedByUserId = userId;
        payment.RefundRequestedUtc = now;
        payment.RefundNotes = notes;

        AddAudit(userId, "PaymentRefundRequested", payment.Id, $"Payment {payment.Id} flagged refund requested.", now);
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Payment {PaymentId} flagged refund requested by user {UserId}", payment.Id, userId);
        return CandidateActionResult.Success;
    }

    /// <summary>
    /// "create a retest payment for a candidate who fails and retests within the same session" —
    /// thin wrapper around Phase 3's PaymentGenerationService.CreateRetestPaymentAsync, adding the
    /// admin-action audit trail that method doesn't write itself (it's also called from contexts
    /// with no acting user). Gated on the candidate currently being Failed — a retest only makes
    /// sense after a failed attempt, matching the mockup's disabled-until-Failed row action.
    /// </summary>
    public async Task<CandidateActionResult> CreateRetestPaymentAsync(int candidateId, int userId, CancellationToken cancellationToken)
    {
        var candidate = await dbContext.Candidates.FirstOrDefaultAsync(c => c.Id == candidateId, cancellationToken);
        if (candidate is null)
        {
            return CandidateActionResult.NotFound;
        }

        if (candidate.ApplicationStatus != CandidateApplicationStatus.Failed)
        {
            return CandidateActionResult.InvalidState;
        }

        var payment = await paymentGenerationService.CreateRetestPaymentAsync(candidateId, cancellationToken);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        AddAudit(userId, "RetestPaymentCreated", candidate.Id, $"Retest Payment {payment.Id} created for candidate {candidate.Id}.", now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return CandidateActionResult.Success;
    }

    private void AddAudit(int userId, string action, int entityId, string details, DateTime now) =>
        dbContext.AuditLogs.Add(new AuditLog
        {
            UserId = userId,
            Action = action,
            EntityType = nameof(Candidate),
            EntityId = entityId,
            TimestampUtc = now,
            Details = details
        });
}

public enum CandidateActionResult
{
    Success,
    NotFound,
    InvalidState,
    AlreadyTested,
    AlreadyDone
}

public enum CandidateMoveResult
{
    Success,
    CandidateNotFound,
    TargetSessionNotFound,
    TargetSessionNotActive,
    AlreadyTested,
    DifferentVec,
    SameSession
}
