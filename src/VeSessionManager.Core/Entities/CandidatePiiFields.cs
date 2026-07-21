namespace VeSessionManager.Core.Entities;

/// <summary>
/// Single definition of which fields count as a Candidate's PII, and how they're cleared — shared
/// by CandidateActionService's immediate no-show/withdrew purge and PiiPurgeService's scheduled
/// retention purge, so the two can never drift on what "PII cleared" actually means. Previously
/// CandidateActionService.DeleteAsync only nulled the Candidate fields and left a candidate's
/// Payment.PaymentLinkUrl/SquarePaymentReferenceId (a live Square-hosted checkout link) untouched.
/// Requires candidate.Payments to already be loaded (Include) by the caller.
/// </summary>
public static class CandidatePiiFields
{
    public static void Clear(Candidate candidate, DateTime purgedUtc)
    {
        candidate.Name = null;
        candidate.Email = null;
        candidate.Frn = null;
        candidate.HasFelonyDisclosure = null;
        candidate.PiiPurgedUtc = purgedUtc;

        foreach (var payment in candidate.Payments)
        {
            payment.PaymentLinkUrl = null;
            payment.SquarePaymentReferenceId = null;
        }
    }
}
