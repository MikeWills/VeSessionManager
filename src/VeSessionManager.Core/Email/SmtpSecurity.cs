using MailKit.Security;

namespace VeSessionManager.Core.Email;

/// <summary>
/// How this app connects to an SMTP server. <b>Transport security is mandatory and is decided by the
/// port, never by configuration</b> (issue #259, 2026-08-11).
///
/// <para><b>What this replaced.</b> The sender picked <c>StartTls</c> when a team had ticked "Use
/// STARTTLS" and <c>Auto</c> when it had not. <c>Auto</c> is <i>opportunistic</i>: if the server does
/// not advertise STARTTLS, MailKit continues in cleartext and the following
/// <c>AuthenticateAsync</c> puts the username and password on the wire in the clear. So the checkbox
/// was never "prefer TLS" — it was "TLS unless the far end declines", with no floor, nothing logged,
/// and the failure invisible from this side.</para>
///
/// <para>Combined with the unvalidated host that was the other half of #259, an admin who could not
/// read the stored password could point a team at a server that simply declines STARTTLS and collect
/// it. Validating the host closes the obvious route; requiring TLS closes the one that survives a
/// legitimate-looking hostname.</para>
///
/// <para><b>Why a hard failure is the right outcome.</b> A refused connection means a send fails and
/// the next poll retries it — every notification path here is scan-based and idempotent precisely so
/// that is safe. A leaked credential does not get retried.</para>
///
/// <para>The per-team and system-wide <c>UseStartTls</c> columns are no longer read. They are left in
/// the schema rather than migrated away: they hold no secret, dropping them buys nothing, and a
/// migration would need a rollback path for a column nothing consults.</para>
/// </summary>
public static class SmtpSecurity
{
    /// <summary>The submissions port that is TLS from the first byte, with no STARTTLS handshake.</summary>
    public const int ImplicitTlsPort = 465;

    /// <summary>
    /// The connection options for a port. <see cref="SecureSocketOptions.StartTls"/> <i>requires</i>
    /// the upgrade and throws if the server will not do it — as distinct from
    /// <see cref="SecureSocketOptions.StartTlsWhenAvailable"/> and
    /// <see cref="SecureSocketOptions.Auto"/>, which both fall back to plaintext and are the two
    /// answers this deliberately does not give.
    /// </summary>
    public static SecureSocketOptions OptionsFor(int port) =>
        port == ImplicitTlsPort ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;
}
