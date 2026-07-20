namespace VeSessionManager.Core.Square;

/// <summary>
/// Client for the subset of the Square Checkout API Phase 3 needs. Wrapped in an interface so
/// payment-generation logic can be unit tested without live calls (per the spec's testing rules).
/// </summary>
public interface ISquareClient
{
    Task<SquarePaymentLink> CreatePaymentLinkAsync(SquareCredentials credentials, SquarePaymentLinkRequest request, CancellationToken cancellationToken);
}
