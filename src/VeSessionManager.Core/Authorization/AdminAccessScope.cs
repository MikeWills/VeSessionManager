using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
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
    /// <param name="availableTeamIds">
    /// The teams the page is offering in its picker, when the caller has them to hand. Supplied only
    /// so that a SystemAdmin on a deployment with exactly ONE team doesn't have to "choose" from a
    /// list of one before any admin page will show them anything (2026-08-04) — a TeamAdmin already
    /// got that for free via the effectiveTeamIds[0] fallback below, so this closes an asymmetry
    /// that punished the higher-privileged role. Deliberately only auto-selects at a count of one:
    /// with two or more, making the choice explicit is what stops an admin editing the wrong team's
    /// credentials by inheriting a stale selection. Omit it to keep the previous behaviour exactly.
    /// </param>
    public int? TryResolveManageableTeamId(User actingUser, int? requestedTeamId, IReadOnlyList<int>? availableTeamIds = null)
    {
        if (actingUser.Role == UserRole.SystemAdmin)
        {
            if (requestedTeamId is null && availableTeamIds is { Count: 1 })
            {
                return availableTeamIds[0];
            }

            return requestedTeamId;
        }

        var effectiveTeamIds = GetEffectiveTeamIds(actingUser) ?? [];
        if (requestedTeamId is not null && effectiveTeamIds.Contains(requestedTeamId.Value))
        {
            return requestedTeamId;
        }

        return effectiveTeamIds.Count > 0 ? effectiveTeamIds[0] : null;
    }

    /// <summary>
    /// The same resolution for a handler that is about to <b>write</b>, where the substitution above
    /// is wrong (issue #263).
    ///
    /// <para>On a GET, falling back to the acting user's first team is a kindness: a stale or
    /// hand-edited <c>?teamId=</c> lands them on a team they can actually see, which beats an error
    /// page. On a POST it means the write goes to a <i>different team than the URL named</i>, and the
    /// redirect afterwards reflects the substitution only once it has already happened. No
    /// cross-tenant access results — the resolved team is always one they manage — but a multi-team
    /// TeamAdmin following a wrong link can overwrite Team X's Square access token believing they are
    /// editing Team Y.</para>
    ///
    /// <para>So this one refuses rather than substitutes. Both exist because both behaviours are
    /// wanted; the bug was having only the forgiving one.</para>
    ///
    /// <para>Ambiguity is refused too: no requested id and more than one candidate returns null,
    /// rather than picking. That is the rule the SystemAdmin branch already applied via
    /// <c>availableTeamIds is { Count: 1 }</c>, now applied to everyone — a single-team admin, which
    /// is most of them, still needs to pass nothing.</para>
    /// </summary>
    public int? TryResolveManageableTeamIdForWrite(User actingUser, int? requestedTeamId, IReadOnlyList<int>? availableTeamIds = null)
    {
        if (actingUser.Role == UserRole.SystemAdmin)
        {
            if (requestedTeamId is not null)
            {
                return requestedTeamId;
            }

            return availableTeamIds is { Count: 1 } ? availableTeamIds[0] : null;
        }

        var effectiveTeamIds = GetEffectiveTeamIds(actingUser) ?? [];

        if (requestedTeamId is not null)
        {
            // The whole point: not manageable means no, never "have this one instead".
            return effectiveTeamIds.Contains(requestedTeamId.Value) ? requestedTeamId : null;
        }

        return effectiveTeamIds.Count == 1 ? effectiveTeamIds[0] : null;
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
    /// The teams this admin may pick from, as (id, name) — what every team picker on the admin side
    /// actually wants (#306, DUP-05).
    ///
    /// <para>Six call sites re-typed <c>ScopeTeams(...).OrderBy(t =&gt; t.Name).Select(...)</c>, and
    /// <see cref="ScopeTeams"/>'s own doc already said it existed to stop that. Mirrors
    /// <c>SessionAccessScope.GetAvailableTeamsAsync</c>, which is the same method on the other scope
    /// class — a page using the wrong one now gets a compile error rather than a subtly different
    /// team list.</para>
    ///
    /// <para><b>Projects in SQL, deliberately.</b> Materialising whole Team entities here would run
    /// every row through <c>EncryptedStringConverter</c>, decrypting each team's ExamTools/Zoom/
    /// Square/SMTP secrets on every render that shows a picker, to read two columns. That was a real
    /// fix on the sibling method; keeping the shape identical is the point of sharing.</para>
    /// </summary>
    public async Task<IReadOnlyList<(int Id, string Name)>> GetAvailableTeamsAsync(
        AppDbContext dbContext, User user, CancellationToken cancellationToken = default) =>
        await ScopeTeams(dbContext.Teams, user)
            .OrderBy(t => t.Name)
            .Select(t => new ValueTuple<int, string>(t.Id, t.Name))
            .ToListAsync(cancellationToken);

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
