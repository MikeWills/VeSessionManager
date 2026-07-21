namespace VeSessionManager.Core.Notifications;

/// <summary>Outcome of a single-candidate, immediately-triggered email send (Phase 9b's admin actions) — distinct from EmailNotificationResult, which is a per-run counter for the scan-based bulk sends.</summary>
public enum CandidateEmailSendResult
{
    Sent,
    CandidateNotFound,
    NoEmailAddress,
    EmailNotConfigured,
    TemplateMissing,
    VecDoesNotSupportYouthProgram
}
