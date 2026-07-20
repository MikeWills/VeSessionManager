namespace VeSessionManager.Core.Email;

/// <summary>A fully-composed, ready-to-send email — IEmailSender's job is purely SMTP mechanics, so every field it needs (including From/Reply-To) is already resolved by the caller.</summary>
public record EmailMessage(
    string ToAddress,
    string FromAddress,
    string? FromDisplayName,
    string ReplyToAddress,
    string Subject,
    string HtmlBody);
