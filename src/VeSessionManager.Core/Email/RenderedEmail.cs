namespace VeSessionManager.Core.Email;

/// <summary>
/// The result of rendering a template. <paramref name="InlineLogo"/> is non-null only when the body
/// actually references <c>{{Logo}}</c> *and* the team has one uploaded — the caller passes it
/// straight through to <see cref="EmailMessage"/>, so a template without the placeholder never pays
/// the size cost of an attachment.
/// </summary>
public record RenderedEmail(string Subject, string Body, InlineImage? InlineLogo = null);
