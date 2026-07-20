namespace VeSessionManager.Core.Entities;

public class VolunteerExaminer
{
    public int Id { get; set; }
    public required string Name { get; set; }

    /// <summary>Always stored upper-invariant — VolunteerExaminerSyncService normalizes on write and matches on it, so a mixed-case manual entry (once Phase 9 exists) must follow the same convention.</summary>
    public string? CallSign { get; set; }
    public string? Frn { get; set; }

    /// <summary>Not in the original shared data model — added for Phase 7, per the multi-team foundation's own note that VEs belong to a Team (see Team's doc comment / docs/multi-team.md). Scopes the (TeamId, CallSign) uniqueness a VE is matched on during roster sync.</summary>
    public int TeamId { get; set; }
    public Team Team { get; set; } = null!;

    public List<SessionVolunteerExaminer> SessionVolunteerExaminers { get; } = [];
}
