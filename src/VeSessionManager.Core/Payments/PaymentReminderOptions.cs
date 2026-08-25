namespace VeSessionManager.Core.Payments;

public class PaymentReminderOptions
{
    public const string SectionName = "PaymentReminder";

    /// <summary>Days after DateRegisteredUtc before a still-Unmatched candidate gets flagged for manual FCC/FRN review. The only setting left on PaymentReminderService — the FCC-fee reminder moved to a per-team MessageRule (#401), and the 10-day exam-fee expiration pass it once sat beside is gone entirely (2026-08-25, Payment.ExpiredUnpaid — see CLAUDE.md's "No fee, no test" Known Constraint).</summary>
    public int UnmatchedReviewWindowDays { get; set; } = 5;
}
