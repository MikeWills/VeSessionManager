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
    /// Pass as <c>tagName</c> to filter to <b>guests</b> — people carrying no tag at all on any team
    /// in scope. "Guest" is derived rather than stored (a stored guest tag would need adding and
    /// removing in step with every other tag change, and would be wrong in between), so it cannot be
    /// selected the way a real tag name is, and needs a sentinel.
    ///
    /// <para>The leading space is what makes the sentinel safe: tag names are trimmed and required
    /// non-empty, so no team can ever define one that collides with this. Same trick as
    /// <c>VeInviteModel.UntaggedFilterValue</c>.</para>
    /// </summary>
    public const string GuestTagFilter = " guest";

    /// <summary>
    /// <b>One row per person</b>, with the teams they serve listed in it (changed 2026-08-07).
    ///
    /// <para>It was one row per person per team, mirroring the session-count report — but that report
    /// is a leaderboard where per-team numbers are the whole point, and this is a directory of people.
    /// Repeating a name once per team made a 176-VE roster read as if it held far more, and buried
    /// the fact that the duplicate rows were one person, which is the very thing the person model
    /// exists to express.</para>
    ///
    /// <para>Everything per-team collapses across the teams <i>in scope</i>: tags union, last-worked
    /// takes the most recent, and the row counts as active if any membership is. Filter to one team
    /// and each of those narrows to that team's answer, which is what makes the collapse safe rather
    /// than lossy — the per-team detail lives on the VE's own page.</para>
    /// </summary>
    /// <param name="teamIds">Null means every team (SystemAdmin, unfiltered) — the convention
    /// <c>SessionAccessScope.ResolveViewableTeamIds</c> already uses. An empty list means no team
    /// context and returns nothing.</param>
    /// <param name="includeInactive">Retired memberships are hidden by default: the roster answers
    /// "who can we call on", and a list that keeps everyone who ever served would stop being that.</param>
    /// <param name="filter">Everything the screen can narrow by. A record rather than a growing
    /// parameter list: this reached five filters, and positional booleans next to nullable strings
    /// are exactly where a call site silently passes the wrong one.</param>
    /// <param name="nowUtc">Taken once by the caller so every row's license status is derived against
    /// the same instant — a list evaluated across a day boundary could otherwise disagree with
    /// itself about who is expiring.</param>
    public async Task<IReadOnlyList<VeDirectoryRow>> GetDirectoryAsync(
        IReadOnlyList<int>? teamIds, VeDirectoryFilter filter, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var search = filter.Search;
        var tagName = filter.TagName;
        var includeInactive = filter.IncludeInactive;

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

        // The guest filter is deliberately NOT applied here. "Guest" is a property of the whole
        // row — no tags on ANY team in scope — and this query is still per-membership. Filtering
        // memberships would match the untagged half of someone who IS tagged on another team, and
        // that row then renders with tags and no Guest chip: a result that contradicts itself.
        // Applied after the grouping instead, where IsGuest actually exists.
        var guestsOnly = string.Equals(tagName, GuestTagFilter, StringComparison.Ordinal);

        if (!guestsOnly && !string.IsNullOrWhiteSpace(tagName))
        {
            // Lower-cased on both sides rather than StringComparison, which EF cannot translate, and
            // matching the OrdinalIgnoreCase grouping the rows use — SQLite's `=` on TEXT is
            // case-sensitive, so "Member" and "member" would otherwise be different filters.
            var tag = tagName.Trim().ToLower();
            query = query.Where(m => m.TagAssignments.Any(a => a.VeTag.Name.ToLower() == tag));
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

        // "Last worked" is a MAX over the session links, and "sessions worked" a COUNT over the
        // same ones — both from a single grouped query rather than per row, so adding the count costs
        // no extra round trip.
        //
        // Still computed PER TEAM even though the row is per person, because the row collapses over
        // the teams in scope: filtered to one team it must answer "when did they last work for you",
        // and only a per-team figure can narrow like that. A single global MAX would silently answer a
        // different question the moment someone filtered.
        //
        // Only sessions that actually happened count. Session.Status is not that signal — it only
        // ever means "not cancelled", so filtering on it would count a session scheduled for next
        // month as worked (the trap documented in CLAUDE.md, and found twice before).
        var veIds = memberships.Select(m => m.VolunteerExaminerId).Distinct().ToList();
        var lastWorked = await dbContext.SessionVolunteerExaminers
            .Where(sve => veIds.Contains(sve.VolunteerExaminerId)
                          && (sve.Session.TestingCompletedUtc != null || sve.Session.ExamToolsClosedUtc != null))
            .GroupBy(sve => new { sve.VolunteerExaminerId, sve.Session.TeamId })
            .Select(g => new
            {
                g.Key.VolunteerExaminerId,
                g.Key.TeamId,
                Last = g.Max(x => x.Session.ScheduledStartUtc),
                Count = g.Count()
            })
            .ToListAsync(cancellationToken);

        var lastByVeAndTeam = lastWorked.ToDictionary(x => (x.VolunteerExaminerId, x.TeamId), x => x.Last);
        var countByVeAndTeam = lastWorked.ToDictionary(x => (x.VolunteerExaminerId, x.TeamId), x => x.Count);

        // Possible duplicates from the phase 1 merge: rows sharing a call sign that were left
        // separate because their names disagreed. Surfaced rather than resolved automatically —
        // merging two people is not reversible, so a human decides.
        var sharedCallSigns = await dbContext.VolunteerExaminers
            .Where(v => v.CallSign != null)
            .GroupBy(v => v.CallSign!)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToListAsync(cancellationToken);

        // Placeholders excluded. ExamTools' "<UNKNOWN>" is shared by every VE it has no call sign
        // for, so those rows always collide — but they are *known* to be different people, not
        // suspected duplicates, and flagging them is noise that trains people to ignore the marker
        // on the rows where it means something. Seen immediately after the split repair, which
        // correctly produced two unidentified people who then both lit up as possible duplicates.
        var duplicateCallSigns = sharedCallSigns
            .Where(CallSign.IsUsable)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return [.. memberships
            .GroupBy(m => m.VolunteerExaminerId)
            .Select(group =>
            {
                var person = group.First().VolunteerExaminer;

                // Deduped by name, not by id: two teams can define their own "Team member" tag, and
                // showing it twice on one row would look like a rendering bug rather than a fact.
                var tags = group
                    .SelectMany(m => m.TagAssignments.Select(a => a.VeTag))
                    .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.OrderBy(t => t.SortOrder).First())
                    .OrderBy(t => t.SortOrder).ThenBy(t => t.Name)
                    .ToList();

                // Summed across the teams in scope, for the same reason LastWorkedUtc is a per-team
                // MAX: filtered to one team the row must answer "how many did they work FOR YOU",
                // and a global count would quietly answer something else. Someone who works for two
                // teams shows their combined total only when both are in view.
                var sessionsWorked = group
                    .Sum(m => countByVeAndTeam.TryGetValue((m.VolunteerExaminerId, m.TeamId), out var n) ? n : 0);

                var mostRecent = group
                    .Select(m => lastByVeAndTeam.TryGetValue((m.VolunteerExaminerId, m.TeamId), out var last) ? last : (DateTime?)null)
                    .Where(d => d is not null)
                    .DefaultIfEmpty(null)
                    .Max();

                return new VeDirectoryRow(
                    person,
                    [.. group.Select(m => new VeDirectoryTeam(m.TeamId, m.Team.Name, m.IsActive, m.Id)).OrderBy(t => t.Name)],
                    tags,
                    mostRecent,
                    sessionsWorked,
                    person.CallSign is { } call && duplicateCallSigns.Contains(call));
            })
            .OrderBy(r => r.VolunteerExaminer.Name)
            .Where(r => !guestsOnly || r.IsGuest)
            // License status and last-worked join guests on this side of the grouping, and for the
            // same reason: both are properties of the finished ROW. LastWorkedUtc is the max across
            // the teams in scope, and the license status is derived in C# from the cached snapshot
            // (DeriveSnapshotStatus is not translatable to SQL). Filtering memberships would answer
            // a different question than the column shows.
            .Where(r => filter.LicenseStatus is not { } status
                || r.VolunteerExaminer.DeriveSnapshotStatus(nowUtc) == status)
            // A row with no last-worked date satisfies NEITHER "worked since X" nor "not worked
            // since X": both are claims about a date that does not exist. A hand-added prospect is
            // therefore only ever in the unfiltered list, which is the honest answer.
            .Where(r => filter.WorkedFromUtc is not { } from || (r.LastWorkedUtc is { } d && d >= from))
            .Where(r => filter.WorkedToUtc is not { } to || (r.LastWorkedUtc is { } d && d <= to))];
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
public record VeDirectoryTeam(int TeamId, string Name, bool IsActive, int MembershipId);

public record VeDirectoryRow(
    VolunteerExaminer VolunteerExaminer,
    IReadOnlyList<VeDirectoryTeam> Teams,
    IReadOnlyList<VeTag> Tags,
    DateTime? LastWorkedUtc,
    /// <summary>
    /// Completed sessions this person has worked, across the teams currently in scope. Counts the
    /// same sessions LastWorkedUtc takes its maximum over — so a VE rostered onto a session next
    /// month is not counted, which is the trap CLAUDE.md records and that this very figure fell into
    /// once before (2026-08-06).
    /// </summary>
    int SessionsWorked,
    bool HasDuplicateCallSign)
{
    public bool IsGuest => Tags.Count == 0;

    /// <summary>Active somewhere in scope. Someone retired from one team but serving another is still a VE this team can call on.</summary>
    public bool IsActive => Teams.Any(t => t.IsActive);

    public string TeamSummary => string.Join(", ", Teams.Select(t => t.IsActive ? t.Name : $"{t.Name} (retired)"));
}

/// <summary>
/// What the VE Directory can narrow by. A record rather than a parameter list, because this reached
/// five filters and a row of positional nullable strings and bools is where a call site quietly
/// passes the wrong one.
/// </summary>
public record VeDirectoryFilter
{
    /// <summary>Call sign, name or email, case-insensitive.</summary>
    public string? Search { get; init; }

    /// <summary>A tag name, or <see cref="VolunteerExaminerDirectoryService.GuestTagFilter"/> for "no tags at all".</summary>
    public string? TagName { get; init; }

    /// <summary>Retired memberships are hidden by default: the directory answers "who can we call on".</summary>
    public bool IncludeInactive { get; init; }

    /// <summary>The derived FCC status a row must have — the same value its License chip shows.</summary>
    public WatchedLicenseStatus? LicenseStatus { get; init; }

    /// <summary>Last worked on or after this instant.</summary>
    public DateTime? WorkedFromUtc { get; init; }

    /// <summary>Last worked on or before this instant — how "hasn't worked in over a year" is expressed.</summary>
    public DateTime? WorkedToUtc { get; init; }
}
