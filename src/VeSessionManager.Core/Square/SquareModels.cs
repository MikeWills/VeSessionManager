namespace VeSessionManager.Core.Square;

/// <summary>Domain-facing request for creating a payment link — SquareClient maps this to the Square SDK's Order-based CreatePaymentLinkRequest.</summary>
public record SquarePaymentLinkRequest(string ReferenceId, string ItemName, decimal AmountUsd);

public class SquarePaymentLink
{
    /// <summary>Square's Order id — this, not ReferenceId, is what payment.updated webhooks carry, so it's what Payment.SquarePaymentReferenceId is matched against.</summary>
    public required string OrderId { get; set; }

    public required string Url { get; set; }
}
