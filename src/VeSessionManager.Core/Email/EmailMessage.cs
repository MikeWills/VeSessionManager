namespace VeSessionManager.Core.Email;

/// <summary>A fully-composed, ready-to-send email — IEmailSender's job is purely SMTP mechanics, so every field it needs (including From/Reply-To) is already resolved by the caller.</summary>
public record EmailMessage(
    string ToAddress,
    string FromAddress,
    string? FromDisplayName,
    string ReplyToAddress,
    string Subject,
    string HtmlBody,
    /// <summary>Attached as a linked resource when present, so the body can reference it as
    /// <c>cid:</c>. Optional, so every existing call site is unaffected.</summary>
    InlineImage? InlineLogo = null);
