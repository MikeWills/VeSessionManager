namespace VeSessionManager.Core.Payments;

public class PaymentReminderResult
{
    public int RemindersSent { get; set; }
    public int ExpirationsProcessed { get; set; }
    public int CandidatesFlaggedForReview { get; set; }
    public int Failed { get; set; }

    public override string ToString() =>
        $"reminders sent {RemindersSent}, expirations processed {ExpirationsProcessed}, candidates flagged for review {CandidatesFlaggedForReview}, failed {Failed}";
}
