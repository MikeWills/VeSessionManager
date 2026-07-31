using Microsoft.AspNetCore.Identity;

namespace VeSessionManager.Core.Entities;

/// <summary>
/// The ASP.NET Core Identity user (Phase 9a) — inherits UserName/Email (now nullable, superseding
/// the old required string Email)/PasswordHash/SecurityStamp/etc. from IdentityUser&lt;int&gt;.
/// Deliberately does NOT use Identity's own Role tables (AspNetRoles/UserRoles) — Role stays one
/// plain enum column, matching every other "pick one of N" field in this codebase (see
/// docs/admin-auth.md).
/// </summary>
public class User : IdentityUser<int>
{
    public required string Name { get; set; }

    /// <summary>
    /// The account holder's amateur call sign (requested 2026-07-30). Nullable — every existing
    /// account predates this field, and a Session Manager isn't required to be licensed for the app
    /// to work.
    ///
    /// <para>Stored upper-invariant, matching <see cref="VolunteerExaminer.CallSign"/>'s existing
    /// convention — normalize on write (see UserManagementService) so the two are comparable. This is
    /// deliberately *not* a foreign key to VolunteerExaminer: a VE row is team-scoped and synced from
    /// ExamTools, whereas a User is a login that may belong to several teams or none, so the same
    /// person can legitimately be one User and several VE rows. Matching them up is a separate
    /// question from recording the call sign here.</para>
    /// </summary>
    public string? CallSign { get; set; }

    public UserRole Role { get; set; }

    /// <summary>
    /// Replaces the old single, nullable TeamId (Phase 9a) — a TeamAdmin/SessionManager can now
    /// belong to more than one Team (issue #19). Empty for SystemAdmin (deployment-wide) and for
    /// TeamLead, whose effective teams are resolved transitively through ManagedByUser instead (see
    /// SessionAccessScope.GetEffectiveTeamIds).
    /// </summary>
    public List<UserTeam> UserTeams { get; } = [];

    /// <summary>TeamLead's assigned manager (a SessionManager or TeamAdmin) — role-agnostic, see SessionAccessScope.</summary>
    public int? ManagedByUserId { get; set; }
    public User? ManagedByUser { get; set; }
}
