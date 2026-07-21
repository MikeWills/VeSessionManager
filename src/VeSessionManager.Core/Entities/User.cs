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
    /// Not in the original shared data model — added in Phase 9a alongside the multi-team
    /// foundation reconciliation. Null for SystemAdmin (deployment-wide); required in practice for
    /// TeamAdmin/SessionManager (their "own team"); unused for TeamLead, whose effective team is
    /// resolved transitively through ManagedByUser instead (see SessionAccessScope).
    /// </summary>
    public int? TeamId { get; set; }
    public Team? Team { get; set; }

    /// <summary>TeamLead's assigned manager (a SessionManager or TeamAdmin) — role-agnostic, see SessionAccessScope.</summary>
    public int? ManagedByUserId { get; set; }
    public User? ManagedByUser { get; set; }
}
