using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Authorization;

/// <summary>
/// Phase 9a's "authorization scoping rules" mechanism — plain, no ASP.NET Core dependency, so it's
/// directly unit-testable (per the spec's own guidance: test role-based query filtering, not the
/// OAuth flows themselves). Wired into a real session-list page in Phase 9b; 9a builds and tests
/// it now with barely any real data to filter yet.
///
/// See docs/admin-auth.md for the full 4-role hierarchy this implements: SystemAdmin sees
/// everything; TeamAdmin and SessionManager are equivalent here (both resolve to their own
/// User.TeamId) — the only difference between those two roles is settings/user-management access,
/// a separate authorization surface Phase 9c will build, not this class's concern; TeamLead is
/// scoped transitively through whichever manager (SessionManager or TeamAdmin) they're assigned
/// to via ManagedByUserId, read-only.
/// </summary>
public class SessionAccessScope
{
    /// <summary>
    /// The team a user's session visibility is scoped to, or null for "no filter" (SystemAdmin
    /// only). For a TeamLead, the caller must have ManagedByUser loaded/included — this reads
    /// user.ManagedByUser.TeamId directly rather than querying, so it works uniformly whether the
    /// manager is a SessionManager or a TeamAdmin.
    /// </summary>
    public int? GetEffectiveTeamId(User user) => user.Role switch
    {
        UserRole.SystemAdmin => null,
        UserRole.TeamAdmin => user.TeamId,
        UserRole.SessionManager => user.TeamId,
        UserRole.TeamLead => user.ManagedByUser?.TeamId,
        _ => null
    };

    /// <summary>
    /// Filters a Session query to what the given user is allowed to see. SystemAdmin gets the
    /// query back unfiltered; everyone else is scoped to their effective team — a non-SystemAdmin
    /// with no effective team (e.g. a TeamAdmin/SessionManager not yet assigned to a Team, or a
    /// TeamLead not yet assigned a manager) correctly sees nothing, not everything.
    /// </summary>
    public IQueryable<Session> Scope(IQueryable<Session> sessions, User user)
    {
        if (user.Role == UserRole.SystemAdmin)
        {
            return sessions;
        }

        var effectiveTeamId = GetEffectiveTeamId(user);
        return sessions.Where(s => s.TeamId == effectiveTeamId);
    }

    /// <summary>
    /// Whether the given user may edit (not just view) the given session. TeamLead is always
    /// read-only, per spec, pending explicit sign-off on any TeamLead write access.
    /// </summary>
    public bool CanEdit(User user, Session session)
    {
        if (user.Role == UserRole.TeamLead)
        {
            return false;
        }

        if (user.Role == UserRole.SystemAdmin)
        {
            return true;
        }

        return GetEffectiveTeamId(user) == session.TeamId;
    }
}
