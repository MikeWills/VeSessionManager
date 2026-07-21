using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VeSessionManager.Core.Data;

namespace VeSessionManager.Core.Email;

/// <summary>
/// Loads a Team's EmailTemplate by Key and substitutes {{Placeholder}} tokens. A placeholder
/// present in the caller's dictionary (even with an empty-string value, e.g. PaymentLinkUrl when
/// nothing's outstanding) substitutes cleanly. A placeholder the caller never provided a value
/// for at all — almost always a template typo — is left as the literal "{{Typo}}" text (so a
/// broken template is visibly broken, not silently mangled) and logged as a warning, per the spec.
///
/// Body is sent as real HTML (SmtpEmailSender sets HtmlBody), so placeholder values there are
/// HTML-encoded before substitution — several placeholders (CandidateName, etc.) ultimately come
/// from ExamTools' public registration intake, i.e. registrant-controlled data, and without
/// encoding an HTML/script-bearing name would be injected verbatim into a real HTML email rendered
/// by the recipient's mail client. Subject is plain text, so it's left unencoded.
///
/// Multi-team: template content is per-team customizable (confirmed with the user), not shared —
/// each Team has its own full set of templates, keyed by (TeamId, Key). See docs/multi-team.md.
/// </summary>
public partial class EmailTemplateRenderer(AppDbContext dbContext, ILogger<EmailTemplateRenderer> logger)
{
    public async Task<RenderedEmail?> RenderAsync(int teamId, string templateKey, IReadOnlyDictionary<string, string> placeholders, CancellationToken cancellationToken)
    {
        var template = await dbContext.EmailTemplates.FirstOrDefaultAsync(t => t.TeamId == teamId && t.Key == templateKey, cancellationToken);
        if (template is null)
        {
            logger.LogError("No EmailTemplate found for team {TeamId}, key {TemplateKey} — cannot send", teamId, templateKey);
            return null;
        }

        return new RenderedEmail(
            Substitute(template.Subject, placeholders, templateKey, "Subject", encodeHtml: false),
            Substitute(template.Body, placeholders, templateKey, "Body", encodeHtml: true));
    }

    private string Substitute(string text, IReadOnlyDictionary<string, string> placeholders, string templateKey, string field, bool encodeHtml) =>
        PlaceholderPattern().Replace(text, match =>
        {
            var name = match.Groups[1].Value;
            if (placeholders.TryGetValue(name, out var value))
            {
                return encodeHtml ? WebUtility.HtmlEncode(value) : value;
            }

            logger.LogWarning("EmailTemplate {TemplateKey}.{Field} references unknown placeholder '{Placeholder}' — left unsubstituted, check for a typo",
                templateKey, field, name);
            return match.Value;
        });

    [GeneratedRegex(@"\{\{(\w+)\}\}")]
    private static partial Regex PlaceholderPattern();
}
