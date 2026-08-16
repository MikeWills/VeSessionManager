using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Email;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Admin;

/// <summary>
/// Phase 9c: management of EmailTemplate Subject/Body. A save takes effect on the very next send,
/// with no deploy needed — EmailTemplateRenderer reads the row fresh from the DB every time, no
/// caching.
///
/// <para><b>This was edit-only, on the stated grounds that "the set of Keys is fixed by what
/// CandidateNotificationService/PaymentReminderService actually look up".</b> That reasoning was
/// right and still holds — which is exactly why create/delete is safe for a second population of
/// rows (#144): a <see cref="EmailTemplate.IsUserDefined"/> template is never looked up by anything.
/// A person picks it on a session's Email candidates screen, so no code path can break by its
/// absence, and none can be pointed at a template that no longer exists.</para>
///
/// <para>The two populations are kept apart by <see cref="EmailTemplate.Key"/>: generated keys carry
/// a <c>Custom.</c> prefix, and no shipped key contains a dot. Rename and delete refuse a system
/// template here rather than relying on the admin page not to offer the buttons.</para>
/// </summary>
public partial class EmailTemplateAdminService(AppDbContext dbContext, TimeProvider timeProvider)
{
    public async Task<EmailTemplateActionResult> UpdateAsync(int emailTemplateId, string subject, string body, int userId, CancellationToken cancellationToken)
    {
        var template = await dbContext.EmailTemplates.FirstOrDefaultAsync(t => t.Id == emailTemplateId, cancellationToken);
        if (template is null)
        {
            return EmailTemplateActionResult.NotFound;
        }

        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(body))
        {
            return EmailTemplateActionResult.ContentRequired;
        }

        template.Subject = subject;
        template.Body = body;

        var now = timeProvider.GetUtcNow().UtcDateTime;
        template.UpdatedByUserId = userId;
        template.UpdatedUtc = now;

        dbContext.AddAuditLog(userId, "EmailTemplateUpdated", nameof(EmailTemplate), template.Id, $"Team {template.TeamId} template '{template.Key}' Subject/Body updated.", now);

        await dbContext.SaveChangesAsync(cancellationToken);
        return EmailTemplateActionResult.Success;
    }

    /// <summary>
    /// The prefix every generated key carries. <b>The dot is the whole mechanism</b>: no shipped key
    /// contains one, so a name somebody types can never become a key the sending code looks up — now
    /// or after a future template is added.
    /// </summary>
    public const string UserDefinedKeyPrefix = "Custom.";

    /// <summary>Creates a template a team wrote for itself (#144). Nothing sends it; it exists to be picked as the starting text on the Email candidates screen.</summary>
    public async Task<EmailTemplateActionResult> CreateAsync(
        int teamId, string name, string subject, string body, EmailTemplateAudience audience, int userId, CancellationToken cancellationToken)
    {
        var trimmedName = name?.Trim() ?? "";
        if (trimmedName.Length == 0)
        {
            return EmailTemplateActionResult.NameRequired;
        }

        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(body))
        {
            return EmailTemplateActionResult.ContentRequired;
        }

        var template = new EmailTemplate
        {
            TeamId = teamId,
            Key = await GenerateKeyAsync(teamId, trimmedName, cancellationToken),
            DisplayName = trimmedName,
            IsUserDefined = true,
            // Asked once, at creation, rather than inferred from the body: the two audiences have
            // different tokens, and a candidate template used on VEs reaches them with a literal
            // {{CandidateFirstName}} in the text.
            Audience = audience,
            Subject = subject,
            Body = body
        };
        dbContext.EmailTemplates.Add(template);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        template.UpdatedByUserId = userId;
        template.UpdatedUtc = now;

        dbContext.AddAuditLog(userId, "EmailTemplateCreated", nameof(EmailTemplate), template.Id,
            $"Team {teamId} template '{trimmedName}' created for {audience}.", now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return EmailTemplateActionResult.Success;
    }

    /// <summary>
    /// Changes what a user-defined template is called. <b>The key deliberately does not move</b>: a
    /// rename is a label change, and regenerating the key would strand any open compose screen and
    /// make the row look like a different template. History rows keep the label they were sent under,
    /// which is correct — they record what was actually sent, not what it is called today.
    /// </summary>
    public async Task<EmailTemplateActionResult> RenameAsync(
        int emailTemplateId, string name, int userId, CancellationToken cancellationToken)
    {
        var template = await dbContext.EmailTemplates.FirstOrDefaultAsync(t => t.Id == emailTemplateId, cancellationToken);
        if (template is null)
        {
            return EmailTemplateActionResult.NotFound;
        }

        if (!template.IsUserDefined)
        {
            return EmailTemplateActionResult.NotUserDefined;
        }

        var trimmedName = name?.Trim() ?? "";
        if (trimmedName.Length == 0)
        {
            return EmailTemplateActionResult.NameRequired;
        }

        template.DisplayName = trimmedName;

        var now = timeProvider.GetUtcNow().UtcDateTime;
        template.UpdatedByUserId = userId;
        template.UpdatedUtc = now;

        dbContext.AddAuditLog(userId, "EmailTemplateRenamed", nameof(EmailTemplate), template.Id,
            $"Team {template.TeamId} template renamed to '{trimmedName}'.", now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return EmailTemplateActionResult.Success;
    }

    /// <summary>
    /// Deletes a user-defined template. Refuses a system one: something in the code sends it by key,
    /// and a team that deleted it would get a failed send with one log line nobody reads.
    ///
    /// <para>A real delete rather than a soft one, and the reason it is safe is that nothing points
    /// at the row: <c>CandidateEmailSend</c> stores the label as a string precisely so the record of
    /// what somebody was told outlives the template it was written from.</para>
    /// </summary>
    public async Task<EmailTemplateActionResult> DeleteAsync(int emailTemplateId, int userId, CancellationToken cancellationToken)
    {
        var template = await dbContext.EmailTemplates.FirstOrDefaultAsync(t => t.Id == emailTemplateId, cancellationToken);
        if (template is null)
        {
            return EmailTemplateActionResult.NotFound;
        }

        if (!template.IsUserDefined)
        {
            return EmailTemplateActionResult.NotUserDefined;
        }

        dbContext.EmailTemplates.Remove(template);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        dbContext.AddAuditLog(userId, "EmailTemplateDeleted", nameof(EmailTemplate), template.Id,
            $"Team {template.TeamId} template '{template.DisplayName}' deleted.", now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return EmailTemplateActionResult.Success;
    }

    /// <summary>
    /// <c>Custom.&lt;slug&gt;</c>, with a numeric suffix if that team already has one — (TeamId, Key)
    /// is unique, so two templates named the same thing must not collide at the database and must not
    /// silently overwrite each other either.
    ///
    /// <para>A name of nothing but punctuation slugs to an empty string, which would make every such
    /// name the same key; <c>template</c> stands in so the suffix logic still separates them.</para>
    /// </summary>
    private async Task<string> GenerateKeyAsync(int teamId, string name, CancellationToken cancellationToken)
    {
        var slug = NonSlugCharacters().Replace(name.ToLowerInvariant(), "-").Trim('-');
        if (slug.Length == 0)
        {
            slug = "template";
        }

        if (slug.Length > 60)
        {
            slug = slug[..60].TrimEnd('-');
        }

        var existing = await dbContext.EmailTemplates
            .Where(t => t.TeamId == teamId)
            .Select(t => t.Key)
            .ToListAsync(cancellationToken);

        var key = UserDefinedKeyPrefix + slug;
        var suffix = 2;
        while (existing.Contains(key))
        {
            key = $"{UserDefinedKeyPrefix}{slug}-{suffix++}";
        }

        return key;
    }

    /// <summary>Non-blocking warning helper — same {{Token}} pattern EmailTemplateRenderer itself uses, diffed against the registry for this Key. A likely typo, surfaced to the admin after save, never blocking it (mirrors EmailTemplateRenderer's own "log a warning, don't fail the send" behavior for an unknown token).</summary>
    public IReadOnlyList<string> FindUnknownPlaceholders(string templateKey, string subject, string body)
    {
        // A team-defined key has no registry entry — it gets the candidate token set, which is what
        // the compose screen resolves for every draft.
        var known = templateKey.StartsWith(UserDefinedKeyPrefix, StringComparison.Ordinal)
            ? EmailTemplatePlaceholders.ForUserDefined()
            : EmailTemplatePlaceholders.For(templateKey);
        var found = PlaceholderPattern().Matches(subject + " " + body).Select(m => m.Groups[1].Value).Distinct();
        return found.Where(name => !known.Contains(name)).ToList();
    }

    [GeneratedRegex(@"\{\{(\w+)\}\}")]
    private static partial Regex PlaceholderPattern();

    /// <summary>Anything that is not a letter, digit or dash becomes one — the key is an identifier, not a label.</summary>
    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonSlugCharacters();
}

public enum EmailTemplateActionResult
{
    Success,
    NotFound,

    /// <summary>
    /// Subject or body arrived blank (issue #275). Worse than the null case that throws: a blank
    /// value saves cleanly and the template stays "configured", so the next candidate email goes out
    /// with an empty subject or an empty body.
    /// </summary>
    ContentRequired,

    /// <summary>A team-defined template arrived with no name. It is the only thing anyone will ever see it by, so a blank one is not a template.</summary>
    NameRequired,

    /// <summary>
    /// Rename or delete was aimed at a shipped template (#144). Refused here rather than left to the
    /// admin page not to offer the button: something in the code sends that key, and it has no other
    /// way to find it.
    /// </summary>
    NotUserDefined
}
