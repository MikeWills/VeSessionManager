using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
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
/// User.UserTeams) — the only difference between those two roles is settings/user-management
/// access, a separate authorization surface Phase 9c will build, not this class's concern;
/// TeamLead is scoped transitively through whichever manager (SessionManager or TeamAdmin) they're
/// assigned to via ManagedByUserId, read-only.
///
/// Multi-team (issue #19): a TeamAdmin/SessionManager can belong to more than one Team (User.UserTeams,
/// replacing the old single nullable User.TeamId) — every method here now returns/checks a *set* of
/// team ids rather than one scalar. Callers must have UserTeams loaded (and, for a TeamLead,
/// ManagedByUser.UserTeams too) — same caller responsibility the old code already had for
/// ManagedByUser itself.
/// </summary>
public class SessionAccessScope
{
    /// <summary>
    /// The teams a user's session visibility is scoped to: null means "no filter" (SystemAdmin
    /// only); an empty list means "not assigned to any team yet" (correctly sees nothing, not
    /// everything). For a TeamLead, resolved transitively through ManagedByUser.UserTeams.
    /// </summary>
    public IReadOnlyList<int>? GetEffectiveTeamIds(User user) => user.Role switch
    {
        UserRole.SystemAdmin => null,
        UserRole.TeamAdmin => user.UserTeams.Select(ut => ut.TeamId).ToList(),
        UserRole.SessionManager => user.UserTeams.Select(ut => ut.TeamId).ToList(),
        UserRole.TeamLead => user.ManagedByUser?.UserTeams.Select(ut => ut.TeamId).ToList() ?? [],
        _ => []
    };

    /// <summary>
    /// Filters a Session query to what the given user is allowed to see. SystemAdmin gets the query
    /// back unfiltered unless a specific team was requested (the session list's own team filter —
    /// issue #17); everyone else is scoped to every team they belong to, or narrowed to just
    /// `selectedTeamId` when that's one of their own teams (a tampered/foreign teamId is silently
    /// ignored, falling back to "all my teams" rather than erroring).
    /// </summary>
    public IQueryable<Session> Scope(IQueryable<Session> sessions, User user, int? selectedTeamId = null)
    {
        if (user.Role == UserRole.SystemAdmin)
        {
            return selectedTeamId is null ? sessions : sessions.Where(s => s.TeamId == selectedTeamId);
        }

        var effectiveTeamIds = GetEffectiveTeamIds(user)!;
        if (selectedTeamId is not null && effectiveTeamIds.Contains(selectedTeamId.Value))
        {
            return sessions.Where(s => s.TeamId == selectedTeamId);
        }

        return sessions.Where(s => effectiveTeamIds.Contains(s.TeamId));
    }

    /// <summary>
    /// Whether the given user may view the given session at all — the read-only counterpart to
    /// CanEdit, used to gate page *display* (a 403/404 on GET) rather than write actions. Unlike
    /// CanEdit, TeamLead is not carved out here: a TeamLead assigned to a team can view that team's
    /// sessions, just never edit them.
    /// </summary>
    public bool CanView(User user, Session session)
    {
        if (user.Role == UserRole.SystemAdmin)
        {
            return true;
        }

        return GetEffectiveTeamIds(user)?.Contains(session.TeamId) ?? false;
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

        return CanView(user, session);
    }

    /// <summary>
    /// The single "which team is this user actually looking at right now" resolution for the
    /// per-team list pages (VE Roster, VEC Submission, Unmatched Payments, Fee Configurations) that
    /// show one team's data at a time rather than a mixed multi-team list like the session list.
    /// SystemAdmin uses whatever was requested (their own team-picker choice, or null if they
    /// haven't picked one yet); everyone else picks from their own teams — the requested team if
    /// it's one of theirs, otherwise the first team they belong to (or null if they have none).
    /// Mirrors AdminAccessScope.TryResolveManageableTeamId's shape for the admin-config side.
    /// </summary>
    public int? TryResolveViewableTeamId(User user, int? requestedTeamId)
    {
        if (user.Role == UserRole.SystemAdmin)
        {
            return requestedTeamId;
        }

        var effectiveTeamIds = GetEffectiveTeamIds(user) ?? [];
        if (requestedTeamId is not null && effectiveTeamIds.Contains(requestedTeamId.Value))
        {
            return requestedTeamId;
        }

        return effectiveTeamIds.Count > 0 ? effectiveTeamIds[0] : null;
    }

    /// <summary>
    /// The "which teams should this user's team-picker dropdown list" resolution — extracted
    /// 2026-07-29 after a duplicate-code review found the identical 5-line "SystemAdmin sees every
    /// team, everyone else joins GetEffectiveTeamIds against dbContext.Teams" block copy-pasted
    /// across Index/VeRoster/ApplicantStatus/VecSubmission/UnmatchedPayments' OnGetAsync methods.
    /// SystemAdmin sees every team (for the team-picker); everyone else sees only the team(s) they
    /// belong to, ordered by name either way.
    /// </summary>
    public async Task<IReadOnlyList<(int Id, string Name)>> GetAvailableTeamsAsync(AppDbContext dbContext, User user)
    {
        if (user.Role == UserRole.SystemAdmin)
        {
            return await dbContext.Teams.OrderBy(t => t.Name).Select(t => new ValueTuple<int, string>(t.Id, t.Name)).ToListAsync();
        }

        var effectiveTeamIds = GetEffectiveTeamIds(user) ?? [];
        return effectiveTeamIds
            .Join(await dbContext.Teams.ToListAsync(), id => id, t => t.Id, (_, t) => new ValueTuple<int, string>(t.Id, t.Name))
            .OrderBy(t => t.Item2)
            .ToList();
    }
}
