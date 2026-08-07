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
    public static void AddAuditLog(this AppDbContext dbContext, int? userId, string action, string entityType, int entityId, string details, DateTime timestampUtc) =>
        dbContext.AuditLogs.Add(new AuditLog
        {
            UserId = userId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            TimestampUtc = timestampUtc,
            Details = details
        });
}
