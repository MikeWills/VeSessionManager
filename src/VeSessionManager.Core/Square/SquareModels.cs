namespace VeSessionManager.Core.Square;

/// <summary>Domain-facing request for creating a payment link — SquareClient maps this to the Square SDK's Order-based CreatePaymentLinkRequest. IdempotencyKey must be generated and persisted by the caller *before* this call (see Payment.SquareIdempotencyKey) so a retry after a crash reuses the same key rather than creating a duplicate link.</summary>
public record SquarePaymentLinkRequest(string ReferenceId, string ItemName, decimal AmountUsd, string IdempotencyKey);

/// <summary>
/// Domain-facing request for refunding a Square payment (#375). <paramref name="SquarePaymentId"/>
/// is Square's <b>payment</b> id, not an order id — see Refund.SquarePaymentId for why that
/// distinction has its own paragraph. IdempotencyKey must be persisted by the caller before this
/// call and reused on every retry, exactly as for a payment link.
/// </summary>
/// <param name="Reason">Optional, and passed through to Square so it shows in the merchant dashboard as well as here.</param>
public record SquareRefundRequest(string SquarePaymentId, decimal AmountUsd, string? Reason, string IdempotencyKey);

/// <summary>
/// What Square says about a refund, at the moment it was asked. Deliberately not a bool: Square
/// answers a refund request immediately and then processes it for anything up to 14 days, so a
/// returned <see cref="SquareRefund"/> with a PENDING status is a successful call and an unfinished
/// refund. See RefundStatus.
/// </summary>
public class SquareRefund
{
    public required string Id { get; set; }

    /// <summary>Square's raw status string — PENDING / COMPLETED / REJECTED / FAILED. Mapped to RefundStatus by the caller; kept as the wire value here so an unrecognized state is visible rather than silently collapsed into a known one.</summary>
    public required string Status { get; set; }

    public decimal AmountUsd { get; set; }
}

public class SquarePaymentLink
{
    /// <summary>Square's own payment-link id — distinct from OrderId, and what a delete call is keyed by (see ISquareClient.DeletePaymentLinkAsync).</summary>
    public required string Id { get; set; }

    /// <summary>Square's Order id — this, not ReferenceId, is what payment.updated webhooks carry, so it's what Payment.SquarePaymentReferenceId is matched against.</summary>
    public required string OrderId { get; set; }

    public required string Url { get; set; }
}
