namespace VeSessionManager.Core.Square;

/// <summary>Domain-facing request for creating a payment link — SquareClient maps this to the Square SDK's Order-based CreatePaymentLinkRequest. IdempotencyKey must be generated and persisted by the caller *before* this call (see Payment.SquareIdempotencyKey) so a retry after a crash reuses the same key rather than creating a duplicate link.</summary>
public record SquarePaymentLinkRequest(string ReferenceId, string ItemName, decimal AmountUsd, string IdempotencyKey);

public class SquarePaymentLink
{
    /// <summary>Square's own payment-link id — distinct from OrderId, and what a delete call is keyed by (see ISquareClient.DeletePaymentLinkAsync).</summary>
    public required string Id { get; set; }

    /// <summary>Square's Order id — this, not ReferenceId, is what payment.updated webhooks carry, so it's what Payment.SquarePaymentReferenceId is matched against.</summary>
    public required string OrderId { get; set; }

    public required string Url { get; set; }
}
