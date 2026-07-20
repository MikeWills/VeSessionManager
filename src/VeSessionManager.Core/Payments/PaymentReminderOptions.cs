namespace VeSessionManager.Core.Payments;

public class PaymentReminderOptions
{
    public const string SectionName = "PaymentReminder";

    /// <summary>Days after DateRegisteredUtc before a still-Unmatched candidate gets flagged for manual FCC/FRN review. Per the spec, this is the one part of Phase 6 explicitly called out as configurable — the 5-day reminder and 10-day expiration thresholds are fixed by definition, not config values.</summary>
    public int UnmatchedReviewWindowDays { get; set; } = 5;
}
