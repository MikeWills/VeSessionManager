using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VeSessionManager.Core.Data;

namespace VeSessionManager.Core.Email;

/// <summary>
/// Substitutes {{Placeholder}} tokens into a message's subject and body. A placeholder
/// present in the caller's dictionary (even with an empty-string value, e.g. PaymentLinkUrl when
/// nothing's outstanding) substitutes cleanly. A placeholder the caller never provided a value
/// for at all — almost always a template typo — is left as the literal "{{Typo}}" text (so a
/// broken message is visibly broken, not silently mangled) and logged as a warning, per the spec.
///
/// Body is sent as real HTML (SmtpEmailSender sets HtmlBody), so placeholder values there are
/// HTML-encoded before substitution — several placeholders (CandidateName, etc.) ultimately come
/// from ExamTools' public registration intake, i.e. registrant-controlled data, and without
/// encoding an HTML/script-bearing name would be injected verbatim into a real HTML email rendered
/// by the recipient's mail client. Subject is plain text, so it's left unencoded.
///
/// Multi-team: message content is per-team (confirmed with the user), not shared — each Team writes
/// its own. See docs/multi-team.md.
///
/// <para><b>Renders text, never a row (2026-08-21).</b> This used to have a RenderAsync(teamId, key)
/// overload that loaded an EmailTemplate first. The table is gone — a message owns its words — and
/// every sender already went through RenderTextAsync anyway.</para>
/// </summary>
public partial class EmailTemplateRenderer(AppDbContext dbContext, ILogger<EmailTemplateRenderer> logger)
{
    /// <summary>
    /// The same rendering, over text that is <b>not</b> a stored template — a draft someone composed
    /// on the Email candidates screen, starting from one and editing it (#144).
    ///
    /// <para><b>This exists so there is not a second renderer.</b>
    /// <c>VeSessionInvitationService</c> had exactly this need and wrote its own <c>Replace</c> chain,
    /// which shipped without HTML-encoding: a session title carrying markup rendered as a live link in
    /// every invited VE's mail client, inside a genuine message from the team's real address (#260).
    /// Candidate names come from the same class of source — ExamTools' public registration intake — so
    /// the encoding rule, the subject line-break stripping (#261) and <c>{{Logo}}</c>'s
    /// raw-HTML-plus-attachment handling all have to be the ones below, not a second copy of
    /// them.</para>
    /// </summary>
    /// <param name="templateKey">Only for the log line naming an unknown placeholder. A composed draft passes the label it started from, so a typo is still attributable to something.</param>
    public async Task<RenderedEmail> RenderTextAsync(
        int teamId, string subject, string body, IReadOnlyDictionary<string, string> placeholders,
        string templateKey, CancellationToken cancellationToken)
    {
        // Only load the logo when the body actually asks for it — a template without {{Logo}} should
        // never pay the size cost of an attachment on every send.
        InlineImage? logo = null;
        var effectivePlaceholders = placeholders;
        if (LogoPlaceholderPattern().IsMatch(body))
        {
            var team = await dbContext.Teams.FirstOrDefaultAsync(t => t.Id == teamId, cancellationToken);
            if (team?.LogoBytes is { Length: > 0 } bytes)
            {
                logo = new InlineImage(InlineImage.TeamLogoContentId, team.LogoContentType ?? "image/png", bytes);
            }

            // Present either way. With a logo it becomes the <img> tag; without one it becomes an
            // empty string, so a template carrying {{Logo}} stays valid for a team that has not
            // uploaded one — rather than emitting a literal "{{Logo}}" into a candidate's inbox,
            // which is what the unknown-placeholder path would otherwise do.
            effectivePlaceholders = new Dictionary<string, string>(placeholders)
            {
                [LogoPlaceholder] = logo is null
                    ? string.Empty
                    : $"<img src=\"cid:{InlineImage.TeamLogoContentId}\" alt=\"\" style=\"max-width:220px;height:auto;border:0;\" />"
            };
        }

        return new RenderedEmail(
            Substitute(subject, effectivePlaceholders, templateKey, "Subject", encodeHtml: false),
            Substitute(body, effectivePlaceholders, templateKey, "Body", encodeHtml: true),
            logo);
    }

    /// <summary>
    /// The one placeholder whose value is HTML rather than text, and is therefore substituted
    /// **without** encoding.
    ///
    /// <para><b>This is a deliberate, narrow exception to the encoding rule in this class's summary,
    /// and the reason it is safe is that the value is built here, from a constant, out of app-owned
    /// data — not supplied by a caller and never derived from registrant input.</b> Every other
    /// placeholder ultimately traces back to ExamTools' public registration intake, which is exactly
    /// why they are encoded. Nothing registrant-controlled may ever be added to this set: doing so
    /// would inject attacker-authored markup straight into a real HTML email.</para>
    /// </summary>
    private const string LogoPlaceholder = "Logo";

    private string Substitute(string text, IReadOnlyDictionary<string, string> placeholders, string templateKey, string field, bool encodeHtml) =>
        PlaceholderPattern().Replace(text, match =>
        {
            var name = match.Groups[1].Value;
            if (placeholders.TryGetValue(name, out var value))
            {
                // The subject is plain text, so NOT HTML-encoding it is correct — but it is a mail
                // *header*, and a header ends at a newline. {{CandidateName}} comes from ExamTools'
                // public registration intake, so a name carrying CR/LF is attacker-controlled input
                // reaching a header builder (#261).
                //
                // MimeKit re-encodes headers and is generally not vulnerable to this, which is why
                // the finding is Low. That is still an undocumented assumption about a third-party
                // library standing between untrusted input and an injected header, so the control
                // characters are removed here rather than relied upon to be harmless downstream.
                // CR is dropped and LF becomes a space, so a two-line name stays readable.
                if (!encodeHtml)
                {
                    return StripLineBreaks(value);
                }

                // The one deliberate raw-HTML placeholder, in an HTML body. Line breaks are part of
                // the markup here and must survive — this is why the strip above is not applied to
                // every "raw" case.
                return name == LogoPlaceholder ? value : WebUtility.HtmlEncode(value);
            }

            logger.LogWarning("EmailTemplate {TemplateKey}.{Field} references unknown placeholder '{Placeholder}' — left unsubstituted, check for a typo",
                templateKey, field, name);
            return match.Value;
        });

    /// <summary>
    /// Removes the characters that end a mail header. Applied to substituted values only, never to
    /// the template text an admin wrote — a template author cannot put a newline in a subject
    /// through the editor anyway, and rewriting their text would be a surprise.
    /// </summary>
    private static string StripLineBreaks(string value) =>
        value.Contains('\r') || value.Contains('\n')
            ? value.Replace("\r", "").Replace('\n', ' ')
            : value;

    [GeneratedRegex(@"\{\{(\w+)\}\}")]
    private static partial Regex PlaceholderPattern();

    [GeneratedRegex(@"\{\{Logo\}\}")]
    private static partial Regex LogoPlaceholderPattern();
}
