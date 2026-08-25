namespace VeSessionManager.Core.Payments;

/// <summary>
/// What one <c>PaymentReminderService</c> run did. Down to one counter — the exam-fee expiration pass
/// (2026-08-25) went the same way the FCC-fee reminder / expiration notice did in #401, except this
/// one had no replacement: it rested on a state ("our own exam fee unpaid after the fact") that
/// cannot legitimately arise. See <c>PaymentReminderService</c>'s own summary.
/// </summary>
public class PaymentReminderResult
{
    public int CandidatesFlaggedForReview { get; set; }

    public override string ToString() =>
        $"candidates flagged for review {CandidatesFlaggedForReview}";
}
