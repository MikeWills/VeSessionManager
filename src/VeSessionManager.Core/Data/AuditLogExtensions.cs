using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Data;

/// <summary>
/// Single definition of "build and stage an AuditLog row" — previously a private AddAudit helper
/// (or an inline object initializer) re-declared independently across TeamSettingsService,
/// VecManagementService, FeeConfigurationService, UserManagementService, CandidateActionService,
/// PiiPurgeService, EmailTemplateAdminService, SessionActionService, and VecSubmissionService.
/// </summary>
public static class AuditLogExtensions
{
    /// <param name="sourceIpAddress">
    /// Set for authentication events and PII export only — see <see cref="AuditLog.SourceIpAddress"/>
    /// for why this is not simply passed everywhere. Optional so the ~175 ordinary call sites did not
    /// have to change, and so that adding it to one is a deliberate act rather than a default.
    /// </param>
    /// <param name="teamId">
    /// Set on <b>background-job</b> call sites — the ones passing a null <paramref name="userId"/> —
    /// so a TeamAdmin can see them at all (#86 part 3). A user-attributed entry does not need it: it
    /// already scopes through the acting user's own team memberships, and setting both would be two
    /// sources of truth for one question. Pass null when the action genuinely belongs to no single
    /// team, which is the honest answer for anything acting on a <c>VolunteerExaminer</c>. See
    /// <see cref="AuditLog.TeamId"/>.
    /// </param>
    public static void AddAuditLog(
        this AppDbContext dbContext, int? userId, string action, string entityType, int entityId, string details,
        DateTime timestampUtc, string? sourceIpAddress = null, int? teamId = null) =>
        dbContext.AuditLogs.Add(new AuditLog
        {
            UserId = userId,
            TeamId = teamId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            TimestampUtc = timestampUtc,
            Details = details,
            SourceIpAddress = sourceIpAddress
        });
}
