using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.VolunteerExaminers;

/// <summary>
/// Reads the VE directory for the management screens (issue #142 phase 2) — the roster of people a
/// team works with, as opposed to <see cref="VolunteerExaminerReportService"/>, which counts
/// appearances at sessions.
///
/// <para>The two are deliberately separate. The report answers "how much has each person worked",
/// is a per-VE-per-team leaderboard, and predates this. This answers "who are our VEs, how do we
/// reach them, and are they current" — which is what issue #142 says the focus actually is.</para>
///
/// <para><b>Contact details never leave this service unfiltered.</b> Every caller is a page gated to
/// TeamAdmin/SystemAdmin, but the gate lives on the page and gates are forgotten, so the rows this
/// returns carry contact fields only because every current caller is entitled to them. A future
/// caller that is not must project to something narrower rather than filtering in Razor.</para>
/// </summary>
public class VolunteerExaminerDirectoryService(AppDbContext dbContext)
{
    /// <summary>
    /// One row per person per team they belong to — the same shape as the session-count report, so
    /// a VE serving two teams appears under each rather than being silently collapsed.
    /// </summary>
    /// <param name="teamIds">Null means every team (SystemAdmin, unfiltered) — the convention
    /// <c>SessionAccessScope.ResolveViewableTeamIds</c> already uses. An empty list means no team
    /// context and returns nothing.</param>
    /// <param name="includeInactive">Retired memberships are hidden by default: the roster answers
    /// "who can we call on", and a list that keeps everyone who ever served would stop being that.</param>
    public async Task<IReadOnlyList<VeDirectoryRow>> GetDirectoryAsync(
        IReadOnlyList<int>? teamIds, string? search, int? tagId, bool includeInactive, CancellationToken cancellationToken)
    {
        var query = dbContext.VeTeamMemberships
            .Include(m => m.VolunteerExaminer)
            .Include(m => m.Team)
            .Include(m => m.TagAssignments).ThenInclude(a => a.VeTag)
            .AsQueryable();

        if (teamIds is not null)
        {
            query = query.Where(m => teamIds.Contains(m.TeamId));
        }

        if (!includeInactive)
        {
            query = query.Where(m => m.IsActive);
        }

        if (tagId is { } id)
        {
            query = query.Where(m => m.TagAssignments.Any(a => a.VeTagId == id));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(m =>
                m.VolunteerExaminer.Name.ToLower().Contains(term)
                || (m.VolunteerExaminer.CallSign ?? "").ToLower().Contains(term)
                || (m.VolunteerExaminer.Email ?? "").ToLower().Contains(term));
        }

        var memberships = await query.ToListAsync(cancellationToken);
        if (memberships.Count == 0)
        {
            return [];
        }

        // "Last worked" is a MAX over the session links, fetched in one pass rather than per row.
        //
        // Scoped to the same team as the membership: a VE's most recent session for THIS team is the
        // useful figure, and their last outing anywhere would quietly answer a different question on
        // a page that is otherwise entirely per-team.
        //
        // Only sessions that actually happened count. Session.Status is not that signal — it only
        // ever means "not cancelled", so filtering on it would count a session scheduled for next
        // month as worked (the trap documented in CLAUDE.md, and found twice before).
        var veIds = memberships.Select(m => m.VolunteerExaminerId).Distinct().ToList();
        var lastWorked = await dbContext.SessionVolunteerExaminers
            .Where(sve => veIds.Contains(sve.VolunteerExaminerId)
                          && (sve.Session.TestingCompletedUtc != null || sve.Session.ExamToolsClosedUtc != null))
            .GroupBy(sve => new { sve.VolunteerExaminerId, sve.Session.TeamId })
            .Select(g => new { g.Key.VolunteerExaminerId, g.Key.TeamId, Last = g.Max(x => x.Session.ScheduledStartUtc) })
            .ToListAsync(cancellationToken);

        var lastByVeAndTeam = lastWorked.ToDictionary(x => (x.VolunteerExaminerId, x.TeamId), x => x.Last);

        // Possible duplicates from the phase 1 merge: rows sharing a call sign that were left
        // separate because their names disagreed. Surfaced rather than resolved automatically —
        // merging two people is not reversible, so a human decides.
        var sharedCallSigns = await dbContext.VolunteerExaminers
            .Where(v => v.CallSign != null)
            .GroupBy(v => v.CallSign!)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToListAsync(cancellationToken);
        var duplicateCallSigns = sharedCallSigns.ToHashSet(StringComparer.OrdinalIgnoreCase);

        return [.. memberships
            .Select(m => new VeDirectoryRow(
                m.VolunteerExaminer,
                m.Id,
                m.TeamId,
                m.Team.Name,
                m.IsActive,
                [.. m.TagAssignments.Select(a => a.VeTag).OrderBy(t => t.SortOrder).ThenBy(t => t.Name)],
                lastByVeAndTeam.TryGetValue((m.VolunteerExaminerId, m.TeamId), out var last) ? last : null,
                m.VolunteerExaminer.CallSign is { } call && duplicateCallSigns.Contains(call)))
            .OrderBy(r => r.VolunteerExaminer.Name)
            .ThenBy(r => r.TeamName)];
    }

    /// <summary>Everything one person's detail screen needs, or null when the id doesn't exist.</summary>
    public Task<VolunteerExaminer?> GetPersonAsync(int volunteerExaminerId, CancellationToken cancellationToken) =>
        dbContext.VolunteerExaminers
            .Include(v => v.TeamMemberships).ThenInclude(m => m.Team)
            .Include(v => v.TeamMemberships).ThenInclude(m => m.TagAssignments).ThenInclude(a => a.VeTag)
            .Include(v => v.VecAccreditations).ThenInclude(a => a.Vec)
            .Include(v => v.CallSignHistory)
            .FirstOrDefaultAsync(v => v.Id == volunteerExaminerId, cancellationToken);
}

/// <summary>
/// One person as seen by one team. <see cref="IsGuest"/> is derived, never stored — a stored "guest"
/// tag would have to be added and removed in step with every other tag change and would be wrong in
/// between.
/// </summary>
public record VeDirectoryRow(
    VolunteerExaminer VolunteerExaminer,
    int MembershipId,
    int TeamId,
    string TeamName,
    bool IsActive,
    IReadOnlyList<VeTag> Tags,
    DateTime? LastWorkedUtc,
    bool HasDuplicateCallSign)
{
    public bool IsGuest => Tags.Count == 0;
}
