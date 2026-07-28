using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Authorization;

/// <summary>
/// Phase 9c's authorization surface for the admin config screens — the "separate authorization
/// surface" <see cref="SessionAccessScope"/>'s own doc comment flags as settings/user-management
/// access, distinct from session visibility/editing. Same plain-C#, no-ASP.NET-dependency shape as
/// SessionAccessScope, and delegates team-resolution to it rather than duplicating that logic.
///
/// Multi-team (issue #19): a TeamAdmin can now belong to more than one Team, so "which team(s) can
/// this TeamAdmin manage" is a set, not a scalar — see SessionAccessScope.GetEffectiveTeamIds.
/// </summary>
public class AdminAccessScope(SessionAccessScope sessionAccessScope)
{
    public IReadOnlyList<int>? GetEffectiveTeamIds(User user) => sessionAccessScope.GetEffectiveTeamIds(user);

    /// <summary>Whether the acting user may manage the given team's settings/credentials. SystemAdmin: any team. TeamAdmin: any of their own teams.</summary>
    public bool CanManageTeam(User actingUser, int targetTeamId) =>
        actingUser.Role == UserRole.SystemAdmin || (actingUser.Role == UserRole.TeamAdmin && (GetEffectiveTeamIds(actingUser)?.Contains(targetTeamId) ?? false));

    /// <summary>
    /// The single "which team is this SystemAdmin/TeamAdmin actually allowed to manage right now"
    /// resolution: SystemAdmin uses whatever teamId was requested (their own team-picker choice, or
    /// null if they haven't picked one yet); TeamAdmin picks from their own teams — the requested
    /// team if it's one of theirs, otherwise defaults to the first team they manage (or null if
    /// they have none) — same shape as SessionAccessScope.TryResolveViewableTeamId, since a TeamAdmin
    /// can now legitimately manage more than one team and needs the same kind of picker. Returns
    /// null for "nothing to show yet" (SystemAdmin hasn't picked, or TeamAdmin has no teams) as well
    /// as "not allowed" (requesting a team they don't belong to falls back to their own default
    /// instead of being granted) — the caller decides which response that maps to (an empty Page()
    /// vs. Forbid()). Previously this exact resolution was hand-written independently at every admin
    /// config page's OnGetAsync/AuthorizeAsync, including twice within TeamSettingsModel itself.
    /// </summary>
    public int? TryResolveManageableTeamId(User actingUser, int? requestedTeamId)
    {
        if (actingUser.Role == UserRole.SystemAdmin)
        {
            return requestedTeamId;
        }

        var effectiveTeamIds = GetEffectiveTeamIds(actingUser) ?? [];
        if (requestedTeamId is not null && effectiveTeamIds.Contains(requestedTeamId.Value))
        {
            return requestedTeamId;
        }

        return effectiveTeamIds.Count > 0 ? effectiveTeamIds[0] : null;
    }

    /// <summary>TeamAdmin may only manage SessionManager/TeamLead users who share at least one team with them — never another TeamAdmin or a SystemAdmin, and never a user on none of their teams.</summary>
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

        var effectiveTeamIds = GetEffectiveTeamIds(actingUser) ?? [];
        var sharesATeam = targetUser.UserTeams.Any(ut => effectiveTeamIds.Contains(ut.TeamId));
        return sharesATeam && targetUser.Role is UserRole.SessionManager or UserRole.TeamLead;
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

    /// <summary>SystemAdmin sees every team (for the team-picker); TeamAdmin/SessionManager/TeamLead see only the team(s) they belong to.</summary>
    public IQueryable<Team> ScopeTeams(IQueryable<Team> teams, User user)
    {
        if (user.Role == UserRole.SystemAdmin)
        {
            return teams;
        }

        var effectiveTeamIds = GetEffectiveTeamIds(user) ?? [];
        return teams.Where(t => effectiveTeamIds.Contains(t.Id));
    }

    /// <summary>
    /// AuditLog has no TeamId column (only UserId), so TeamAdmin's view resolves via "actions
    /// performed by a user who shares one of my teams" — a background-job entry (UserId null) about
    /// their team won't appear. Known, accepted limitation; fully fixing it means adding
    /// AuditLog.TeamId and populating it at every existing AddAudit call site across the app, out of
    /// scope here.
    /// </summary>
    public IQueryable<AuditLog> ScopeAuditLog(IQueryable<AuditLog> auditLogs, User user)
    {
        if (user.Role == UserRole.SystemAdmin)
        {
            return auditLogs;
        }

        var effectiveTeamIds = GetEffectiveTeamIds(user) ?? [];
        return auditLogs.Where(a => a.User != null && a.User.UserTeams.Any(ut => effectiveTeamIds.Contains(ut.TeamId)));
    }

    public IQueryable<JobRunHistory> ScopeJobRunHistory(IQueryable<JobRunHistory> jobRuns, User user)
    {
        if (user.Role == UserRole.SystemAdmin)
        {
            return jobRuns;
        }

        var effectiveTeamIds = GetEffectiveTeamIds(user) ?? [];
        return jobRuns.Where(j => j.TeamId != null && effectiveTeamIds.Contains(j.TeamId.Value));
    }
}
