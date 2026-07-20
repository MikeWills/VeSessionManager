using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace VeSessionManager.Core.Email;

/// <summary>
/// Sends mail via MailKit's SmtpClient. Connects fresh per send rather than holding a persistent
/// connection — this app's email volume (registration confirmations, daily reminders) is low
/// enough that per-send connect/disconnect overhead doesn't matter, and it sidesteps stale-
/// connection handling entirely. Needs no per-team cache the way ExamTools/Zoom/Discord/Square
/// do — credentials are just threaded straight through per call, since each team has its own
/// separate SMTP account (confirmed with the user) and there's no login/session state to reuse
/// across calls in the first place.
/// </summary>
public class SmtpEmailSender(ILogger<SmtpEmailSender> logger) : IEmailSender
{
    public async Task SendAsync(EmailCredentials credentials, EmailMessage message, CancellationToken cancellationToken)
    {
        var mimeMessage = new MimeMessage();
        mimeMessage.From.Add(new MailboxAddress(message.FromDisplayName ?? "", message.FromAddress));
        mimeMessage.ReplyTo.Add(MailboxAddress.Parse(message.ReplyToAddress));
        mimeMessage.To.Add(MailboxAddress.Parse(message.ToAddress));
        mimeMessage.Subject = message.Subject;
        mimeMessage.Body = new BodyBuilder { HtmlBody = message.HtmlBody }.ToMessageBody();

        using var client = new SmtpClient();
        var secureSocketOptions = credentials.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto;
        await client.ConnectAsync(credentials.Host, credentials.Port, secureSocketOptions, cancellationToken);

        if (!string.IsNullOrWhiteSpace(credentials.Username))
        {
            await client.AuthenticateAsync(credentials.Username, credentials.Password, cancellationToken);
        }

        await client.SendAsync(mimeMessage, cancellationToken);
        await client.DisconnectAsync(quit: true, cancellationToken);

        // Never log the candidate's address — PII rule established throughout this codebase
        // (see SessionIngestionService: log ids/counts, never names/emails/FRNs).
        logger.LogInformation("Sent email with subject {Subject} for team {TeamId}", message.Subject, credentials.TeamId);
    }
}
