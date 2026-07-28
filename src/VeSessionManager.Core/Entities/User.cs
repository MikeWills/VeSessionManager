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
