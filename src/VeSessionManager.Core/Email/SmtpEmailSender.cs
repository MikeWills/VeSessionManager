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
    /// <summary>
    /// A hard ceiling on the whole connect+auth+send+disconnect sequence (2026-08-27, live incident).
    /// A stuck socket — a TLS handshake that never completes, a connect attempt that never gets a RST
    /// back — silently blocked <c>MessageRuleJob</c>'s single tick loop for over 5 hours with no
    /// exception and nothing logged, which also blocked every other team's message rules behind it in
    /// the same tick (see PerTeamDailyJob.RunTickAsync's sequential per-team loop). MailKit's own
    /// <c>SmtpClient.Timeout</c> does not reliably bound every stall shape (notably a stuck connect),
    /// so this wraps every call in its own linked, timed-out token instead of trusting that property
    /// alone — a real send normally completes in well under this.
    /// </summary>
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(60);

    public async Task SendAsync(EmailCredentials credentials, EmailMessage message, CancellationToken cancellationToken)
    {
        var settings = await systemSettingsService.GetAsync(cancellationToken);
        var (effectiveMessage, testMode) = TestModeEmailRedirector.Apply(message, settings.TestModeEnabled, settings.TestModeOverrideEmail);

        using var client = new SmtpClient();
        await WithTimeoutAsync(cancellationToken, ct => ConnectAsync(client, credentials, ct));
        await WithTimeoutAsync(cancellationToken, ct => client.SendAsync(BuildMimeMessage(effectiveMessage), ct));
        await WithTimeoutAsync(cancellationToken, ct => client.DisconnectAsync(quit: true, ct));

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
        await WithTimeoutAsync(cancellationToken, ct => ConnectAsync(client, credentials, ct));

        try
        {
            foreach (var message in messages)
            {
                var (effectiveMessage, testMode) = TestModeEmailRedirector.Apply(
                    message, settings.TestModeEnabled, settings.TestModeOverrideEmail);

                try
                {
                    await WithTimeoutAsync(cancellationToken, ct => client.SendAsync(BuildMimeMessage(effectiveMessage), ct));
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
            // dropping the socket on the server. Still timeout-guarded — cleanup that hangs is exactly
            // the failure this whole change exists to prevent.
            await WithTimeoutAsync(CancellationToken.None, ct => client.DisconnectAsync(quit: true, ct));
        }

        return outcomes;
    }

    /// <summary>
    /// Runs one MailKit call under its own <see cref="SendTimeout"/>, layered on top of whatever
    /// cancellation the caller already passed in. A timeout throws <see cref="TimeoutException"/>
    /// rather than <see cref="OperationCanceledException"/> specifically so callers that treat
    /// cancellation as "the host is shutting down, let it propagate" (see the per-message catch in
    /// <see cref="SendManyAsync"/>) still record a stuck send as a per-message failure instead of
    /// aborting the whole batch.
    /// </summary>
    private static async Task WithTimeoutAsync(CancellationToken cancellationToken, Func<CancellationToken, Task> operation)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(SendTimeout);
        try
        {
            await operation(cts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"SMTP operation did not complete within {SendTimeout.TotalSeconds:0}s.");
        }
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

        // Cc is visible, and only a message rule that explicitly asked for one ever sets it (#401
        // PR4) — see EmailMessage.CcAddress for the disclosure/unsubscribe tradeoff a team accepts
        // by setting one.
        if (!string.IsNullOrWhiteSpace(effectiveMessage.CcAddress))
        {
            mimeMessage.Cc.Add(MailboxAddress.Parse(effectiveMessage.CcAddress));
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
