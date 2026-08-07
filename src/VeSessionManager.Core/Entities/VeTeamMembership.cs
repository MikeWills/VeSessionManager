namespace VeSessionManager.Core.Entities;

/// <summary>
/// One VE's place on one team — the per-team half of what <see cref="VolunteerExaminer"/> used to
/// hold in a <c>TeamId</c> column (issue #142).
///
/// <para>Tags hang off the membership rather than the person on purpose: someone can be a full team
/// member of their home team and a guest on another, and a single set of tags on the person could
/// not say that.</para>
///
/// <para><b>Created by the sync, never removed by it.</b> Working a session for a team is what
/// establishes membership, so <c>VolunteerExaminerSyncService</c> adds a row the first time it sees
/// a VE on that team's roster. It never deletes one and never touches <see cref="IsActive"/> —
/// otherwise an admin inactivating someone would find it silently undone the next time that VE
/// worked a session. Deactivation is a human decision; ExamTools has no opinion about it.</para>
/// </summary>
public class VeTeamMembership
{
    public int Id { get; set; }

    public int VolunteerExaminerId { get; set; }
    public VolunteerExaminer VolunteerExaminer { get; set; } = null!;

    public int TeamId { get; set; }
    public Team Team { get; set; } = null!;

    /// <summary>
    /// False once an admin retires this VE from the team. <b>There is no delete.</b> A person can be
    /// on another team, and session history references them by id — a removed row would either
    /// orphan that history or quietly rewrite who ran a past session.
    /// </summary>
    public bool IsActive { get; set; } = true;

    public DateTime? InactivatedUtc { get; set; }

    /// <summary>When this VE was first seen on this team — from the sync, or the moment an admin added them by hand.</summary>
    public DateTime CreatedUtc { get; set; }

    public List<VeTagAssignment> TagAssignments { get; } = [];
}
