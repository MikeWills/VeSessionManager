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
    InlineImage? InlineLogo = null,
    /// <summary>
    /// A silent copy for a team that wants to watch what the app actually sends (EmailSettings.BccAddress).
    ///
    /// <para><b>Only candidate-facing mail may set this.</b> Three senders — password reset, VE
    /// self-service links, VE email-change confirmation — carry tokens that grant access, and a copy
    /// of one of those in a monitoring inbox is an account-takeover path, not a convenience. That
    /// rule is enforced by which call sites populate this field rather than by a runtime flag, so it
    /// cannot be switched on for the wrong sender by mistake. See issue #207.</para>
    ///
    /// <para>Dropped entirely while Test Mode is redirecting — see TestModeEmailRedirector.</para>
    /// </summary>
    string? BccAddress = null,
    /// <summary>
    /// A <b>visible</b> copy, set only by a message rule that asks for one (#401 PR4).
    ///
    /// <para>Distinct from <see cref="BccAddress"/> in the way that matters: everyone on the message
    /// sees this address, and the person at it cannot unsubscribe, because the footer's link belongs
    /// to the To recipient. That is why the admin form does not offer it on candidate-facing rules,
    /// and why it is null everywhere else in the app.</para>
    /// </summary>
    string? CcAddress = null);
