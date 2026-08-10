namespace VeSessionManager.Core.Entities;

public class Payment
{
    public int Id { get; set; }

    public int CandidateId { get; set; }
    public Candidate Candidate { get; set; } = null!;

    /// <summary>A candidate can retest within the same session without re-registering, but owes a second fee — this is why payments are their own table instead of flat fields on Candidate.</summary>
    public PaymentReason Reason { get; set; }

    /// <summary>Snapshot from the session's FeeConfiguration at time of creation.</summary>
    public decimal Amount { get; set; }

    public PaymentStatus Status { get; set; } = PaymentStatus.Unpaid;

    public string? PaymentLinkUrl { get; set; }
    public string? SquarePaymentReferenceId { get; set; }

    /// <summary>Square's own payment-link id (distinct from SquarePaymentReferenceId, which is the
    /// Order id) — needed because deleting a payment link via Square's Checkout API is keyed by the
    /// link id, not the order id. See YouthPaymentConfirmationService, which deletes the standard
    /// $15 link by this id before generating a new youth-rate one.</summary>
    public string? SquarePaymentLinkId { get; set; }

    public DateTime? PaidDateUtc { get; set; }

    /// <summary>
    /// Set once this Payment's Square Order has been marked COMPLETED (see
    /// SquarePaymentMatchingService.CompleteOrderIfEligibleAsync) — matches this team's existing
    /// manual practice of completing an order once it's both paid and the session it's for has
    /// actually happened, so open orders in the Square dashboard stay limited to genuinely
    /// outstanding ones. Only ever set once Status is Paid and SquarePaymentReferenceId is set;
    /// null for a Payment whose order was never completed (not yet eligible, or Square rejected
    /// the call and it wasn't retried — no scan-based job watches this field today).
    /// </summary>
    public DateTime? SquareOrderCompletedUtc { get; set; }

    /// <summary>
    /// Generated and persisted *before* calling Square's Create Payment Link API, then reused on
    /// every retry for this Payment — Square's own idempotency guarantee means a retried request
    /// with the same key returns the original link/order rather than creating a second one. Guards
    /// against a crash between Square's API call succeeding and PaymentLinkUrl being saved (same
    /// class of bug as the Discord/Zoom duplicate-event issue, see docs/square-payments.md).
    /// </summary>
    public string? SquareIdempotencyKey { get; set; }

    /// <summary>True if the 10-day unpaid window passed.</summary>
    public bool ExpiredUnpaid { get; set; }
    public DateTime? PaymentReminderSentUtc { get; set; }

    /// <summary>
    /// The actual amount Square reported paid when this Payment was matched via
    /// SquarePaymentMatchingService (auto-match by buyer email, or a Session Manager's manual
    /// match from the Unmatched Payments screen) — distinct from Amount (the amount owed), because
    /// the two aren't always the same: e.g. a candidate paying the $5 ARRL youth rate through the
    /// separate Square-hosted checkout page when Amount was set to the $15 standard rate at
    /// registration time, before youth status is confirmed (only verified at test time). Null for
    /// a Payment matched the normal way (SquareWebhookHandler's own order_id lookup), where the
    /// amount actually charged was already fixed by this app's own generated payment link.
    /// </summary>
    public decimal? SquareAmountPaidUsd { get; set; }

    /// <summary>
    /// Set when SquareAmountPaidUsd didn't equal Amount at match time — still marked Paid (this
    /// team's call: an ARRL youth payment is a routine, legitimate outcome here, not something to
    /// hold back from being recorded as paid), but flagged so a Session Manager can review whether
    /// the difference needs following up on (e.g. collecting the balance if youth status doesn't
    /// hold up at test-day verification). See SquarePaymentMatchingService.ApplyMatchAsync.
    /// </summary>
    public DateTime? AmountMismatchFlaggedUtc { get; set; }

    /// <summary>
    /// Unguessable lookup key for the public, unauthenticated youth-rate confirmation page
    /// (deliberately not Id, which is sequential and would let anyone enumerate/switch other
    /// candidates' payments). Generated once alongside the standard link, only when the session's
    /// Vec.SupportsYouthProgram is true — see PaymentGenerationService.GenerateLinkAsync and
    /// docs/youth-payment-confirmation.md.
    /// </summary>
    public Guid? YouthConfirmationToken { get; set; }

    /// <summary>
    /// Set once SquarePaymentLinkPurgeService has deleted this Payment's stale Square link (still
    /// Unpaid after Team.PurgeUnpaidLinkDays). Serves two roles: the idempotency guard for the purge
    /// scan itself, and the flag PaymentGenerationService's "Unpaid + no link -> generate one" scan
    /// also checks — without it, that scan would silently regenerate a fresh link on the very next
    /// poll. See docs/payment-link-purge.md.
    /// </summary>
    public DateTime? SquareLinkPurgedUtc { get; set; }

    /// <summary>Actual refund is processed manually in the Square dashboard — this is just a note for tracking.</summary>
    public bool RefundRequested { get; set; }
    public int? RefundRequestedByUserId { get; set; }
    public User? RefundRequestedByUser { get; set; }
    public DateTime? RefundRequestedUtc { get; set; }
    public string? RefundNotes { get; set; }

    public DateTime CreatedUtc { get; set; }
}
