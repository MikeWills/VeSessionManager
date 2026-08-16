namespace VeSessionManager.Core.Notifications;

/// <summary>
/// Outcome of one hand-composed send to several candidates (#144) — deliberately not
/// <see cref="CandidateEmailSendResult"/>, which answers for exactly one candidate and has no way to
/// say "eight went, one had no address, one failed".
///
/// <para>Modelled on <c>VeInvitationResult</c>, the same shape for the same reason: a partial outcome
/// is the normal case for a fan-out over addresses people typed, not an exceptional one.</para>
/// </summary>
public class CandidateEmailBatchResult
{
    public int Sent { get; set; }
    public int Failed { get; set; }

    /// <summary>Chosen but unreachable. Counted so the sender can go and fill in an address rather than wondering which two never arrived.</summary>
    public int NoEmailAddress { get; set; }

    /// <summary>
    /// Requested but not on the session the screen was opened for, so never contacted (#238).
    /// Normally zero. A non-zero value from an ordinary send means the roster changed while the
    /// screen was open; any other cause is a tampered form.
    /// </summary>
    public int NotOnSession { get; set; }

    /// <summary>
    /// Set when nothing was attempted at all — no SMTP, no email settings, a blank draft, or email
    /// switched off for the team. Distinct from <see cref="Failed"/>, which means the message was
    /// attempted and the server refused it.
    /// </summary>
    public string? Error { get; set; }

    public override string ToString() =>
        $"{Sent} sent, {Failed} failed, {NoEmailAddress} with no address, {NotOnSession} not on the session";
}
