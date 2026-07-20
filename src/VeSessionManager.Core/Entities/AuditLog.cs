namespace VeSessionManager.Core.Entities;

public class AuditLog
{
    public int Id { get; set; }

    /// <summary>Null when the action was taken by a background job rather than a person (e.g. Phase 1's reschedule-flagged audit entry).</summary>
    public int? UserId { get; set; }
    public User? User { get; set; }

    public required string Action { get; set; }
    public required string EntityType { get; set; }
    public int EntityId { get; set; }
    public DateTime TimestampUtc { get; set; }
    public string? Details { get; set; }
}
