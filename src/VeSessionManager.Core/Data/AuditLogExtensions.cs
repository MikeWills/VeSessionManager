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
    public static void AddAuditLog(
        this AppDbContext dbContext, int? userId, string action, string entityType, int entityId, string details,
        DateTime timestampUtc, string? sourceIpAddress = null) =>
        dbContext.AuditLogs.Add(new AuditLog
        {
            UserId = userId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            TimestampUtc = timestampUtc,
            Details = details,
            SourceIpAddress = sourceIpAddress
        });
}
