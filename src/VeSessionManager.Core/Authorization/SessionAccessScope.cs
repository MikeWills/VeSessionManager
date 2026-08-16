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
/// TeamLead is scoped by their OWN team assignment, same as the others, and is read-only.
///
/// <para><b>It used to be scoped transitively through ManagedByUser, and that was wrong (2026-08-07).</b>
/// A manager can belong to several teams; a team lead belongs to one. Inheriting the manager's teams
/// therefore handed a lead visibility of every other team that manager worked on - a lead on HRCC
/// seeing WX0MIK's sessions and candidates because their SessionManager covers both. The manager link
/// survives as a record of who a lead reports to; it grants nothing.</para>
///
/// Multi-team (issue #19): a TeamAdmin/SessionManager can belong to more than one Team (User.UserTeams,
/// replacing the old single nullable User.TeamId) — every method here now returns/checks a *set* of
/// team ids rather than one scalar. Callers must have UserTeams loaded.
/// </summary>
public class SessionAccessScope
{
    /// <summary>
    /// The teams a user's session visibility is scoped to: null means "no filter" (SystemAdmin
    /// only); an empty list means "not assigned to any team yet" (correctly sees nothing, not
    /// everything). Every non-SystemAdmin role, TeamLead included, reads its own UserTeams.
    /// </summary>
    public IReadOnlyList<int>? GetEffectiveTeamIds(User user) => user.Role switch
    {
        UserRole.SystemAdmin => null,
        UserRole.TeamAdmin => user.UserTeams.Select(ut => ut.TeamId).ToList(),
        UserRole.SessionManager => user.UserTeams.Select(ut => ut.TeamId).ToList(),
        UserRole.TeamLead => user.UserTeams.Select(ut => ut.TeamId).ToList(),
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
        var teamIds = ResolveViewableTeamIds(user, selectedTeamId);
        return teamIds is null ? sessions : sessions.Where(s => teamIds.Contains(s.TeamId));
    }

    /// <summary>
    /// The team-id set a query should be filtered to, given the user and their team-picker choice.
    /// **null means "every team" (an unfiltered SystemAdmin), never "no teams"** — the same
    /// convention GetEffectiveTeamIds and NavBadgeCountService use. An empty list genuinely means
    /// the user belongs to no team.
    ///
    /// <para>Extracted from <see cref="Scope"/> (2026-07-30) so pages querying something other than
    /// Sessions — Applicant Status over Candidates, Unmatched Payments over UnmatchedSquarePayments —
    /// can apply identical team scoping instead of each inventing its own. That in turn is what lets
    /// those pages support "All teams" the way the session list already does. Contrast
    /// <see cref="TryResolveViewableTeamId"/>, which collapses to a *single* team and treats null as
    /// "no team context, show nothing" — the right shape for a page that genuinely cannot render
    /// without one team chosen, and the wrong shape for a merged list.</para>
    /// </summary>
    public IReadOnlyList<int>? ResolveViewableTeamIds(User user, int? selectedTeamId)
    {
        if (user.Role == UserRole.SystemAdmin)
        {
            return selectedTeamId is null ? null : [selectedTeamId.Value];
        }

        var effectiveTeamIds = GetEffectiveTeamIds(user) ?? [];
        return selectedTeamId is not null && effectiveTeamIds.Contains(selectedTeamId.Value)
            ? [selectedTeamId.Value]
            : effectiveTeamIds;
    }

    /// <summary>
    /// Whether the given user may view the given session at all — the read-only counterpart to
    /// CanEdit, used to gate page *display* (a 403/404 on GET) rather than write actions. Unlike
    /// CanEdit, TeamLead is not carved out here: a TeamLead assigned to a team can view that team's
    /// sessions, just never edit them.
    /// </summary>
    public bool CanView(User user, Session session) => CanView(user, session.TeamId);

    /// <summary>
    /// Team-id overload, for callers holding a projection rather than a loaded Session — the session
    /// list projects its rows instead of materializing every candidate (see SessionListRow). Both
    /// overloads only ever read TeamId, so this is the whole rule, not a weaker version of it.
    /// </summary>
    public bool CanView(User user, int teamId)
    {
        if (user.Role == UserRole.SystemAdmin)
        {
            return true;
        }

        return GetEffectiveTeamIds(user)?.Contains(teamId) ?? false;
    }

    /// <summary>
    /// Whether the given user may edit (not just view) the given session. TeamLead is always
    /// read-only, per spec, pending explicit sign-off on any TeamLead write access.
    /// </summary>
    public bool CanEdit(User user, Session session) => CanEdit(user, session.TeamId);

    /// <summary>Team-id overload — see the CanView overload for why this exists.</summary>
    public bool CanEdit(User user, int teamId)
    {
        if (user.Role == UserRole.TeamLead)
        {
            return false;
        }

        return CanView(user, teamId);
    }

    /// <summary>
    /// The single "which team is this user actually looking at right now" resolution for the
    /// per-team list pages (VE Roster, VEC Submission, Unmatched Payments, Fee Configurations) that
    /// show one team's data at a time rather than a mixed multi-team list like the session list.
    /// SystemAdmin uses whatever was requested (their own team-picker choice, or the sole team if
    /// the deployment only has one — there's no picker to make a choice with otherwise — or null if
    /// there's more than one and they haven't picked yet); everyone else picks from their own teams —
    /// the requested team if it's one of theirs, otherwise the first team they belong to (or null if
    /// they have none). Mirrors AdminAccessScope.TryResolveManageableTeamId's shape for the
    /// admin-config side (that one deliberately does NOT get the same single-team default — a null
    /// team there means "show every team merged," a valid state, not "nothing to show").
    /// </summary>
    public int? TryResolveViewableTeamId(User user, int? requestedTeamId, IReadOnlyList<(int Id, string Name)> availableTeams)
    {
        if (user.Role == UserRole.SystemAdmin)
        {
            return requestedTeamId ?? (availableTeams.Count == 1 ? availableTeams[0].Id : null);
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

        // Filter and project in SQL rather than materializing every Team. Loading whole entities
        // here was not just wasteful: Team's credential columns run through EncryptedStringConverter,
        // so this decrypted every team's ExamTools/Zoom/Square/SMTP secrets on every page render that
        // shows the team picker, purely to read two columns it already had one of.
        //
        // Note for anyone tempted to "optimize" a Team query with AsNoTracking() instead: that makes
        // it worse, not better. See EncryptedStringConverter's remarks.
        return await dbContext.Teams
            .Where(t => effectiveTeamIds.Contains(t.Id))
            .OrderBy(t => t.Name)
            .Select(t => new ValueTuple<int, string>(t.Id, t.Name))
            .ToListAsync();
    }
}
