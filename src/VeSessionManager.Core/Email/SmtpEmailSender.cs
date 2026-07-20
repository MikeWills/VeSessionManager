using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace VeSessionManager.Core.Email;

/// <summary>
/// Sends mail via MailKit's SmtpClient. Connects fresh per send rather than holding a persistent
/// connection — this app's email volume (registration confirmations, daily reminders) is low
/// enough that per-send connect/disconnect overhead doesn't matter, and it sidesteps stale-
/// connection handling entirely. Credential validation is deferred to SendAsync, not the
/// constructor — same reasoning as ZoomClient/DiscordEventClient/SquareClient (this type can be
/// resolved from inside a Worker BackgroundService; a constructor throw there stops the whole
/// host, not just email sending).
/// </summary>
public class SmtpEmailSender(IOptions<EmailOptions> options, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    /// <summary>
    /// Requires both host and username, not just host — SmtpHost alone can have a real, correct
    /// default baked into appsettings.json (e.g. smtp.mailgun.org) while credentials are still
    /// unset, and Mailgun (and most real providers) reject unauthenticated senders outright. A
    /// deployment that genuinely needs a no-auth relay can set any non-empty SmtpUsername to
    /// force this true; SendAsync only actually authenticates when SmtpUsername is non-empty.
    /// </summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.Value.SmtpHost) && !string.IsNullOrWhiteSpace(options.Value.SmtpUsername);

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var smtpOptions = options.Value;
        if (string.IsNullOrWhiteSpace(smtpOptions.SmtpHost))
        {
            throw new InvalidOperationException(
                "SMTP is not configured. Set Email:SmtpHost (and Email:SmtpUsername/Email:SmtpPassword via user-secrets or environment variables).");
        }

        var mimeMessage = new MimeMessage();
        mimeMessage.From.Add(new MailboxAddress(message.FromDisplayName ?? "", message.FromAddress));
        mimeMessage.ReplyTo.Add(MailboxAddress.Parse(message.ReplyToAddress));
        mimeMessage.To.Add(MailboxAddress.Parse(message.ToAddress));
        mimeMessage.Subject = message.Subject;
        mimeMessage.Body = new BodyBuilder { HtmlBody = message.HtmlBody }.ToMessageBody();

        using var client = new SmtpClient();
        var secureSocketOptions = smtpOptions.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto;
        await client.ConnectAsync(smtpOptions.SmtpHost, smtpOptions.SmtpPort, secureSocketOptions, cancellationToken);

        if (!string.IsNullOrWhiteSpace(smtpOptions.SmtpUsername))
        {
            await client.AuthenticateAsync(smtpOptions.SmtpUsername, smtpOptions.SmtpPassword, cancellationToken);
        }

        await client.SendAsync(mimeMessage, cancellationToken);
        await client.DisconnectAsync(quit: true, cancellationToken);

        // Never log the candidate's address — PII rule established throughout this codebase
        // (see SessionIngestionService: log ids/counts, never names/emails/FRNs).
        logger.LogInformation("Sent email with subject {Subject}", message.Subject);
    }
}
