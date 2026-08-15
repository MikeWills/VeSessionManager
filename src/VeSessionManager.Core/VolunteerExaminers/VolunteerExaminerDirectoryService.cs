using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Sessions;

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
        var ids = await OrderedPeopleQuery(teamIds, filter, nowUtc)
            .Select(v => v.Id)
            .ToListAsync(cancellationToken);

        return await BuildRowsAsync(ids, teamIds, filter, cancellationToken);
    }

    /// <summary>
    /// One page of the directory, plus how many people match in total (#298).
    ///
    /// <para><b>Every filter is applied in SQL before the page is taken</b>, which is the whole point
    /// — and is why the licence-status, guest and last-worked filters all had to become translatable
    /// first. Paging on some filters and applying the rest to the page afterwards would produce pages
    /// that render empty while the pager cheerfully claims "showing 1–25 of 176", which is worse than
    /// the unpaged list it replaced.</para>
    ///
    /// <para><paramref name="pageNumber"/> is 1-based and clamped: a page past the end returns the
    /// last one rather than nothing, so a stale link or a shrinking roster lands somewhere real.</para>
    /// </summary>
    public async Task<VeDirectoryPage> GetDirectoryPageAsync(
        IReadOnlyList<int>? teamIds, VeDirectoryFilter filter, DateTime nowUtc,
        int pageNumber, int pageSize, CancellationToken cancellationToken)
    {
        var query = OrderedPeopleQuery(teamIds, filter, nowUtc);

        var totalCount = await query.CountAsync(cancellationToken);
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        var page = Math.Clamp(pageNumber, 1, totalPages);

        var ids = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(v => v.Id)
            .ToListAsync(cancellationToken);

        var rows = await BuildRowsAsync(ids, teamIds, filter, cancellationToken);
        return new VeDirectoryPage(rows, totalCount, page, totalPages);
    }

    /// <summary>
    /// The people matching every filter, in display order.
    ///
    /// <para><b>Ordered by name then id.</b> The id is not decoration: two VEs can share a name, and
    /// paging over an order that does not fully determine position drops and repeats rows across page
    /// boundaries.</para>
    /// </summary>
    private IQueryable<VolunteerExaminer> OrderedPeopleQuery(
        IReadOnlyList<int>? teamIds, VeDirectoryFilter filter, DateTime nowUtc) =>
        BuildPeopleQuery(teamIds, filter, nowUtc).OrderBy(v => v.Name).ThenBy(v => v.Id);

    /// <summary>
    /// Every filter the directory offers, as one translatable query over <b>people</b> rather than
    /// memberships (#298).
    ///
    /// <para>This used to be a per-membership query that materialised the whole roster and then
    /// applied three of the filters in C# after grouping. Two of those genuinely are properties of the
    /// grouped row rather than of a membership — "guest" means no tag on <i>any</i> team in scope, and
    /// last-worked is a maximum across the teams in scope — which is why they were there. Both are
    /// expressible as subqueries against the person, which is what this is.</para>
    /// </summary>
    private IQueryable<VolunteerExaminer> BuildPeopleQuery(
        IReadOnlyList<int>? teamIds, VeDirectoryFilter filter, DateTime nowUtc)
    {
        var includeInactive = filter.IncludeInactive;
        var guestsOnly = string.Equals(filter.TagName, GuestTagFilter, StringComparison.Ordinal);

        var people = dbContext.VolunteerExaminers.AsQueryable();

        // The base scope, and the reason this is a query over people at all: someone with no
        // membership on a team in view is not in this directory.
        people = people.Where(v => dbContext.VeTeamMemberships.Any(m =>
            m.VolunteerExaminerId == v.Id
            && (teamIds == null || teamIds.Contains(m.TeamId))
            && (includeInactive || m.IsActive)));

        if (!guestsOnly && !string.IsNullOrWhiteSpace(filter.TagName))
        {
            // Lower-cased on both sides rather than StringComparison, which EF cannot translate, and
            // matching the OrdinalIgnoreCase grouping the rows use — SQLite's `=` on TEXT is
            // case-sensitive, so "Member" and "member" would otherwise be different filters.
            var tag = filter.TagName.Trim().ToLower();
            people = people.Where(v => dbContext.VeTeamMemberships.Any(m =>
                m.VolunteerExaminerId == v.Id
                && (teamIds == null || teamIds.Contains(m.TeamId))
                && (includeInactive || m.IsActive)
                && m.TagAssignments.Any(a => a.VeTag.Name.ToLower() == tag)));
        }

        if (guestsOnly)
        {
            // "Guest" is no tag on ANY team in scope, which is why it could never be a per-membership
            // filter: matching untagged memberships would also match the untagged half of someone who
            // IS tagged elsewhere, and that row then renders with tags and no Guest chip — a result
            // contradicting itself. As a NOT EXISTS over the person it says exactly what it means.
            people = people.Where(v => !dbContext.VeTeamMemberships.Any(m =>
                m.VolunteerExaminerId == v.Id
                && (teamIds == null || teamIds.Contains(m.TeamId))
                && (includeInactive || m.IsActive)
                && m.TagAssignments.Any()));
        }

        // Dates resolved in C#, classification in SQL — see VeLicenseStatusFilter.
        if (filter.LicenseStatus is { } licenseStatus)
        {
            people = people.Where(VeLicenseStatusFilter.For(licenseStatus, nowUtc));
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim().ToLower();
            people = people.Where(v =>
                v.Name.ToLower().Contains(term)
                || (v.CallSign ?? "").ToLower().Contains(term)
                || (v.Email ?? "").ToLower().Contains(term));
        }

        // "Worked since X" is MAX(start) >= X, which is the same question as "worked at least once at
        // or after X" — an EXISTS, no aggregate needed. "Not worked since X" is the mirror: they must
        // have worked at all, and never after X.
        //
        // A row with no last-worked date therefore satisfies NEITHER, unchanged from before: both are
        // claims about a date that does not exist, so a hand-added prospect appears only in the
        // unfiltered list, which is the honest answer.
        if (filter.WorkedFromUtc is { } from)
        {
            people = people.Where(v => dbContext.SessionVolunteerExaminers.Any(sve =>
                sve.VolunteerExaminerId == v.Id
                && (sve.Session.TestingCompletedUtc != null || sve.Session.ExamToolsClosedUtc != null)
                && (teamIds == null || teamIds.Contains(sve.Session.TeamId))
                && dbContext.VeTeamMemberships.Any(m =>
                    m.VolunteerExaminerId == v.Id
                    && m.TeamId == sve.Session.TeamId
                    && (includeInactive || m.IsActive))
                && sve.Session.ScheduledStartUtc >= from));
        }

        if (filter.WorkedToUtc is { } to)
        {
            people = people
                .Where(v => dbContext.SessionVolunteerExaminers.Any(sve =>
                    sve.VolunteerExaminerId == v.Id
                    && (sve.Session.TestingCompletedUtc != null || sve.Session.ExamToolsClosedUtc != null)
                    && (teamIds == null || teamIds.Contains(sve.Session.TeamId))
                    && dbContext.VeTeamMemberships.Any(m =>
                        m.VolunteerExaminerId == v.Id
                        && m.TeamId == sve.Session.TeamId
                        && (includeInactive || m.IsActive))))
                .Where(v => !dbContext.SessionVolunteerExaminers.Any(sve =>
                    sve.VolunteerExaminerId == v.Id
                    && (sve.Session.TestingCompletedUtc != null || sve.Session.ExamToolsClosedUtc != null)
                    && (teamIds == null || teamIds.Contains(sve.Session.TeamId))
                    && dbContext.VeTeamMemberships.Any(m =>
                        m.VolunteerExaminerId == v.Id
                        && m.TeamId == sve.Session.TeamId
                        && (includeInactive || m.IsActive))
                    && sve.Session.ScheduledStartUtc > to));
        }

        return people;
    }

    /// <summary>
    /// Turns a chosen set of people into finished rows: their in-scope memberships, tags, per-team
    /// session figures and duplicate-call-sign marker.
    ///
    /// <para>Takes ids rather than a query because the caller has already decided <i>which</i> people
    /// — either all of them or one page — and everything here is per-person detail for that set.</para>
    /// </summary>
    private async Task<IReadOnlyList<VeDirectoryRow>> BuildRowsAsync(
        IReadOnlyList<int> personIds, IReadOnlyList<int>? teamIds, VeDirectoryFilter filter, CancellationToken cancellationToken)
    {
        if (personIds.Count == 0)
        {
            return [];
        }

        var includeInactive = filter.IncludeInactive;

        var memberships = await dbContext.VeTeamMemberships
            .Include(m => m.VolunteerExaminer)
            .Include(m => m.Team)
            .Include(m => m.TagAssignments).ThenInclude(a => a.VeTag)
            .Where(m => personIds.Contains(m.VolunteerExaminerId)
                && (teamIds == null || teamIds.Contains(m.TeamId))
                && (includeInactive || m.IsActive))
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

        // "Last worked" is a MAX over the session links, and "sessions worked" a COUNT over the
        // same ones — both from a single grouped query rather than per row, so adding the count costs
        // no extra round trip.
        //
        // Still computed PER TEAM even though the row is per person, because the row collapses over
        // the teams in scope: filtered to one team it must answer "when did they last work for you",
        // and only a per-team figure can narrow like that. A single global MAX would silently answer a
        // different question the moment someone filtered.
        var lastWorked = await dbContext.SessionVolunteerExaminers
            .Where(SessionCompletion.RosterLinkIsCompleted)
            .Where(sve => personIds.Contains(sve.VolunteerExaminerId))
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
        //
        // Scoped to the call signs actually on this page, rather than every shared call sign on the
        // deployment: on a paged list the unscoped version was a whole-table group-by run to answer a
        // question about at most a screenful of people.
        var callSignsInPlay = memberships
            .Select(m => m.VolunteerExaminer.CallSign)
            .Where(c => c != null)
            .Distinct()
            .ToList();

        var sharedCallSigns = await dbContext.VolunteerExaminers
            .Where(v => v.CallSign != null && callSignsInPlay.Contains(v.CallSign))
            .GroupBy(v => v.CallSign!)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToListAsync(cancellationToken);

        // Placeholders excluded. ExamTools' "<UNKNOWN>" is shared by every VE it has no call sign
        // for, so those rows always collide — but they are *known* to be different people, not
        // suspected duplicates, and flagging them is noise that trains people to ignore the marker
        // on the rows where it means something.
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
            // Re-sorted here because the grouping above is over a set loaded by id, which carries no
            // order of its own. Same key as the query that chose the page, so a page's rows appear in
            // the position the pager promised.
            .OrderBy(r => r.VolunteerExaminer.Name)
            .ThenBy(r => r.VolunteerExaminer.Id)];
    }

    /// <summary>
    /// Everything one person's detail screen needs, or null when the id doesn't exist.
    ///
    /// <para><b>AsSplitQuery, because this chains three sibling collections</b> — TeamMemberships,
    /// VecAccreditations and CallSignHistory (#298). In one statement those multiply: a VE on 3 teams
    /// with 2 accreditations and 4 past call signs is 24 rows carrying every column of all four
    /// tables, to build 9 objects. Split, it is four small queries.
    ///
    /// <para>Safe here specifically because this loads <b>one</b> row by primary key. Split queries
    /// run in separate round trips and so can see different snapshots without a surrounding
    /// transaction; that matters for a paged list, and does not for a single person's detail page.</para>
    /// </summary>
    public Task<VolunteerExaminer?> GetPersonAsync(int volunteerExaminerId, CancellationToken cancellationToken) =>
        dbContext.VolunteerExaminers
            .Include(v => v.TeamMemberships).ThenInclude(m => m.Team)
            .Include(v => v.TeamMemberships).ThenInclude(m => m.TagAssignments).ThenInclude(a => a.VeTag)
            .Include(v => v.VecAccreditations).ThenInclude(a => a.Vec)
            .Include(v => v.CallSignHistory)
            .AsSplitQuery()
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

/// <summary>
/// One page of the VE directory (#298). <see cref="TotalCount"/> is how many people match the
/// filters, not how many are on this page — it is what the pager reports and what makes "showing
/// 1–25 of 176" true rather than decorative.
/// </summary>
/// <param name="PageNumber">1-based, and already clamped into range by the service.</param>
public record VeDirectoryPage(
    IReadOnlyList<VeDirectoryRow> Rows, int TotalCount, int PageNumber, int TotalPages);
