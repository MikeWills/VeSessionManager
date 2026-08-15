using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Integrations;
using VeSessionManager.Core.Square;

namespace VeSessionManager.Core.Payments;

/// <summary>
/// Issues refunds through Square's Refunds API (#375) — the thing that previously meant opening the
/// Square dashboard by hand.
///
/// <para><b>Two sources, one code path.</b> A candidate <see cref="Payment"/> and an
/// <see cref="UnmatchedSquarePayment"/> are different rows with different provenance, but a refund
/// against either is the same call keyed by the same kind of id. The two public methods do nothing
/// but resolve their source into that call, which is why the interesting comments are all on
/// <see cref="IssueAsync"/>.</para>
///
/// <para><b>The refund is not finished when this returns.</b> A <see cref="RefundResult.Success"/>
/// means Square accepted it; card and bank-transfer refunds then sit PENDING for anything up to 14
/// days. <see cref="RefundStatusService"/> follows them to a terminal state. Every caller that
/// renders a result has to say "submitted", not "refunded", unless the status says otherwise —
/// getting this wrong shows a Session Manager a completed refund that Square later rejects.</para>
/// </summary>
public class RefundService(
    AppDbContext dbContext,
    ISquareClient squareClient,
    TimeProvider timeProvider,
    TeamIntegrationState integrationState,
    ILogger<RefundService> logger)
{
    /// <summary>
    /// Square refuses a refund whose original payment is more than a year old, and it refuses it at
    /// the API rather than silently — but a user is better served by being told before the call
    /// than by a raw Square error afterwards, and by being told the reason is a hard limit rather
    /// than something to retry.
    /// </summary>
    public const int RefundWindowDays = 365;

    /// <summary>Square's cap: 20 refunds against one payment. Checked here for the same reason as the window above.</summary>
    public const int MaxRefundsPerPayment = 20;

    /// <summary>Refund a candidate's payment. Amount may be partial — see <see cref="IssueAsync"/>.</summary>
    public async Task<RefundOutcome> RefundPaymentAsync(
        int paymentId, decimal amountUsd, string? reason, int userId, CancellationToken cancellationToken)
    {
        var payment = await dbContext.Payments
            .Include(p => p.Candidate).ThenInclude(c => c.Session).ThenInclude(s => s.Team)
            .FirstOrDefaultAsync(p => p.Id == paymentId, cancellationToken);
        if (payment is null)
        {
            return new RefundOutcome(RefundResult.NotFound);
        }

        // Both of these are also expressed by RefundEligibility, which is what the page reads — but
        // they are checked here first because they decide whether there is even a Square payment id
        // to key the rest of the work on. Same rule, one definition; see RefundEligibility.
        if (payment.Status != PaymentStatus.Paid)
        {
            return new RefundOutcome(RefundResult.NotPaid);
        }

        if (payment.SquarePaymentId is null)
        {
            return new RefundOutcome(RefundResult.NoSquarePaymentId);
        }

        return await IssueAsync(
            payment.Candidate.Session.Team,
            payment.SquarePaymentId,
            // The amount Square actually took, which is not always what was owed — a $5 youth payment
            // against a $15 Payment.Amount is routine here (see Payment.SquareAmountPaidUsd). Using
            // Amount would offer a $15 refund on a $5 payment and Square would refuse the whole thing.
            payment.SquareAmountPaidUsd ?? payment.Amount,
            payment.PaidDateUtc,
            payment.Id,
            unmatchedSquarePaymentId: null,
            amountUsd, reason, userId, cancellationToken);
    }

    /// <summary>
    /// Refund a payment that never matched a candidate — the half of #375 that needed no schema
    /// change, because <see cref="UnmatchedSquarePayment.SquarePaymentId"/> was already the one place
    /// in this app where Square's payment id was stored.
    ///
    /// <para>Does not resolve the row. "Refund and dismiss" is two things, and the caller does the
    /// second only once this one has succeeded — a dismissal that recorded itself after a failed
    /// refund would hide the money from the only screen that lists it.</para>
    /// </summary>
    public async Task<RefundOutcome> RefundUnmatchedPaymentAsync(
        int unmatchedSquarePaymentId, decimal amountUsd, string? reason, int userId, CancellationToken cancellationToken)
    {
        var unmatched = await dbContext.UnmatchedSquarePayments
            .Include(u => u.Team)
            .FirstOrDefaultAsync(u => u.Id == unmatchedSquarePaymentId, cancellationToken);
        if (unmatched is null)
        {
            return new RefundOutcome(RefundResult.NotFound);
        }

        return await IssueAsync(
            unmatched.Team,
            unmatched.SquarePaymentId,
            unmatched.AmountUsd,
            unmatched.ReceivedUtc,
            paymentId: null,
            unmatched.Id,
            amountUsd, reason, userId, cancellationToken);
    }

    /// <param name="originalAmountUsd">What Square actually took — the ceiling on what can go back.</param>
    /// <param name="originalPaidUtc">When the money was taken, for the one-year window. Null (a Paid payment with no PaidDateUtc, which pre-webhook rows can be) is treated as unknown and allowed through to Square, which knows the real date.</param>
    private async Task<RefundOutcome> IssueAsync(
        Team team,
        string squarePaymentId,
        decimal originalAmountUsd,
        DateTime? originalPaidUtc,
        int? paymentId,
        int? unmatchedSquarePaymentId,
        decimal amountUsd,
        string? reason,
        int userId,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;

        // Muted before unconfigured, per TeamIntegrationState's own instruction: a team that switched
        // Square off should not also be told its credentials are incomplete. Both are true; only one
        // is useful. Unlike the scan-based jobs, this is user-triggered, so it reports rather than
        // returning quietly — nobody is watching the Worker log after clicking a button.
        if (!integrationState.ShouldCall(team, TeamIntegration.Square, "refunding a Square payment"))
        {
            return new RefundOutcome(RefundResult.SquareSwitchedOff);
        }

        if (!team.IsSquareConfigured)
        {
            return new RefundOutcome(RefundResult.SquareNotConfigured);
        }

        // Resume rather than re-issue. A row in Submitting with no SquareRefundId is the residue of a
        // crash (or a timeout) between persisting the key and hearing back — the refund may well
        // exist at Square. Re-sending the SAME key returns that original refund instead of creating a
        // second one, which is the entire reason the key is persisted before the call rather than
        // generated at it (CLAUDE.md's Established Pattern; a key parameter alone proves nothing).
        var inFlight = await dbContext.Refunds.FirstOrDefaultAsync(
            r => r.SquarePaymentId == squarePaymentId
                 && r.Status == RefundStatus.Submitting
                 && r.SquareRefundId == null,
            cancellationToken);

        Refund refund;
        if (inFlight is not null)
        {
            // The stored amount wins over whatever was typed this time — the key is bound to the
            // amount Square already saw, so sending a different one would either be ignored (Square
            // returns the original) or, worse, read as a new request.
            refund = inFlight;
            logger.LogInformation("Resuming in-flight refund {RefundId} for Square payment {SquarePaymentId} rather than issuing a second one", refund.Id, squarePaymentId);
        }
        else
        {
            // The same eligibility the page rendered its button from, re-decided here — the page's
            // answer is a display decision and this one is the authorization-shaped equivalent: a
            // posted form is not evidence the rule still holds, and it may not, since a refund can
            // be issued from two screens and a status can settle in between.
            var refundsSoFar = await dbContext.Refunds
                .Where(r => r.SquarePaymentId == squarePaymentId)
                .ToListAsync(cancellationToken);

            var eligibility = RefundEligibility.For(
                isPaid: true, squarePaymentId, originalAmountUsd, originalPaidUtc, refundsSoFar, now);

            if (!eligibility.CanRefund)
            {
                return new RefundOutcome(FromBlocker(eligibility.Blocker), RemainingRefundableUsd: 0m);
            }

            if (amountUsd <= 0m || amountUsd > eligibility.RemainingUsd)
            {
                return new RefundOutcome(RefundResult.AmountInvalid, RemainingRefundableUsd: eligibility.RemainingUsd);
            }

            refund = new Refund
            {
                TeamId = team.Id,
                PaymentId = paymentId,
                UnmatchedSquarePaymentId = unmatchedSquarePaymentId,
                SquarePaymentId = squarePaymentId,
                AmountUsd = amountUsd,
                Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
                // 32 characters, comfortably inside Square's 45.
                SquareIdempotencyKey = Guid.NewGuid().ToString("N"),
                Status = RefundStatus.Submitting,
                RequestedByUserId = userId,
                RequestedUtc = now
            };
            dbContext.Refunds.Add(refund);

            // Saved BEFORE the call. This line is the whole retry guarantee — everything after it can
            // crash without producing a duplicate refund.
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        try
        {
            var squareRefund = await squareClient.RefundPaymentAsync(
                team.ToSquareCredentials(),
                new SquareRefundRequest(squarePaymentId, refund.AmountUsd, refund.Reason, refund.SquareIdempotencyKey),
                cancellationToken);

            refund.SquareRefundId = squareRefund.Id;
            refund.SubmittedUtc = now;
            refund.LastCheckedUtc = now;
            refund.Status = MapStatus(squareRefund.Status);
            refund.FailureDetail = null;
            if (refund.IsSettled)
            {
                refund.SettledUtc = now;
            }

            dbContext.AddAuditLog(userId, "SquareRefundIssued", nameof(Refund), refund.Id,
                $"Refunded {Usd.Format(refund.AmountUsd)} against Square payment {squarePaymentId} "
                + $"(refund {squareRefund.Id}, status {squareRefund.Status})."
                + (refund.Reason is null ? "" : $" Reason: {refund.Reason}"), now);
            await dbContext.SaveChangesAsync(cancellationToken);

            return new RefundOutcome(RefundResult.Success, Status: refund.Status, RefundId: refund.Id);
        }
        catch (SquareRefundException ex)
        {
            // Square answered, and the answer was no. Terminal: the same key will get the same
            // refusal, so this settles rather than staying in flight for the status job to retry.
            refund.Status = RefundStatus.Failed;
            refund.FailureDetail = ex.Message;
            refund.SettledUtc = now;
            refund.LastCheckedUtc = now;

            dbContext.AddAuditLog(userId, "SquareRefundFailed", nameof(Refund), refund.Id,
                $"Square refused a {Usd.Format(refund.AmountUsd)} refund against payment {squarePaymentId}: {ex.Message}", now);
            await dbContext.SaveChangesAsync(cancellationToken);

            logger.LogError(ex, "Square refused a refund against payment {SquarePaymentId} for team {TeamId}", squarePaymentId, team.Id);
            return new RefundOutcome(RefundResult.SquareRefused, ex.Message);
        }
        catch (Exception ex)
        {
            // Never reached Square, or never heard back. The refund may or may not exist there, which
            // is exactly the case the persisted key covers: the row stays in Submitting and the next
            // attempt — a re-click, or RefundStatusService — re-sends the same key. Deliberately NOT
            // marked Failed: that would settle it and strand a refund Square might have accepted.
            refund.FailureDetail = ex.Message;
            refund.LastCheckedUtc = now;
            await dbContext.SaveChangesAsync(cancellationToken);

            logger.LogError(ex, "Refund call failed for Square payment {SquarePaymentId}, team {TeamId} — refund {RefundId} left in flight and will be retried with the same idempotency key", squarePaymentId, team.Id, refund.Id);
            return new RefundOutcome(RefundResult.CallFailed, ex.Message, RefundId: refund.Id);
        }
    }

    /// <summary>
    /// Square's wire status to this app's enum. An <b>unrecognized</b> value maps to Pending, not to
    /// a terminal state: the cost of being wrong is asymmetric. Pending means the status job keeps
    /// asking, and the truth arrives on the next poll; a wrong terminal guess stops all further
    /// checking and freezes a lie on the screen.
    /// </summary>
    /// <summary>
    /// The blocker the page shows, as the result the service returns. A pair of enums rather than
    /// one shared enum because they are answers to different questions — "why is this button
    /// disabled" has no equivalent of <see cref="RefundResult.CallFailed"/>, and never will.
    /// <see cref="RefundBlocker.FullyRefunded"/> collapses into AmountInvalid, which is what it is
    /// once an amount has actually been typed: there is nothing left to refund.
    /// </summary>
    private static RefundResult FromBlocker(RefundBlocker blocker) => blocker switch
    {
        RefundBlocker.NotPaid => RefundResult.NotPaid,
        RefundBlocker.NoSquarePaymentId => RefundResult.NoSquarePaymentId,
        RefundBlocker.TooOld => RefundResult.TooOld,
        RefundBlocker.RefundLimitReached => RefundResult.RefundLimitReached,
        _ => RefundResult.AmountInvalid
    };

    internal static RefundStatus MapStatus(string squareStatus) => squareStatus.ToUpperInvariant() switch
    {
        "COMPLETED" => RefundStatus.Completed,
        "REJECTED" => RefundStatus.Rejected,
        "FAILED" => RefundStatus.Failed,
        _ => RefundStatus.Pending
    };
}

/// <param name="Detail">Square's own error text, when there is one. Shown to the user — "it failed" alone just sends them to the Square dashboard to find out why.</param>
/// <param name="Status">Where the refund got to. Present on success, and the reason success is not phrased as "refunded".</param>
/// <param name="RemainingRefundableUsd">Set on <see cref="RefundResult.AmountInvalid"/> so the message can say what the ceiling actually is.</param>
public readonly record struct RefundOutcome(
    RefundResult Result,
    string? Detail = null,
    RefundStatus? Status = null,
    decimal? RemainingRefundableUsd = null,
    int? RefundId = null);

public enum RefundResult
{
    /// <summary>Square accepted it. <b>Not</b> the same as "the buyer has their money" — check the status.</summary>
    Success,
    NotFound,

    /// <summary>The payment predates Square's payment id being captured (#375) — refund it in the Square dashboard.</summary>
    NoSquarePaymentId,
    NotPaid,
    SquareNotConfigured,
    SquareSwitchedOff,

    /// <summary>Past Square's one-year refund window. Permanent, not worth retrying.</summary>
    TooOld,

    /// <summary>Square allows 20 refunds against one payment.</summary>
    RefundLimitReached,

    /// <summary>Zero, negative, or more than what is left to refund.</summary>
    AmountInvalid,

    /// <summary>Square answered and declined. Terminal.</summary>
    SquareRefused,

    /// <summary>The call did not complete. The refund is recorded and will be retried with the same idempotency key — it must not be re-issued.</summary>
    CallFailed
}
