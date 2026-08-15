namespace VeSessionManager.Core.Entities;

/// <summary>
/// One refund issued through Square's Refunds API from inside this app (#375). Before this existed,
/// refunding meant opening the Square dashboard and <see cref="Payment.RefundRequested"/> was a note
/// saying somebody intended to.
///
/// <para><b>Why a row rather than just an audit entry.</b> Two reasons, and only the second is
/// obvious. A refund does not finish when the API call returns — see <see cref="RefundStatus"/> — so
/// something has to hold the in-flight state and be polled to a conclusion; an audit entry is a
/// sentence, not a state machine. And the row is written <i>before</i> Square is called, carrying
/// the idempotency key, so a crash between the call succeeding and the response landing does not
/// produce a second refund on retry (the Established Pattern in CLAUDE.md; the same shape as
/// <see cref="Payment.SquareIdempotencyKey"/>).</para>
///
/// <para><b>Money is not moved by this row.</b> It records a refund Square is doing. Nothing here
/// changes <see cref="Payment.Status"/> — see the note on <see cref="PaymentId"/>.</para>
/// </summary>
public class Refund
{
    public int Id { get; set; }

    /// <summary>
    /// Stored rather than derived through the payment, because the two sources this can come from
    /// reach a team by different routes (a Payment goes through Candidate -> Session, an
    /// UnmatchedSquarePayment holds TeamId directly) and every screen and job here filters by team.
    /// It is also the team whose Square credentials the call was made with, which is the fact that
    /// actually matters when reading this back.
    /// </summary>
    public int TeamId { get; set; }
    public Team Team { get; set; } = null!;

    /// <summary>
    /// The candidate payment refunded, if this came from the candidate side. Null for a refund of an
    /// <see cref="UnmatchedSquarePayment"/> — money with no candidate behind it — which is the whole
    /// reason both links are optional. Exactly one of the two is always set (enforced in
    /// RefundConfiguration by a check constraint).
    ///
    /// <para><b>Refunding does not move the Payment off Paid</b>, and must not. Unpaid is a live
    /// state here: <c>PaymentGenerationService</c> scans "Unpaid and no link" and would generate a
    /// fresh Square checkout link for the candidate whose money was just returned, and
    /// <c>PaymentReminderService</c> would then chase them for it. Refunded-ness is derived from
    /// these rows instead.</para>
    /// </summary>
    public int? PaymentId { get; set; }
    public Payment? Payment { get; set; }

    /// <summary>The unmatched payment refunded, if this came from the Unmatched Payments screen. See <see cref="PaymentId"/>.</summary>
    public int? UnmatchedSquarePaymentId { get; set; }
    public UnmatchedSquarePayment? UnmatchedSquarePayment { get; set; }

    /// <summary>
    /// Square's <b>payment</b> id — what <c>RefundPayment</c> is keyed by, and the thing this whole
    /// feature was blocked on. Not the order id. Copied onto the row rather than read back through
    /// the link above, so a refund stays readable even if the payment it came from is later purged,
    /// and so the reconciliation question ("what did we send back against this Square payment?") is
    /// answerable with one indexed lookup.
    /// </summary>
    public required string SquarePaymentId { get; set; }

    /// <summary>How much was sent back. Not necessarily the full payment — partials are supported and are the right remedy for an amount mismatch (see <see cref="Payment.AmountMismatchFlaggedUtc"/>).</summary>
    public decimal AmountUsd { get; set; }

    /// <summary>Free text passed through to Square's own <c>reason</c> field, so it appears in the merchant's dashboard too rather than only here. Optional — Square does not require it.</summary>
    public string? Reason { get; set; }

    /// <summary>
    /// Generated and persisted <b>before</b> the Square call, then reused on every retry — Square's
    /// idempotency guarantee returns the original refund rather than issuing a second one. Max 45
    /// characters at Square; a 32-character GUID ("N" format) fits with room to spare.
    /// </summary>
    public required string SquareIdempotencyKey { get; set; }

    /// <summary>Null until Square accepts the call. Null with a <see cref="Status"/> of Submitting is the crashed-mid-call state the status job retries.</summary>
    public string? SquareRefundId { get; set; }

    public RefundStatus Status { get; set; } = RefundStatus.Submitting;

    /// <summary>Square's error detail on a Rejected/Failed refund, or the exception message on a call that never got through. Shown to the Session Manager, because "it failed" without the reason sends them to the Square dashboard anyway.</summary>
    public string? FailureDetail { get; set; }

    public int RequestedByUserId { get; set; }
    public User RequestedByUser { get; set; } = null!;
    public DateTime RequestedUtc { get; set; }

    /// <summary>When Square accepted the refund — i.e. when <see cref="SquareRefundId"/> arrived. Distinct from <see cref="SettledUtc"/> by up to 14 days.</summary>
    public DateTime? SubmittedUtc { get; set; }

    /// <summary>When a terminal status was observed. Both the "stop polling" guard for <c>RefundStatusService</c> and the answer to "when did this actually land?".</summary>
    public DateTime? SettledUtc { get; set; }

    /// <summary>Last time the status job asked Square about this one. Written even when nothing changed, so a refund stuck Pending for a fortnight is distinguishable from one nothing is watching.</summary>
    public DateTime? LastCheckedUtc { get; set; }

    /// <summary>Terminal states are the ones the status job stops polling. Rejected and Failed are outcomes, not errors to retry — Square will not change its mind, and re-sending the same idempotency key would just return the same rejection.</summary>
    public bool IsSettled => Status is RefundStatus.Completed or RefundStatus.Rejected or RefundStatus.Failed;
}
