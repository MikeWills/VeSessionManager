using MailKit.Security;
using VeSessionManager.Core.Email;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Issue #259's second half: SMTP transport security is now <b>mandatory</b>, decided by the port
/// alone and not by an admin checkbox.
///
/// <para><b>What was wrong.</b> The sender chose <c>SecureSocketOptions.StartTls</c> when the team
/// had ticked "Use STARTTLS" and <c>SecureSocketOptions.Auto</c> when it had not — and <c>Auto</c> is
/// <i>opportunistic</i>: if the server does not advertise STARTTLS, MailKit continues in cleartext
/// and <c>AuthenticateAsync</c> sends the username and password in the clear. So the checkbox was not
/// "prefer TLS", it was "TLS unless the far end says no", with no floor and nothing logged.</para>
///
/// <para>Combined with an unvalidated host (the other half of #259), an admin who never knew the
/// stored password could point the team at a server that simply declines STARTTLS and read it off
/// the wire.</para>
///
/// <para><b>Now:</b> implicit TLS on the submissions port 465, required STARTTLS everywhere else.
/// Neither can fall back to plaintext — MailKit throws instead, which is the correct outcome: a
/// failed send is recoverable and retried by the next poll, a leaked credential is not.</para>
/// </summary>
public class SmtpSecurityTests
{
    /// <summary>465 is implicit TLS from the first byte — there is no STARTTLS handshake to require.</summary>
    [Fact]
    public void Port465_UsesImplicitTls()
    {
        Assert.Equal(SecureSocketOptions.SslOnConnect, SmtpSecurity.OptionsFor(465));
    }

    /// <summary>587 is the submission port, and 25 still turns up in hand-typed settings.</summary>
    [Theory]
    [InlineData(587)]
    [InlineData(25)]
    [InlineData(2525)]
    [InlineData(1025)]
    public void EveryOtherPort_RequiresStartTls(int port)
    {
        Assert.Equal(SecureSocketOptions.StartTls, SmtpSecurity.OptionsFor(port));
    }

    /// <summary>
    /// The assertion that actually encodes the decision: no port may resolve to an option that
    /// permits cleartext. <c>Auto</c> and <c>StartTlsWhenAvailable</c> both do — the first is what
    /// this replaced, and the second is the plausible-looking wrong answer someone reaches for when
    /// a server complains.
    /// </summary>
    [Theory]
    [InlineData(25)]
    [InlineData(465)]
    [InlineData(587)]
    [InlineData(2525)]
    public void NoPort_ResolvesToAnOptionThatPermitsCleartext(int port)
    {
        var options = SmtpSecurity.OptionsFor(port);

        Assert.NotEqual(SecureSocketOptions.None, options);
        Assert.NotEqual(SecureSocketOptions.Auto, options);
        Assert.NotEqual(SecureSocketOptions.StartTlsWhenAvailable, options);
    }
}
