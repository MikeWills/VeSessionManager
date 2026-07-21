using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Authorization;

/// <summary>
/// Phase 9c's authorization surface for the admin config screens — the "separate authorization
/// surface" <see cref="SessionAccessScope"/>'s own doc comment flags as settings/user-management
/// access, distinct from session visibility/editing. Same plain-C#, no-ASP.NET-dependency shape as
/// SessionAccessScope, and delegates team-resolution to it rather than duplicating that logic.
/// </summary>
public class AdminAccessScope(SessionAccessScope sessionAccessScope)
{
    public int? GetEffectiveTeamId(User user) => sessionAccessScope.GetEffectiveTeamId(user);

    /// <summary>Whether the acting user may manage the given team's settings/credentials. SystemAdmin: any team. TeamAdmin: only their own team.</summary>
    public bool CanManageTeam(User actingUser, int targetTeamId) =>
        actingUser.Role == UserRole.SystemAdmin || (actingUser.Role == UserRole.TeamAdmin && actingUser.TeamId == targetTeamId);

    /// <summary>TeamAdmin may only manage SessionManager/TeamLead users on their own team — never another TeamAdmin or a SystemAdmin, and never cross-team.</summary>
    public bool CanManageUser(User actingUser, User targetUser)
    {
        if (actingUser.Role == UserRole.SystemAdmin)
        {
            return true;
        }

        if (actingUser.Role != UserRole.TeamAdmin)
        {
            return false;
        }

        return targetUser.TeamId == actingUser.TeamId && targetUser.Role is UserRole.SessionManager or UserRole.TeamLead;
    }

    /// <summary>Whether the acting user may assign the given role to someone. TeamAdmin can only ever grant SessionManager/TeamLead, never TeamAdmin/SystemAdmin.</summary>
    public bool CanAssignRole(User actingUser, UserRole newRole) => actingUser.Role switch
    {
        UserRole.SystemAdmin => true,
        UserRole.TeamAdmin => newRole is UserRole.SessionManager or UserRole.TeamLead,
        _ => false
    };

    /// <summary>Vec management (shared/global reference data) and deployment-wide SystemSettings are SystemAdmin-only — TeamAdmin has no notion of "their own" VEC or deployment setting.</summary>
    public bool CanAccessVecManagement(User actingUser) => actingUser.Role == UserRole.SystemAdmin;

    public bool CanAccessSystemSettings(User actingUser) => actingUser.Role == UserRole.SystemAdmin;

    public bool CanCreateTeam(User actingUser) => actingUser.Role == UserRole.SystemAdmin;

    /// <summary>SystemAdmin sees every team (for the team-picker); TeamAdmin/SessionManager/TeamLead see only their own.</summary>
    public IQueryable<Team> ScopeTeams(IQueryable<Team> teams, User user) =>
        user.Role == UserRole.SystemAdmin ? teams : teams.Where(t => t.Id == user.TeamId);

    /// <summary>
    /// AuditLog has no TeamId column (only UserId), so TeamAdmin's view resolves via "actions
    /// performed by a user on my team" — a background-job entry (UserId null) about their team
    /// won't appear. Known, accepted limitation; fully fixing it means adding AuditLog.TeamId and
    /// populating it at every existing AddAudit call site across the app, out of scope here.
    /// </summary>
    public IQueryable<AuditLog> ScopeAuditLog(IQueryable<AuditLog> auditLogs, User user)
    {
        if (user.Role == UserRole.SystemAdmin)
        {
            return auditLogs;
        }

        var teamId = GetEffectiveTeamId(user);
        return auditLogs.Where(a => a.User != null && a.User.TeamId == teamId);
    }

    public IQueryable<JobRunHistory> ScopeJobRunHistory(IQueryable<JobRunHistory> jobRuns, User user) =>
        user.Role == UserRole.SystemAdmin ? jobRuns : jobRuns.Where(j => j.TeamId == GetEffectiveTeamId(user));
}
