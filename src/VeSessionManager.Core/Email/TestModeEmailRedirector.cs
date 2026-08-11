using System.Net;

namespace VeSessionManager.Core.Email;

/// <summary>
/// Pure "should this send be redirected, and to what" decision — pulled out of SmtpEmailSender so
/// it's directly unit-testable without a live SMTP server or DB (SmtpEmailSender itself talks real
/// SMTP, so it isn't unit tested, per IEmailSender's own doc comment).
/// </summary>
public static class TestModeEmailRedirector
{
    public static (EmailMessage Message, bool Redirected) Apply(EmailMessage message, bool testModeEnabled, string? overrideEmail)
    {
        if (!testModeEnabled || string.IsNullOrWhiteSpace(overrideEmail))
        {
            return (message, false);
        }

        // Test Mode already sends everything to one monitoring inbox, so a BCC would deliver the
        // same message there twice — and worse, the copy would be the *un*redirected one, which
        // carries no "[TEST MODE]" marking and reads like real mail that genuinely went out.

        // The original recipient is registrant-controlled data flowing into an HTML body, same as
        // any EmailTemplateRenderer placeholder — HTML-encoded before insertion for the same reason.
        var redirected = message with
        {
            ToAddress = overrideEmail,
            BccAddress = null,
            Subject = $"[TEST MODE] {message.Subject}",
            HtmlBody = $"<p style=\"background:#fee2e2;color:#991b1b;padding:8px 12px;border:1px solid #fca5a5;font-family:sans-serif;\">" +
                       $"TEST MODE — this would have been sent to <strong>{WebUtility.HtmlEncode(message.ToAddress)}</strong>.</p>" +
                       message.HtmlBody
        };
        return (redirected, true);
    }
}
