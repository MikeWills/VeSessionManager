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
    /// <summary>
    /// "mark a candidate Failed" — only meaningful from a non-terminal status; once
    /// Granted/Failed/NotTested, the FCC watcher (and every other job) already treats the row as
    /// settled.
    ///
    /// <para><b>Sets <see cref="Candidate.Tested"/> as well as the status (2026-08-15).</b> Someone
    /// who failed an exam did, by definition, sit one, but this used to set only the status — so a
    /// manually-failed candidate stayed <c>Tested = false</c> and the two fields disagreed. Three
    /// consequences, all wrong in the same direction: the candidate was absent from "candidates
    /// tested" on the stats page while still counting against the pass rate, the session and
    /// candidate screens showed no tested tick, and — the one that could actually destroy data —
    /// they remained eligible for the no-show delete, which is gated on <c>!Tested</c> and nulls PII
    /// immediately.</para>
    ///
    /// <para><see cref="ExamResultSyncService"/> already set both when it auto-failed someone, which
    /// is why this went unnoticed: every failure on this deployment came from that path, and
    /// <c>ResultMarkedByUserId</c> is null on all of them. The manual button had never been used on
    /// real data.</para>
    /// </summary>
    public async Task<CandidateActionResult> MarkFailedAsync(int candidateId, int userId, CancellationToken cancellationToken)
    {
        var candidate = await dbContext.Candidates.FirstOrDefaultAsync(c => c.Id == candidateId, cancellationToken);
        if (candidate is null)
        {
            return CandidateActionResult.NotFound;
        }

        if (candidate.ApplicationStatus.IsTerminal())
        {
            return CandidateActionResult.InvalidState;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        candidate.ApplicationStatus = CandidateApplicationStatus.Failed;
        candidate.MarkTested(now);
        candidate.ResultMarkedByUserId = userId;
        candidate.ResultMarkedUtc = now;

        AddAudit(userId, "CandidateMarkedFailed", candidate.Id, $"Candidate {candidate.Id} marked Failed.", now);
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Candidate {CandidateId} marked Failed by user {UserId}", candidate.Id, userId);
        return CandidateActionResult.Success;
    }

    /// <summary>
    /// "Delete" (withdrew/no-showed) — only available while Tested = false, per spec. Sets
    /// ApplicationStatus = NotTested and immediately nulls PII via the same CandidatePiiFields.Clear
    /// PiiPurgeService's scheduled purge uses (distinct from Phase 10's scheduled purge window — a
    /// no-show has no reporting relevance to preserve), keeping the row for stats. Idempotent: a
    /// candidate already NotTested is left alone rather than re-nulling already-null fields and
    /// writing a duplicate audit entry.
    /// </summary>
    public async Task<CandidateActionResult> DeleteAsync(int candidateId, int userId, CancellationToken cancellationToken)
    {
        var candidate = await dbContext.Candidates.Include(c => c.Payments).FirstOrDefaultAsync(c => c.Id == candidateId, cancellationToken);
        if (candidate is null)
        {
            return CandidateActionResult.NotFound;
        }

        if (candidate.ApplicationStatus == CandidateApplicationStatus.NotTested)
        {
            return CandidateActionResult.AlreadyDone;
        }

        // Evidence, not the bare flag (#419): a Tested that came only from "Mark session completed"
        // is an assertion about the roster, not this person, and this button is the only repair for a
        // no-show stranded by it — a completed-and-closed session's roster is never synced again, so
        // ingestion cannot withdraw the row itself.
        if (candidate.TestedWithEvidence)
        {
            return CandidateActionResult.AlreadyTested;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        candidate.ApplicationStatus = CandidateApplicationStatus.NotTested;
        CandidatePiiFields.Clear(candidate, now);
        candidate.UndoCompletionTested();
        candidate.ResultMarkedByUserId = userId;
        candidate.ResultMarkedUtc = now;

        AddAudit(userId, "CandidateDeleted", candidate.Id, $"Candidate {candidate.Id} marked NotTested (withdrew/no-show) — PII cleared immediately.", now);
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Candidate {CandidateId} deleted (no-show/withdrew) by user {UserId}", candidate.Id, userId);
        return CandidateActionResult.Success;
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
        dbContext.AddAuditLog(userId, action, nameof(Candidate), entityId, details, now);
}

public enum CandidateActionResult
{
    Success,
    NotFound,
    InvalidState,
    AlreadyTested,
    AlreadyDone
}
