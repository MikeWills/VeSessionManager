using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VeSessionManager.Core.Data;

namespace VeSessionManager.Core.Email;

/// <summary>
/// Loads an EmailTemplate by Key and substitutes {{Placeholder}} tokens. A placeholder present in
/// the caller's dictionary (even with an empty-string value, e.g. PaymentLinkUrl when nothing's
/// outstanding) substitutes cleanly. A placeholder the caller never provided a value for at all —
/// almost always a template typo — is left as the literal "{{Typo}}" text (so a broken template
/// is visibly broken, not silently mangled) and logged as a warning, per the spec.
/// </summary>
public partial class EmailTemplateRenderer(AppDbContext dbContext, ILogger<EmailTemplateRenderer> logger)
{
    public async Task<RenderedEmail?> RenderAsync(string templateKey, IReadOnlyDictionary<string, string> placeholders, CancellationToken cancellationToken)
    {
        var template = await dbContext.EmailTemplates.FirstOrDefaultAsync(t => t.Key == templateKey, cancellationToken);
        if (template is null)
        {
            logger.LogError("No EmailTemplate found for key {TemplateKey} — cannot send", templateKey);
            return null;
        }

        return new RenderedEmail(
            Substitute(template.Subject, placeholders, templateKey, "Subject"),
            Substitute(template.Body, placeholders, templateKey, "Body"));
    }

    private string Substitute(string text, IReadOnlyDictionary<string, string> placeholders, string templateKey, string field) =>
        PlaceholderPattern().Replace(text, match =>
        {
            var name = match.Groups[1].Value;
            if (placeholders.TryGetValue(name, out var value))
            {
                return value;
            }

            logger.LogWarning("EmailTemplate {TemplateKey}.{Field} references unknown placeholder '{Placeholder}' — left unsubstituted, check for a typo",
                templateKey, field, name);
            return match.Value;
        });

    [GeneratedRegex(@"\{\{(\w+)\}\}")]
    private static partial Regex PlaceholderPattern();
}
