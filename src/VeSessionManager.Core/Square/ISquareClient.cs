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
}
