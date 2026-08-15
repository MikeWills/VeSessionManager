using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Integrations;
using VeSessionManager.Core.Square;

namespace VeSessionManager.Core.Payments;

/// <summary>
/// Handles a Square payment.updated/COMPLETED event whose order_id didn't match any Payment row
/// this app created — a payment taken through some other Square-hosted page (e.g. a separate
/// online payment page), not one of PaymentGenerationService's own generated links.
/// SquareWebhookHandler calls HandleUnmatchedOrderAsync for that case instead of just logging and
/// discarding it (the previous behavior).
///
/// Also owns "complete the Square order once it's paid and the session has happened" — this
/// team's existing manual practice, automated here: CompleteOrderIfEligibleAsync is the one
/// definition of that eligibility check, called from both directions since either side can happen
/// second — right after a payment gets matched (ApplyMatchAsync) and right after a session gets
/// marked completed (SessionActionService.MarkCompletedAsync calls
/// CompleteEligibleOrdersForSessionAsync for any of that session's payments still open in Square).
///
/// A matched payment's actual amount doesn't always equal the amount owed — see
/// ApplyMatchAsync's own doc comment for why (ARRL's $5 youth rate vs. the $15 standard rate,
/// unknown until test day) — matched either way, but flagged (Payment.AmountMismatchFlaggedUtc)
/// for a Session Manager to review.
/// </summary>
public class SquarePaymentMatchingService(
    AppDbContext dbContext,
    ISquareClient squareClient,
    TimeProvider timeProvider,
    TeamIntegrationState integrationState,
    ILogger<SquarePaymentMatchingService> logger)
{
    /// <summary>
    /// Tries an email fallback match first — exactly one candidate on this team with a matching
    /// email address (case-insensitive) and an outstanding Unpaid payment. Zero or more than one
    /// match (e.g. a shared family email) is treated the same as no match: don't guess, fall
    /// through to persisting an UnmatchedSquarePayment row for a Session Manager to resolve by
    /// hand. Idempotent against Square webhook redelivery for an order still awaiting manual
    /// review (the unique (TeamId, SquareOrderId) index is the hard backstop; this check avoids
    /// relying on it throwing).
    /// </summary>
    public async Task<SquareUnmatchedPaymentOutcome> HandleUnmatchedOrderAsync(
        int teamId, string squareOrderId, string squarePaymentId, decimal amountUsd, string? buyerEmailAddress, CancellationToken cancellationToken)
    {
        var alreadyRecorded = await dbContext.UnmatchedSquarePayments
            .AnyAsync(u => u.TeamId == teamId && u.SquareOrderId == squareOrderId, cancellationToken);
        if (alreadyRecorded)
        {
            return SquareUnmatchedPaymentOutcome.AlreadyRecorded;
        }

        if (!string.IsNullOrWhiteSpace(buyerEmailAddress))
        {
            var normalizedEmail = buyerEmailAddress.Trim().ToLowerInvariant();
            var candidates = await dbContext.Candidates
                .Include(c => c.Payments)
                .Include(c => c.Session).ThenInclude(s => s.Team)
                .Where(c => c.Session.TeamId == teamId
                            && c.Email != null && c.Email.ToLower() == normalizedEmail
                            && c.Payments.Any(p => p.Status == PaymentStatus.Unpaid))
                .ToListAsync(cancellationToken);

            if (candidates.Count == 1)
            {
                var targetPayment = PrimaryUnpaidPayment(candidates[0]);
                if (targetPayment is not null)
                {
                    await ApplyMatchAsync(targetPayment, squareOrderId, squarePaymentId, amountUsd, cancellationToken);
                    logger.LogInformation("Square order {SquareOrderId} (team {TeamId}) auto-matched to candidate {CandidateId} by buyer email", squareOrderId, teamId, candidates[0].Id);
                    return SquareUnmatchedPaymentOutcome.AutoMatched;
                }
            }
        }

        dbContext.UnmatchedSquarePayments.Add(new UnmatchedSquarePayment
        {
            TeamId = teamId,
            SquareOrderId = squareOrderId,
            SquarePaymentId = squarePaymentId,
            AmountUsd = amountUsd,
            BuyerEmailAddress = buyerEmailAddress,
            ReceivedUtc = timeProvider.GetUtcNow().UtcDateTime
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogWarning("Square order {SquareOrderId} (team {TeamId}) could not be auto-matched — recorded for manual review", squareOrderId, teamId);
        return SquareUnmatchedPaymentOutcome.RecordedUnmatched;
    }

    /// <summary>Session Manager manually matches an unmatched Square payment to a candidate, via the Unmatched Payments screen — applies to that candidate's most recent outstanding Unpaid payment, same target-selection rule the auto-match path uses.</summary>
    public async Task<SquareManualMatchResult> ManuallyMatchAsync(int unmatchedSquarePaymentId, int candidateId, int userId, CancellationToken cancellationToken)
    {
        var unmatched = await dbContext.UnmatchedSquarePayments.FirstOrDefaultAsync(u => u.Id == unmatchedSquarePaymentId, cancellationToken);
        if (unmatched is null)
        {
            return SquareManualMatchResult.NotFound;
        }

        if (unmatched.ResolvedUtc is not null)
        {
            return SquareManualMatchResult.AlreadyResolved;
        }

        var candidate = await dbContext.Candidates
            .Include(c => c.Payments)
            .Include(c => c.Session).ThenInclude(s => s.Team)
            .FirstOrDefaultAsync(c => c.Id == candidateId, cancellationToken);
        if (candidate is null || candidate.Session.TeamId != unmatched.TeamId)
        {
            return SquareManualMatchResult.CandidateNotFound;
        }

        var targetPayment = PrimaryUnpaidPayment(candidate);
        if (targetPayment is null)
        {
            return SquareManualMatchResult.NoOutstandingPayment;
        }

        await ApplyMatchAsync(targetPayment, unmatched.SquareOrderId, unmatched.SquarePaymentId, unmatched.AmountUsd, cancellationToken);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        unmatched.ResolvedUtc = now;
        unmatched.ResolvedByUserId = userId;
        unmatched.MatchedPaymentId = targetPayment.Id;
        dbContext.AddAuditLog(userId, "SquarePaymentManuallyMatched", nameof(Payment), targetPayment.Id,
            $"Square order {unmatched.SquareOrderId} manually matched to candidate {candidateId}, Payment {targetPayment.Id}.", now);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Square order {SquareOrderId} manually matched to Payment {PaymentId} by user {UserId}", unmatched.SquareOrderId, targetPayment.Id, userId);
        return SquareManualMatchResult.Success;
    }

    /// <summary>
    /// Resolve an unmatched payment without matching it to anything (#99) — the outcome for money
    /// that legitimately has no candidate behind it: a test charge, a donation, a duplicate of a
    /// payment already recorded, or a refund the team handled in Square directly.
    ///
    /// <para><b>This does nothing in Square.</b> It only stops the row nagging on this screen and in
    /// the nav badge; the money is untouched and un-refunded. The confirmation wording on the page
    /// says so explicitly, because "dismiss" is the one word here a user could reasonably read as
    /// "send it back".</para>
    ///
    /// <para>Deliberately reuses <see cref="SquareManualMatchResult"/> rather than growing a parallel
    /// enum: the three outcomes reachable here — resolved, gone, already resolved — are the same
    /// three, and the caller's switch already handles them. The candidate-shaped members simply
    /// never occur, which is why the page's mapping needs no new arm.</para>
    ///
    /// <para>Leaves <see cref="UnmatchedSquarePayment.MatchedPaymentId"/> null, which is what makes a
    /// dismissal distinguishable from a match after the fact.</para>
    /// </summary>
    /// <param name="reason">Optional free text. Not a gate — a dismissal with no reason is a valid dismissal.</param>
    public async Task<SquareManualMatchResult> DismissAsync(
        int unmatchedSquarePaymentId, string? reason, int userId, CancellationToken cancellationToken)
    {
        var unmatched = await dbContext.UnmatchedSquarePayments.FirstOrDefaultAsync(u => u.Id == unmatchedSquarePaymentId, cancellationToken);
        if (unmatched is null)
        {
            return SquareManualMatchResult.NotFound;
        }

        if (unmatched.ResolvedUtc is not null)
        {
            return SquareManualMatchResult.AlreadyResolved;
        }

        var trimmedReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();

        var now = timeProvider.GetUtcNow().UtcDateTime;
        unmatched.ResolvedUtc = now;
        unmatched.ResolvedByUserId = userId;
        unmatched.ResolutionNote = trimmedReason;

        // The order id and amount are in the audit detail on purpose: this entry is the only record
        // that survives if the row itself is ever purged, and both are what someone reconciling
        // against a Square statement months later would search for.
        dbContext.AddAuditLog(userId, "SquarePaymentDismissed", nameof(UnmatchedSquarePayment), unmatched.Id,
            $"Square order {unmatched.SquareOrderId} ({Usd.Format(unmatched.AmountUsd)}) dismissed without matching. " +
            (trimmedReason is null ? "No reason given." : $"Reason: {trimmedReason}"), now);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Square order {SquareOrderId} dismissed without matching by user {UserId}", unmatched.SquareOrderId, userId);
        return SquareManualMatchResult.Success;
    }

    /// <summary>Called by SquareWebhookHandler right after its own normal order_id match marks a Payment Paid — the other "either side can happen second" direction (the session was already marked completed before this payment's webhook arrived). Requires payment.Candidate.Session.Team already loaded by the caller.</summary>
    public Task TryCompleteOrderAsync(Payment payment, CancellationToken cancellationToken) =>
        CompleteOrderIfEligibleAsync(payment, cancellationToken);

    /// <summary>Called right after a session is marked completed — completes the Square order for any of that session's candidates' Payments that are already Paid but arrived before the session was marked done, so they don't stay silently open.</summary>
    public async Task CompleteEligibleOrdersForSessionAsync(int sessionId, CancellationToken cancellationToken)
    {
        var payments = await dbContext.Payments
            .Include(p => p.Candidate).ThenInclude(c => c.Session).ThenInclude(s => s.Team)
            .Where(p => p.Candidate.SessionId == sessionId
                        && p.Status == PaymentStatus.Paid
                        && p.SquarePaymentReferenceId != null
                        && p.SquareOrderCompletedUtc == null)
            .ToListAsync(cancellationToken);

        foreach (var payment in payments)
        {
            await CompleteOrderIfEligibleAsync(payment, cancellationToken);
        }
    }

    /// <summary>
    /// Marks Paid regardless of whether amountPaidUsd matches payment.Amount — a below-Amount
    /// payment through the separate Square-hosted checkout page is a routine, legitimate outcome
    /// for this team (e.g. the $5 ARRL youth rate against a Payment created at the $15 standard
    /// rate, since youth status isn't known until test day), not something to hold back from being
    /// recorded as paid. A mismatch is flagged (AmountMismatchFlaggedUtc) for a Session Manager to
    /// follow up on instead.
    /// </summary>
    /// <param name="squarePaymentId">
    /// Square's payment id, stored so the payment can later be refunded from inside the app (#375).
    /// Both callers have it to hand — the webhook parsed it, and an UnmatchedSquarePayment row has
    /// carried it since it was written — which is why this is a parameter rather than a lookup.
    /// </param>
    private async Task ApplyMatchAsync(Payment payment, string squareOrderId, string squarePaymentId, decimal amountPaidUsd, CancellationToken cancellationToken)
    {
        payment.Status = PaymentStatus.Paid;
        payment.PaidDateUtc = timeProvider.GetUtcNow().UtcDateTime;
        payment.SquarePaymentReferenceId = squareOrderId;
        payment.SquarePaymentId = squarePaymentId;
        payment.SquareAmountPaidUsd = amountPaidUsd;
        if (amountPaidUsd != payment.Amount)
        {
            payment.AmountMismatchFlaggedUtc = timeProvider.GetUtcNow().UtcDateTime;
            logger.LogWarning("Square order {SquareOrderId} paid ${AmountPaidUsd} against Payment {PaymentId}, which owed ${AmountOwedUsd} — matched and marked Paid, but flagged for review",
                squareOrderId, amountPaidUsd, payment.Id, payment.Amount);
        }
        await dbContext.SaveChangesAsync(cancellationToken);

        await CompleteOrderIfEligibleAsync(payment, cancellationToken);
    }

    /// <summary>Requires payment.Candidate.Session.Team already loaded by the caller (both call sites load it via Include/ThenInclude).</summary>
    private async Task CompleteOrderIfEligibleAsync(Payment payment, CancellationToken cancellationToken)
    {
        if (payment.Status != PaymentStatus.Paid || payment.SquareOrderCompletedUtc is not null || payment.SquarePaymentReferenceId is null)
        {
            return;
        }

        var team = payment.Candidate.Session.Team;

        // The inbound webhook stays switchable-off-proof by design — a delivery only arrives because
        // somebody acted in Square, and processing it is local. But COMPLETING the order is an
        // outbound call to a real merchant account, so it is muted with the rest of Square (#64).
        if (payment.Candidate.Session.TestingCompletedUtc is null
            || !integrationState.ShouldCall(team, TeamIntegration.Square, "completing a Square order")
            || !team.IsSquareConfigured)
        {
            return;
        }

        try
        {
            var credentials = team.ToSquareCredentials();
            await squareClient.CompleteOrderAsync(credentials, payment.SquarePaymentReferenceId, cancellationToken);
            payment.SquareOrderCompletedUtc = timeProvider.GetUtcNow().UtcDateTime;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // Not blocking — the Payment is already correctly marked Paid either way; completing
            // the Square order is a housekeeping follow-up to the actual payment/session-completion
            // action, not something that should fail it. SquareOrderCompletedUtc stays null on
            // failure; no scan-based job retries this today, so a failure here needs a human to
            // notice and complete the order manually in the Square dashboard if it matters.
            logger.LogError(ex, "Failed to mark Square order {SquareOrderId} Completed for Payment {PaymentId}", payment.SquarePaymentReferenceId, payment.Id);
        }
    }

    private static Payment? PrimaryUnpaidPayment(Candidate candidate) =>
        candidate.Payments.Where(p => p.Status == PaymentStatus.Unpaid).OrderByDescending(p => p.CreatedUtc).FirstOrDefault();
}

public enum SquareUnmatchedPaymentOutcome
{
    AutoMatched,
    RecordedUnmatched,
    AlreadyRecorded
}

public enum SquareManualMatchResult
{
    Success,
    NotFound,
    AlreadyResolved,
    CandidateNotFound,
    NoOutstandingPayment
}
