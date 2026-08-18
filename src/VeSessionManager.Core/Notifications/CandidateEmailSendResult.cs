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
    NoFelonyDisclosure,

    /// <summary>
    /// The team has email switched off, so nothing was sent (#396). Distinct from
    /// <see cref="EmailNotConfigured"/>, which is "setup is unfinished" — this one is a switch
    /// somebody threw on purpose, and the fix is to throw it back rather than to enter credentials.
    ///
    /// <para>These three actions used to report <see cref="Sent"/> for a muted team, because the
    /// send path they shared with the scan-based jobs had to answer "nothing more to do" for a job's
    /// benefit. The jobs are rules now (#401), so the answer can be the true one.</para>
    /// </summary>
    EmailMuted
}
