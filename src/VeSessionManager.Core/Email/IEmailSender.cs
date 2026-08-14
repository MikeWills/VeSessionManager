namespace VeSessionManager.Core.Email;

/// <summary>
/// Sends a fully-composed email. Wrapped in an interface so notification logic can be unit
/// tested without live SMTP calls (per the spec's testing rules).
/// </summary>
public interface IEmailSender
{
    Task SendAsync(EmailCredentials credentials, EmailMessage message, CancellationToken cancellationToken);

    /// <summary>
    /// Sends many messages under one connection (#293). <see cref="SmtpEmailSender"/> does
    /// connect + TLS + AUTH + send + disconnect <b>per message</b>, so a 30-VE session invitation was
    /// 30 full SMTP handshakes inside a single POST — plausibly ten to fifteen seconds before the
    /// page responded.
    ///
    /// <para>Returns one outcome per message, in the order given, rather than throwing. The fan-out
    /// rule this replaces is worth preserving exactly: one bad address must not stop the rest of the
    /// batch going out, and the caller needs to know which failed to report "sent 28, failed 2".</para>
    ///
    /// <para>The default implementation loops over <see cref="SendAsync"/>, so the nine test fakes
    /// and any future sender keep working unchanged with no batching and identical semantics. Only
    /// the real SMTP sender overrides it, because it is the only one for which a connection is
    /// expensive.</para>
    /// </summary>
    async Task<IReadOnlyList<EmailSendOutcome>> SendManyAsync(
        EmailCredentials credentials, IReadOnlyList<EmailMessage> messages, CancellationToken cancellationToken)
    {
        var outcomes = new List<EmailSendOutcome>(messages.Count);
        foreach (var message in messages)
        {
            try
            {
                await SendAsync(credentials, message, cancellationToken);
                outcomes.Add(EmailSendOutcome.Success);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                outcomes.Add(new EmailSendOutcome(false, ex));
            }
        }

        return outcomes;
    }
}

/// <summary>
/// What happened to one message in a batch. Deliberately not an exception: a partial failure is the
/// expected case for a fan-out over addresses people typed, not an exceptional one.
/// </summary>
public record EmailSendOutcome(bool Sent, Exception? Error)
{
    public static readonly EmailSendOutcome Success = new(true, null);
}
