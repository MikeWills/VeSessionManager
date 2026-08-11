namespace VeSessionManager.Core.Notifications;

/// <summary>Outcome of a single-candidate, immediately-triggered email send (Phase 9b's admin actions) — distinct from EmailNotificationResult, which is a per-run counter for the scan-based bulk sends.</summary>
public enum CandidateEmailSendResult
{
    Sent,
    CandidateNotFound,
    NoEmailAddress,
    EmailNotConfigured,
    TemplateMissing,
    VecDoesNotSupportYouthProgram,

    /// <summary>
    /// The candidate has not declared a felony disclosure, so the instructions do not apply to them.
    /// Refused rather than sent: the email tells someone their disclosure requires extra FCC
    /// paperwork, which is not a thing to say to the wrong person (#221).
    /// </summary>
    NoFelonyDisclosure
}
