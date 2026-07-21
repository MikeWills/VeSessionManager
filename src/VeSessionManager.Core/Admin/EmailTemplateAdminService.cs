using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Email;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Admin;

/// <summary>
/// Phase 9c: edit-only management of EmailTemplate Subject/Body. No create/delete — the set of Keys
/// is fixed by what CandidateNotificationService/PaymentReminderService actually look up (seeded
/// per-team by EmailDefaultsSeeder), matching the spec's exact wording ("edit Subject/Body per
/// EmailTemplate.Key"). A save takes effect on the very next send, with no deploy needed —
/// EmailTemplateRenderer reads the row fresh from the DB every time, no caching.
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

        template.Subject = subject;
        template.Body = body;

        var now = timeProvider.GetUtcNow().UtcDateTime;
        template.UpdatedByUserId = userId;
        template.UpdatedUtc = now;

        dbContext.AuditLogs.Add(new AuditLog
        {
            UserId = userId,
            Action = "EmailTemplateUpdated",
            EntityType = nameof(EmailTemplate),
            EntityId = template.Id,
            TimestampUtc = now,
            Details = $"Team {template.TeamId} template '{template.Key}' Subject/Body updated."
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        return EmailTemplateActionResult.Success;
    }

    /// <summary>Non-blocking warning helper — same {{Token}} pattern EmailTemplateRenderer itself uses, diffed against the registry for this Key. A likely typo, surfaced to the admin after save, never blocking it (mirrors EmailTemplateRenderer's own "log a warning, don't fail the send" behavior for an unknown token).</summary>
    public IReadOnlyList<string> FindUnknownPlaceholders(string templateKey, string subject, string body)
    {
        var known = EmailTemplatePlaceholders.For(templateKey);
        var found = PlaceholderPattern().Matches(subject + " " + body).Select(m => m.Groups[1].Value).Distinct();
        return found.Where(name => !known.Contains(name)).ToList();
    }

    [GeneratedRegex(@"\{\{(\w+)\}\}")]
    private static partial Regex PlaceholderPattern();
}

public enum EmailTemplateActionResult
{
    Success,
    NotFound
}
