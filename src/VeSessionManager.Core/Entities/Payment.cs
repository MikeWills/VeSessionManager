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
    public DateTime? PaidDateUtc { get; set; }

    /// <summary>True if the 10-day unpaid window passed.</summary>
    public bool ExpiredUnpaid { get; set; }
    public DateTime? PaymentReminderSentUtc { get; set; }

    /// <summary>Actual refund is processed manually in the Square dashboard — this is just a note for tracking.</summary>
    public bool RefundRequested { get; set; }
    public int? RefundRequestedByUserId { get; set; }
    public User? RefundRequestedByUser { get; set; }
    public DateTime? RefundRequestedUtc { get; set; }
    public string? RefundNotes { get; set; }

    public DateTime CreatedUtc { get; set; }
}
