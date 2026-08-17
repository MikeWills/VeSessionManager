namespace VeSessionManager.Core.Payments;

/// <summary>
/// What one <c>PaymentReminderService</c> run did. Two counters since #401 — the "FCC fee reminders
/// sent" and "failed" counts went with the two messages that moved onto trigger points, and are
/// reported by <c>MessageRuleResult</c> now.
/// </summary>
public class PaymentReminderResult
{
    public int ExpirationsProcessed { get; set; }
    public int CandidatesFlaggedForReview { get; set; }

    public override string ToString() =>
        $"expirations processed {ExpirationsProcessed}, candidates flagged for review {CandidatesFlaggedForReview}";
}
