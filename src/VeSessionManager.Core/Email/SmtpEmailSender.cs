using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;
using VeSessionManager.Core.Admin;

namespace VeSessionManager.Core.Email;

/// <summary>
/// Sends mail via MailKit's SmtpClient. Connects fresh per send rather than holding a persistent
/// connection — this app's email volume (registration confirmations, daily reminders) is low
/// enough that per-send connect/disconnect overhead doesn't matter, and it sidesteps stale-
/// connection handling entirely. Needs no per-team cache the way ExamTools/Zoom/Discord/Square
/// do — credentials are just threaded straight through per call, since each team has its own
/// separate SMTP account (confirmed with the user) and there's no login/session state to reuse
/// across calls in the first place.
///
/// While SystemSettings.TestModeEnabled is on, every send is redirected here to
/// TestModeOverrideEmail instead of the real recipient (candidate, or EmailSettings.
/// AdminNotificationEmail for payment expiration notices — every caller of IEmailSender.SendAsync
/// goes through this one method, so no other code needs to know test mode exists) — checked fresh
/// on every send, no caching, same as every other admin-editable setting in this app, so flipping
/// the switch takes effect on the very next send with no restart. The original recipient is kept
/// visible in the redirected subject/body so a tester can still tell who each email would have
/// gone to.
/// </summary>
public class SmtpEmailSender(SystemSettingsService systemSettingsService, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    public async Task SendAsync(EmailCredentials credentials, EmailMessage message, CancellationToken cancellationToken)
    {
        var settings = await systemSettingsService.GetAsync(cancellationToken);
        var (effectiveMessage, testMode) = TestModeEmailRedirector.Apply(message, settings.TestModeEnabled, settings.TestModeOverrideEmail);

        var mimeMessage = new MimeMessage();
        mimeMessage.From.Add(new MailboxAddress(effectiveMessage.FromDisplayName ?? "", effectiveMessage.FromAddress));
        mimeMessage.ReplyTo.Add(MailboxAddress.Parse(effectiveMessage.ReplyToAddress));
        mimeMessage.To.Add(MailboxAddress.Parse(effectiveMessage.ToAddress));
        mimeMessage.Subject = effectiveMessage.Subject;
        mimeMessage.Body = new BodyBuilder { HtmlBody = effectiveMessage.HtmlBody }.ToMessageBody();

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
        logger.LogInformation("Sent email with subject {Subject} for team {TeamId} (test mode: {TestMode})", effectiveMessage.Subject, credentials.TeamId, testMode);
    }
}
