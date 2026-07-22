namespace VeSessionManager.Core.Entities;

/// <summary>
/// A Square payment.updated/COMPLETED webhook event whose order_id didn't match any Payment row
/// this app created, and whose buyer email (if Square collected one) didn't uniquely identify
/// exactly one candidate with an outstanding Unpaid payment either — typically a payment taken
/// through a separate online payment page, not one of this app's own generated links. Persisted
/// so nothing is silently dropped (SquareWebhookHandler previously just logged and discarded these
/// — see SquarePaymentMatchingService); a Session Manager resolves it manually via the Unmatched
/// Payments screen.
/// </summary>
public class UnmatchedSquarePayment
{
    public int Id { get; set; }

    public int TeamId { get; set; }
    public Team Team { get; set; } = null!;

    public required string SquareOrderId { get; set; }
    public required string SquarePaymentId { get; set; }
    public decimal AmountUsd { get; set; }
    public string? BuyerEmailAddress { get; set; }
    public DateTime ReceivedUtc { get; set; }

    /// <summary>Null while still awaiting manual review. Set once a Session Manager matches it to a candidate (or, in principle, some other future resolution) — never re-flagged as pending again on a Square webhook redelivery for the same order id.</summary>
    public DateTime? ResolvedUtc { get; set; }
    public int? ResolvedByUserId { get; set; }
    public User? ResolvedByUser { get; set; }
    public int? MatchedPaymentId { get; set; }
    public Payment? MatchedPayment { get; set; }
}
