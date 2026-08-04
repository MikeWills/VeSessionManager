namespace VeSessionManager.Core.Entities;

/// <summary>
/// Single definition of which fields count as a Candidate's PII, and how they're cleared — shared
/// by CandidateActionService's immediate no-show/withdrew purge and PiiPurgeService's scheduled
/// retention purge, so the two can never drift on what "PII cleared" actually means. Previously
/// CandidateActionService.DeleteAsync only nulled the Candidate fields and left a candidate's
/// Payment.PaymentLinkUrl/SquarePaymentReferenceId (a live Square-hosted checkout link) untouched.
/// Requires candidate.Payments to already be loaded (Include) by the caller.
/// Frn is deliberately NOT cleared (decided 2026-08-03): an FRN is public FCC data, not PII, and
/// retaining it — like CallSign and the ULS keys — keeps the record traceable if a question about
/// the candidate's application ever comes up after the purge.
/// </summary>
public static class CandidatePiiFields
{
    public static void Clear(Candidate candidate, DateTime purgedUtc)
    {
        candidate.Name = null;
        // Added 2026-08-03. FirstName arrived later than this helper (Phase 4, for the
        // {{CandidateFirstName}} email placeholder) and was never added to it, so every purged
        // candidate kept their given name indefinitely — visible on Candidate Detail, and directly
        // contrary to what the Privacy page promises. CandidatePiiFieldsTests asserts by reflection
        // that every PII-classed property is cleared here, so the next field added cannot repeat it.
        candidate.FirstName = null;
        candidate.Email = null;
        candidate.HasFelonyDisclosure = null;
        candidate.PiiPurgedUtc = purgedUtc;

        foreach (var payment in candidate.Payments)
        {
            payment.PaymentLinkUrl = null;
            payment.SquarePaymentReferenceId = null;
        }
    }
}
