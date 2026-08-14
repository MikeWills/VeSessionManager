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

        using var client = new SmtpClient();
        await ConnectAsync(client, credentials, cancellationToken);
        await client.SendAsync(BuildMimeMessage(effectiveMessage), cancellationToken);
        await client.DisconnectAsync(quit: true, cancellationToken);

        LogSent(effectiveMessage, credentials, testMode);
    }

    /// <summary>
    /// One connection for the whole batch (#293), which is the entire point — the per-message path
    /// above does a full connect + TLS + AUTH + disconnect every time, and
    /// VeSessionInvitationService fans out over a session's whole VE roster inside a POST.
    ///
    /// <para>Failures are per message, exactly as the caller's own loop used to do it: a rejected
    /// recipient is recorded and the batch continues. A failure to <i>connect</i> is different and is
    /// not swallowed — it means nothing can be sent, and reporting that as N individual send failures
    /// would bury one cause under a list of symptoms.</para>
    /// </summary>
    public async Task<IReadOnlyList<EmailSendOutcome>> SendManyAsync(
        EmailCredentials credentials, IReadOnlyList<EmailMessage> messages, CancellationToken cancellationToken)
    {
        var outcomes = new List<EmailSendOutcome>(messages.Count);
        if (messages.Count == 0)
        {
            return outcomes;
        }

        // Fetched once rather than per message — it is a deployment-wide row, and re-reading it
        // inside the loop was part of what made the per-message path expensive.
        var settings = await systemSettingsService.GetAsync(cancellationToken);

        using var client = new SmtpClient();
        await ConnectAsync(client, credentials, cancellationToken);

        try
        {
            foreach (var message in messages)
            {
                var (effectiveMessage, testMode) = TestModeEmailRedirector.Apply(
                    message, settings.TestModeEnabled, settings.TestModeOverrideEmail);

                try
                {
                    await client.SendAsync(BuildMimeMessage(effectiveMessage), cancellationToken);
                    outcomes.Add(EmailSendOutcome.Success);
                    LogSent(effectiveMessage, credentials, testMode);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    outcomes.Add(new EmailSendOutcome(false, ex));
                }
            }
        }
        finally
        {
            // In a finally so a cancellation mid-batch still closes the session politely rather than
            // dropping the socket on the server.
            await client.DisconnectAsync(quit: true, CancellationToken.None);
        }

        return outcomes;
    }

    /// <summary>
    /// Mandatory TLS, chosen by port and never by configuration — see SmtpSecurity (issue #259).
    /// A server that will not do it gets a thrown connection rather than a cleartext password.
    /// </summary>
    private static async Task ConnectAsync(SmtpClient client, EmailCredentials credentials, CancellationToken cancellationToken)
    {
        await client.ConnectAsync(credentials.Host, credentials.Port, SmtpSecurity.OptionsFor(credentials.Port), cancellationToken);

        if (!string.IsNullOrWhiteSpace(credentials.Username))
        {
            await client.AuthenticateAsync(credentials.Username, credentials.Password, cancellationToken);
        }
    }

    /// <summary>Never log the candidate's address — PII rule established throughout this codebase
    /// (see SessionIngestionService: log ids/counts, never names/emails/FRNs).</summary>
    private void LogSent(EmailMessage effectiveMessage, EmailCredentials credentials, bool testMode) =>
        logger.LogInformation("Sent email with subject {Subject} for team {TeamId} (test mode: {TestMode})",
            effectiveMessage.Subject, credentials.TeamId, testMode);

    private static MimeMessage BuildMimeMessage(EmailMessage effectiveMessage)
    {
        var mimeMessage = new MimeMessage();
        mimeMessage.From.Add(new MailboxAddress(effectiveMessage.FromDisplayName ?? "", effectiveMessage.FromAddress));
        mimeMessage.ReplyTo.Add(MailboxAddress.Parse(effectiveMessage.ReplyToAddress));
        mimeMessage.To.Add(MailboxAddress.Parse(effectiveMessage.ToAddress));

        // Bcc, not Cc: the candidate must not see that anyone else received a copy, and must not be
        // able to reply-all to a team's internal monitoring inbox.
        if (!string.IsNullOrWhiteSpace(effectiveMessage.BccAddress))
        {
            mimeMessage.Bcc.Add(MailboxAddress.Parse(effectiveMessage.BccAddress));
        }
        mimeMessage.Subject = effectiveMessage.Subject;
        var bodyBuilder = new BodyBuilder { HtmlBody = effectiveMessage.HtmlBody };

        if (effectiveMessage.InlineLogo is { } logo)
        {
            // LinkedResources, not Attachments: a linked resource is referenced from the HTML by
            // cid: and is not offered to the recipient as a downloadable file. Adding it as a plain
            // attachment would both fail to render inline and put a stray "logo.png" paperclip on
            // every email.
            var resource = bodyBuilder.LinkedResources.Add(
                logo.ContentId,
                logo.Content,
                ContentType.Parse(logo.ContentType));

            // Must match the cid: the renderer wrote into the <img> tag exactly, or the client
            // silently shows a broken image. Add() sets a generated id, so it is overwritten here.
            resource.ContentId = logo.ContentId;
        }

        mimeMessage.Body = bodyBuilder.ToMessageBody();
        return mimeMessage;
    }
}
