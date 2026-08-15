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

    /// <summary>Null while still awaiting manual review. Set once a Session Manager matches it to a candidate, or dismisses it — never re-flagged as pending again on a Square webhook redelivery for the same order id.</summary>
    public DateTime? ResolvedUtc { get; set; }
    public int? ResolvedByUserId { get; set; }
    public User? ResolvedByUser { get; set; }

    /// <summary>
    /// The Payment this was matched to. Null on a resolved row means it was <b>dismissed</b> rather
    /// than matched — that pairing (<see cref="ResolvedUtc"/> set, this null) is the only stored
    /// signal distinguishing the two, so don't read a null here as "unresolved".
    /// </summary>
    public int? MatchedPaymentId { get; set; }
    public Payment? MatchedPayment { get; set; }

    /// <summary>
    /// Why it was dismissed, if whoever dismissed it typed anything — optional by design, since
    /// requiring a reason on a housekeeping action mostly produces the word "duplicate".
    ///
    /// <para>Duplicated into the audit log entry rather than only living here, because that entry is
    /// what survives if this row is ever purged; kept on the row as well so the dismissed-rows view
    /// can show it without a join.</para>
    /// </summary>
    public string? ResolutionNote { get; set; }

    /// <summary>
    /// Refunds issued against this payment (#375). This is the one place in the app where Square's
    /// payment id was already stored, which is why the unmatched side could be refunded without any
    /// schema change at all while the candidate side needed a new column.
    ///
    /// <para>A refund does not resolve the row by itself — "Refund and dismiss" does both, and the
    /// dismissal is still what clears it from the screen.</para>
    /// </summary>
    public ICollection<Refund> Refunds { get; set; } = [];
}
