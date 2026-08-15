namespace VeSessionManager.Core.Square;

/// <summary>
/// Client for the subset of the Square Checkout API Phase 3 needs. Wrapped in an interface so
/// payment-generation logic can be unit tested without live calls (per the spec's testing rules).
/// </summary>
public interface ISquareClient
{
    Task<SquarePaymentLink> CreatePaymentLinkAsync(SquareCredentials credentials, SquarePaymentLinkRequest request, CancellationToken cancellationToken);

    /// <summary>Marks a Square Order COMPLETED (Orders API) — a no-op if it's already in that state, so callers don't need their own idempotency guard against retrying this call.</summary>
    Task CompleteOrderAsync(SquareCredentials credentials, string orderId, CancellationToken cancellationToken);

    /// <summary>Deletes a Square payment link (cancels its still-open order without touching an already-completed payment, per Square's own docs). A no-op if the link is already gone (e.g. a retried call), so callers don't need their own idempotency guard.</summary>
    Task DeletePaymentLinkAsync(SquareCredentials credentials, string paymentLinkId, CancellationToken cancellationToken);

    /// <summary>
    /// Refunds a Square payment, in full or in part (#375).
    ///
    /// <para><b>Unlike the two calls above, this one does not swallow anything.</b> Their
    /// already-done cases are genuinely no-ops; here, Square's own errors are the answer — a refund
    /// larger than what is left, a payment over a year old, a 21st refund against one payment — and
    /// treating any of them as "fine, already handled" would report money as returned that was not.
    /// Retry safety comes from the caller's persisted idempotency key instead, which is what makes
    /// swallowing unnecessary rather than merely unwise.</para>
    /// </summary>
    /// <exception cref="SquareRefundException">Square rejected the refund, with its own error detail.</exception>
    Task<SquareRefund> RefundPaymentAsync(SquareCredentials credentials, SquareRefundRequest request, CancellationToken cancellationToken);

    /// <summary>Reads one refund back by Square's refund id — how a PENDING refund is followed to a terminal state, since Square sends no webhook this app subscribes to for it.</summary>
    Task<SquareRefund> GetRefundAsync(SquareCredentials credentials, string squareRefundId, CancellationToken cancellationToken);
}

/// <summary>
/// Square refused a refund, and said why. A distinct type rather than
/// <see cref="InvalidOperationException"/> (which the other calls throw) because RefundService has
/// to tell a rejection apart from a transport failure: a rejection is final and belongs on the
/// screen, while a timeout means the refund may well have been created and must be retried with the
/// same key, never re-issued.
/// </summary>
public class SquareRefundException(string message) : Exception(message);
