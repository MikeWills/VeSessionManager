using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Payments;

/// <summary>
/// Whether a Square payment can still be refunded, and how much of it is left (#375).
///
/// <para><b>Why this is a type and not two ifs in RefundService.</b> Two places need the same
/// answer and they need it for different reasons: the service, to refuse the call, and the page, to
/// decide whether to offer the button and what to say when it does not. That is exactly the shape
/// that produced #274 — one copy of a rule checking the VEC's youth-program flag and the other
/// checking nothing — and the shape <see cref="CandidateCapabilities"/> and <c>ActionOutcomes</c>
/// were extracted to stop. A refund offered where it can only fail is bad; a refund <i>refused</i>
/// on a screen where the service would have allowed it is worse, because nobody reports it.</para>
///
/// <para>Pure and synchronous on purpose — it takes the refunds rather than fetching them, so the
/// page can answer it from a collection it already loaded with the payment.</para>
/// </summary>
/// <param name="RemainingUsd">What is still refundable. Zero whenever <see cref="Blocker"/> says no.</param>
public readonly record struct RefundEligibility(decimal RemainingUsd, RefundBlocker Blocker)
{
    public bool CanRefund => Blocker == RefundBlocker.None;

    /// <param name="isPaid">Square only refunds a completed payment, and an unpaid row has no money behind it. Always true for an UnmatchedSquarePayment, which exists because money arrived.</param>
    /// <param name="squarePaymentId">Null for a candidate payment matched before #375 — permanently un-refundable from here.</param>
    /// <param name="originalAmountUsd">What Square actually took, not what was owed. See RefundService.RefundPaymentAsync for why those differ.</param>
    /// <param name="paidUtc">Null is treated as unknown rather than as too old — Square holds the real date and will refuse it there if need be. Refusing locally on a missing timestamp would block refunds on rows that are perfectly refundable.</param>
    public static RefundEligibility For(
        bool isPaid,
        string? squarePaymentId,
        decimal originalAmountUsd,
        DateTime? paidUtc,
        IEnumerable<Refund> refunds,
        DateTime nowUtc)
    {
        if (!isPaid)
        {
            return new RefundEligibility(0m, RefundBlocker.NotPaid);
        }

        if (squarePaymentId is null)
        {
            return new RefundEligibility(0m, RefundBlocker.NoSquarePaymentId);
        }

        var against = refunds as IReadOnlyCollection<Refund> ?? refunds.ToList();

        if (against.Count >= RefundService.MaxRefundsPerPayment)
        {
            return new RefundEligibility(0m, RefundBlocker.RefundLimitReached);
        }

        if (paidUtc is { } paid && nowUtc - paid > TimeSpan.FromDays(RefundService.RefundWindowDays))
        {
            return new RefundEligibility(0m, RefundBlocker.TooOld);
        }

        // A refund Square refused returns its amount to the pot — nothing was sent, so it cannot
        // have consumed any of what is refundable. Everything else counts, including the ones still
        // pending: see RefundService.RemainingRefundableAsync for why in-flight is counted here and
        // is not by Square.
        var spent = against
            .Where(r => r.Status != RefundStatus.Rejected && r.Status != RefundStatus.Failed)
            .Sum(r => r.AmountUsd);

        var remaining = originalAmountUsd - spent;
        return remaining > 0m
            ? new RefundEligibility(remaining, RefundBlocker.None)
            : new RefundEligibility(0m, RefundBlocker.FullyRefunded);
    }
}

/// <summary>Why a payment cannot be refunded, or <see cref="None"/> if it can.</summary>
public enum RefundBlocker
{
    None,
    NotPaid,

    /// <summary>Predates Square's payment id being captured. Permanent for that row — the Square dashboard is the only route.</summary>
    NoSquarePaymentId,

    /// <summary>Past Square's one-year window. Also permanent, and gets more permanent by the day.</summary>
    TooOld,
    RefundLimitReached,

    /// <summary>Nothing left — the whole payment has already gone back, or is on its way back.</summary>
    FullyRefunded
}
