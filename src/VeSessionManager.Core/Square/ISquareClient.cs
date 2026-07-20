namespace VeSessionManager.Core.Square;

/// <summary>
/// Client for the subset of the Square Checkout API Phase 3 needs. Wrapped in an interface so
/// payment-generation logic can be unit tested without live calls (per the spec's testing rules).
/// </summary>
public interface ISquareClient
{
    /// <summary>True once Square:AccessToken is set. Square is an optional integration — orgs that don't collect fees online, or haven't set it up yet, should never see repeated failed-call error noise; PaymentGenerationService checks this before attempting link generation at all.</summary>
    bool IsConfigured { get; }

    Task<SquarePaymentLink> CreatePaymentLinkAsync(SquarePaymentLinkRequest request, CancellationToken cancellationToken);
}
