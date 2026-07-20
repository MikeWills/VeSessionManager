namespace VeSessionManager.Core.Entities;

/// <summary>
/// Not in the original shared data model — added as a multi-team foundation. A Team is the group
/// of VEs operating a deployment of this app (holds Discord/Zoom/ExamTools/Square credentials);
/// a Vec is the FCC-recognized coordinating org (ARRL, W5YI, etc.) a team's sessions are run
/// under. The hierarchy is VEC ⇒ Team ⇒ VE, not the reverse — Vec is deliberately NOT owned by
/// Team here (see docs/multi-team.md): it stays a shared/global reference table, since a VEC
/// dictates fees universally, not per-team, and the same VEC can be shared by multiple teams. A
/// Session references both independently (VecId for its fee schedule, TeamId for which team ran
/// it) with no relationship required between Vec and Team themselves.
/// </summary>
public class Team
{
    public int Id { get; set; }
    public required string Name { get; set; }

    // ExamTools credentials — nullable, same "graceful skip until configured" pattern as every
    // other integration's IsConfigured gate in this codebase (Zoom/Discord/Square/Email), just
    // living on the entity now instead of a client, since credentials are per-Team, not global.
    /// <summary>The sessions API's "?team=" filter value, e.g. WX0MIK.</summary>
    public string? ExamToolsTeamCode { get; set; }
    public string? ExamToolsUsername { get; set; }
    public string? ExamToolsPassword { get; set; }

    // Zoom credentials — nullable. Unlike ExamTools/Square/Email, this team's own separate Zoom
    // subscription/S2S OAuth app (confirmed with the user — not shared across teams).
    public string? ZoomAccountId { get; set; }
    public string? ZoomClientId { get; set; }
    public string? ZoomClientSecret { get; set; }
    /// <summary>Which Zoom user's calendar meetings get created under — defaults to "me" in code (ZoomClient) when null, not required to be set explicitly.</summary>
    public string? ZoomUserId { get; set; }

    public DateTime CreatedUtc { get; set; }

    public List<Session> Sessions { get; } = [];

    public bool IsExamToolsConfigured =>
        !string.IsNullOrWhiteSpace(ExamToolsTeamCode)
        && !string.IsNullOrWhiteSpace(ExamToolsUsername)
        && !string.IsNullOrWhiteSpace(ExamToolsPassword);

    public bool IsZoomConfigured =>
        !string.IsNullOrWhiteSpace(ZoomAccountId)
        && !string.IsNullOrWhiteSpace(ZoomClientId)
        && !string.IsNullOrWhiteSpace(ZoomClientSecret);
}
