using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Authorization;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Sessions;
using VeSessionManager.Core.VecSubmissions;

namespace VeSessionManager.Web.Pages.SessionManager;

/// <summary>
/// Phase 9b's real session list, replacing the Phase 9a placeholder — recreated from
/// design_handoff_vesessionmanager_admin_ui/session-list.html. TeamAdmin is included alongside
/// SessionManager (not just SystemAdmin/SessionManager, the original 9a placeholder's attribute)
/// because SessionAccessScope already treats TeamAdmin as an equal-scope superset of SessionManager
/// for session visibility — see docs/admin-auth.md's role hierarchy. TeamLead was added in the
/// TeamLead-read-only-view fix (see docs/admin-auth.md) — SessionAccessScope.Scope already resolves a
/// TeamLead's effective teams the same way as everyone else, this page just needed the role added.
///
/// Multi-team (issue #19) + team filter/column (issue #17): a user belonging to more than one team
/// sees every team's sessions mixed together by default, with a Team column to tell them apart and
/// a team dropdown (TeamId/AvailableTeams) to narrow down to just one.
///
/// Dropdown filters + remembered selection (2026-07-28): the original filter-pill row only supported
/// one status at a time and reset to the "Upcoming" default on every bare navigation back to this
/// page (the "← Sessions" breadcrumb, the nav bar's own Sessions link, and RoleLandingPages'
/// post-login redirect are all plain links with no query string) — annoying when a Session Manager
/// picks "Past" or a specific team, clicks into a session, and comes back to find their filter gone.
/// Status is now a multi-select checkbox dropdown (Status/AvailableStatuses) instead of four mutually
/// exclusive pills, and the last-applied Status/TeamId/PageSize combination is remembered in a cookie
/// (FilterCookieName) and restored on any bare navigation. A submitted filter form always carries the
/// hidden "applied" field so OnGetAsync can tell "the user just changed filters" (even to an empty
/// status selection, meaning "show all") apart from "a bare link landed here with no query string at
/// all" — only the latter falls back to the cookie. Page (which page you're on) is deliberately NOT
/// remembered the same way — only PageSize is; changing a filter or coming back fresh always starts
/// back at page 1, since a remembered mid-list page number would routinely land on stale/empty
/// results once the underlying data changes.
///
/// Paging (2026-07-28): added once the list started getting long enough to matter. PageSize is one
/// of AllowedPageSizes (10/25/50/100, default 10) rather than a free-typed number, both so an
/// unvalidated huge page size can't be used to force one big unpaginated query and to keep the
/// dropdown's options fixed. Prev/Next links are built server-side (BuildPageUrl) rather than via
/// asp-route-* tag helpers because Status is a multi-value list — asp-route-* only supports one value
/// per key, so preserving multiple checked statuses across a page link needs manual query-string
/// construction.
///
/// Date-range filter (2026-07-28): added after a real test session was hard to find in the list
/// (39+ real HRCC sessions once ingestion resumed). DateRange is one of the relative presets in
/// DateRangePresetDays (Last7/Last14/... — relative to "now", so unlike an absolute custom range it
/// stays meaningful and is safe to remember in the cookie the same way Status/TeamId/PageSize are)
/// or "" for no date filter; DateFrom/DateTo are an explicit custom range (absolute dates, so
/// deliberately NOT remembered in the cookie — an old custom range would just be confusing to land
/// back on later) that override the preset when either is set. Whenever a date filter of either kind
/// is active, the sort flips to newest-first (most other views stay oldest/soonest-first) — the whole
/// point of this filter is finding a *recent* session fast, which an oldest-first sort would still
/// bury on a late page.
///
/// Session ID column (issue #35, 2026-07-29): originally showed ExamToolsSessionId (the raw Mongo
/// id), so it's usable to tell sessions apart at a glance and cross-reference against ExamTools'
/// own UI, per the issue's "know whose session is whose" ask. Swapped 2026-07-30 for Session.ExtId
/// (ExamTools' own short lead-VE-callsign code, e.g. "KM6Z - W5CBW") once it turned out the raw id
/// wasn't actually meaningful to a user for that purpose — ExtId is the same parenthetical text
/// ExamTools' own calendar UI shows next to the team name.
///
/// Filter-row realignment (reported 2026-07-29): the Status filter's old Upcoming/NeedsReview/Past
/// checkboxes didn't correspond to anything the Status column actually showed (Active/Reschedule
/// flagged/Completed/Cancelled) — Status is now that same four-value set, matching ToRow's
/// statusLabel priority exactly. "Upcoming" was a time-window concept, not a lifecycle status, so it
/// moved into the Date range dropdown as a distinct forward-looking preset (ScheduledStartUtc >= now,
/// unbounded) alongside the existing backward-looking Last7/Last14/... presets — it keeps the default
/// ascending sort rather than the look-back presets' newest-first flip, since its whole point is the
/// *soonest* session. The filter row is now Status, Date range, Team, in that order, with Team
/// converted from an auto-submitting bare &lt;select&gt; to the same dropdown-menu-plus-Apply pattern
/// as Status/Date range for consistency; Page size moved out of the filter row entirely to sit next to
/// the pagination controls below the table (via a `form` attribute referencing the filter form, since
/// it's no longer physically inside the &lt;form&gt; tag).
///
/// "Last 7 + Upcoming" preset + past-row shading (2026-07-30): a second forward-looking preset
/// alongside Upcoming, covering ScheduledStartUtc from 7 days ago through the unbounded future in
/// one filter — same ascending-sort treatment as Upcoming, just with the lower bound pushed back a
/// week. Replaced Upcoming as the fallback default for a fresh visit with no filter cookie yet (a
/// returning visitor's remembered cookie choice is untouched either way). Independent of any date
/// filter, every row now also gets a `row-past` CSS class once Session.HasEnded(now) — a light
/// background tint (see app.css) so a mixed list (this preset, or no date filter at all) makes it
/// obvious at a glance which sessions already happened without needing to read every date.
///
/// Column sorting (2026-07-31): every other table in the app sorts client-side in app.js, but this
/// list pages server-side — reordering only the ten rows currently on screen would look like a sort
/// of the whole result set and silently isn't one. So Sort/SortDirection are real query parameters
/// applied to the EF query before Skip/Take (see ApplySort), and the headers render as links whose
/// href is the next state in the click cycle (BuildSortUrl: ascending → descending → back to the
/// default ordering). Sorting resets to page 1, since the row that was on page 3 is somewhere else
/// entirely once the order changes. The choice rides along in the existing filter cookie, so it
/// survives a bare navigation back to this page exactly like Status/TeamId/PageSize already do.
/// </summary>
[Authorize(Roles = RoleGroups.AllRoles)]
public class IndexModel(
    AppDbContext dbContext,
    UserManager<User> userManager,
    SessionAccessScope accessScope,
    AdminAccessScope adminAccessScope,
    SessionActionService sessionActionService,
    VecSubmissionService vecSubmissionService,
    TimeProvider timeProvider) : PageModel
{
    private const string FilterCookieName = "vsm_session_filters";

    // Realigned 2026-07-29 (reported filter-row confusion) to match the labels ToRow's statusLabel
    // actually shows in the Status column (Active/Reschedule flagged/Completed/Cancelled) instead
    // of the old Upcoming/NeedsReview/Past set, which didn't correspond to anything in the table.
    // "Upcoming" was a time-window concept, not a lifecycle status, so it moved to the Date range
    // filter instead (DateRange == "Upcoming", handled alongside DateRangePresets below).
    // PendingVecSubmission is deliberately a different axis from the other four: those are mutually
    // exclusive lifecycle states mirroring the Status column, while this one cuts across them (a
    // Completed session may or may not still owe VEC paperwork — that's the separate VEC Submission
    // column). It lives in the same checkbox group because the group already ORs its members, so
    // ticking only this one yields exactly the "still owes paperwork" worklist. Added 2026-07-30 when
    // the standalone VEC Submission page was removed as redundant — see docs/vec-submission-tracker.md.
    private static readonly string[] KnownStatuses = ["Active", "RescheduleFlagged", "Completed", "Cancelled", "PendingVecSubmission"];

    /// <summary>Sortable columns, keyed by the value that travels in the query string. Anything not
    /// in here is ignored, so a hand-edited `sort=` can't reach an arbitrary expression.</summary>
    internal static readonly string[] SortableColumns = ["date", "extid", "team", "vec", "candidates", "status", "vecsubmission"];
    internal static readonly int[] AllowedPageSizes = [10, 25, 50, 100];
    private const int DefaultPageSize = 10;
    private const string UpcomingDateRangeKey = "Upcoming";

    /// <summary>Requested 2026-07-30: "Last 7 + Upcoming" — everything from 7 days ago through the
    /// unbounded future, so a Session Manager can spot a just-finished session alongside what's
    /// still coming up without the two separate filters. Same "unbounded forward, keep ascending
    /// sort" shape as UpcomingDateRangeKey, just with the lower bound pushed back a week instead of
    /// pinned to "now".</summary>
    private const string Last7PlusUpcomingDateRangeKey = "Last7PlusUpcoming";

    internal static readonly IReadOnlyDictionary<string, (int Days, string Label)> DateRangePresets = new Dictionary<string, (int, string)>
    {
        ["Last7"] = (7, "Last 7 days"),
        ["Last14"] = (14, "Last 14 days"),
        ["Last30"] = (30, "Last 30 days"),
        ["Last60"] = (60, "Last 60 days"),
        ["Last90"] = (90, "Last 90 days"),
        ["Last6Months"] = (182, "Last 6 months"),
        ["Last12Months"] = (365, "Last 12 months")
    };

    [BindProperty(SupportsGet = true, Name = "status")]
    public List<string> Status { get; set; } = [];

    [BindProperty(SupportsGet = true)]
    public int? TeamId { get; set; }

    [BindProperty(SupportsGet = true)]
    public int PageSize { get; set; } = DefaultPageSize;

    /// <summary>
    /// <b><c>pageNumber</c>, not <c>page</c>, and the difference is the whole of #368.</b> Razor
    /// Pages puts this page's own path into route values under the key <c>page</c>
    /// ("/SessionManager/Index"), and the route value provider runs <i>before</i> the query string
    /// provider — so <c>?page=2</c> never reached this property. Binding took the route value, failed
    /// to parse it as an int, and left the default. Every page rendered as page 1.
    ///
    /// <para>It failed in total silence: right page count, right "Showing X–Y of Z", pager links
    /// present and correct, and pressing Next did nothing. Shipped that way from 2026-07-28 until
    /// 2026-08-14, found only because the audit log grew a pager and hit the identical trap.</para>
    /// </summary>
    [BindProperty(SupportsGet = true, Name = "pageNumber")]
    public int PageNumber { get; set; } = 1;

    /// <summary>One of DateRangePresets' keys, or "" for no date filter.</summary>
    [BindProperty(SupportsGet = true)]
    public string DateRange { get; set; } = "";

    /// <summary>Explicit custom range — set (either or both) to override DateRange.</summary>
    [BindProperty(SupportsGet = true)]
    public DateOnly? DateFrom { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateOnly? DateTo { get; set; }

    /// <summary>One of SortableColumns, or "" for the default date ordering.</summary>
    [BindProperty(SupportsGet = true, Name = "sort")]
    public string Sort { get; set; } = "";

    /// <summary>"asc" or "desc" — ignored unless Sort names a column.</summary>
    [BindProperty(SupportsGet = true, Name = "dir")]
    public string SortDirection { get; set; } = "asc";

    /// <summary>Hidden marker on the filter form — present only when the form was actually submitted, so an empty Status list can be told apart from "no query string was sent at all."</summary>
    [BindProperty(SupportsGet = true)]
    public bool Applied { get; set; }

    public IReadOnlyList<(int Id, string Name)> AvailableTeams { get; private set; } = [];
    public IReadOnlyList<SessionRow> Sessions { get; private set; } = [];
    public string StatusSummaryLabel { get; private set; } = "";
    public string DateRangeSummaryLabel { get; private set; } = "Any time";
    public string TeamSummaryLabel { get; private set; } = "All teams";
    public int TotalCount { get; private set; }
    public int TotalPages { get; private set; }

    public async Task OnGetAsync()
    {
        ResolveFilterState();
        BuildSummaryLabels();

        var user = await userManager.GetRequiredUserAsync(dbContext, User);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        AvailableTeams = await accessScope.GetAvailableTeamsAsync(dbContext, user);
        TeamSummaryLabel = TeamId is not null
            ? AvailableTeams.FirstOrDefault(t => t.Id == TeamId).Name ?? "All teams"
            : "All teams";

        var query = ApplyFilters(accessScope.Scope(dbContext.Sessions, user, TeamId), now, out var defaultsToNewestFirst);
        query = ApplySort(query, defaultsToNewestFirst, now);

        TotalCount = await query.CountAsync();
        TotalPages = Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
        PageNumber = Math.Min(PageNumber, TotalPages);

        // Projected, not materialized: this used to Include(s => s.Candidates) and pull every
        // candidate row of up to 100 sessions purely to render a count. The projection also removes
        // the Vec/Team includes, since only their names are shown.
        var rows = await query
            .Skip((PageNumber - 1) * PageSize)
            .Take(PageSize)
            .Select(SessionListRow.Projection)
            .ToListAsync(HttpContext.RequestAborted);

        Sessions = rows.Select(r => ToRow(r, now, user)).ToList();
    }

    /// <summary>
    /// Reconciles the three sources of filter state — the submitted form, the remembered cookie, and
    /// this page's defaults — and clamps every value to a known-good one before it reaches a query
    /// or a cookie.
    /// </summary>
    private void ResolveFilterState()
    {
        // [BindProperty(SupportsGet = true)] can leave a string property null rather than its C#
        // default when the request's query string omits the key entirely (confirmed live 2026-07-29,
        // e.g. unchecking every Status checkbox and submitting) — unlike Status (List<string>) and
        // PageSize (int) on this same page, which correctly keep their defaults. Normalize before any
        // DateRangePresets lookup, since Dictionary.ContainsKey/TryGetValue throw on a null key.
        DateRange ??= "";
        Sort ??= "";
        SortDirection ??= "asc";

        if (Applied)
        {
            PageSize = AllowedPageSizes.Contains(PageSize) ? PageSize : DefaultPageSize;
            DateRange = IsKnownDateRange(DateRange) ? DateRange : "";
            Sort = SortableColumns.Contains(Sort) ? Sort : "";
            SortDirection = SortDirection == "desc" ? "desc" : "asc";
            SaveFilterCookie(Status, TeamId, PageSize, DateRange, Sort, SortDirection);
        }
        else
        {
            var cookie = ReadFilterCookie();
            Status = cookie.Status ?? [];
            TeamId = cookie.TeamId;
            PageSize = cookie.PageSize ?? DefaultPageSize;
            DateRange = cookie.DateRange ?? Last7PlusUpcomingDateRangeKey;
            Sort = cookie.Sort ?? "";
            SortDirection = cookie.SortDirection ?? "asc";
            PageNumber = 1;
        }

        Status = Status.Where(s => KnownStatuses.Contains(s)).Distinct().ToList();
        PageNumber = Math.Max(1, PageNumber);
    }

    /// <summary>The "what am I filtered to" text on each dropdown button.</summary>
    private void BuildSummaryLabels()
    {
        StatusSummaryLabel = Status.Count switch
        {
            0 => "All",
            1 => StatusLabel(Status[0]),
            _ => $"{Status.Count} selected"
        };

        DateRangeSummaryLabel = (DateFrom, DateTo) switch
        {
            (not null, not null) => $"{DateFrom:MMM d} – {DateTo:MMM d}",
            (not null, null) => $"From {DateFrom:MMM d}",
            (null, not null) => $"Through {DateTo:MMM d}",
            _ when DateRange == UpcomingDateRangeKey => "Upcoming",
            _ when DateRange == Last7PlusUpcomingDateRangeKey => "Last 7 + Upcoming",
            _ => DateRangePresets.TryGetValue(DateRange, out var preset) ? preset.Label : "Any time"
        };
    }

    /// <summary>
    /// Status and date-range filtering. <paramref name="defaultsToNewestFirst"/> is returned rather
    /// than recomputed by the caller because it depends on the resolved date range, which only this
    /// method works out.
    /// </summary>
    private IQueryable<Session> ApplyFilters(IQueryable<Session> query, DateTime now, out bool defaultsToNewestFirst)
    {
        // These mirror ToRow's statusLabel priority exactly (Cancelled > Reschedule flagged >
        // Completed > Active) so a checked box always matches what the Status column shows.
        var wantActive = Status.Contains("Active");
        var wantRescheduleFlagged = Status.Contains("RescheduleFlagged");
        var wantCompleted = Status.Contains("Completed");
        var wantCancelled = Status.Contains("Cancelled");
        var wantPendingVecSubmission = Status.Contains("PendingVecSubmission");
        if (wantActive || wantRescheduleFlagged || wantCompleted || wantCancelled || wantPendingVecSubmission)
        {
            query = query.Where(s =>
                // "Completed" means finished by either route — a Session Manager marking it, or
                // ExamTools closing it (2026-07-31). Session.IsCompleted is this same rule for an
                // already-materialized session; EF cannot translate that property, so the rule is
                // spelled out here and SessionCompletionRuleTests pins the two spellings together.
                (wantActive && s.Status == SessionStatus.Active && !s.RescheduleFlaggedForReview && s.TestingCompletedUtc == null && s.ExamToolsClosedUtc == null)
                || (wantRescheduleFlagged && s.Status == SessionStatus.Active && s.RescheduleFlaggedForReview)
                || (wantCompleted && s.Status == SessionStatus.Active && !s.RescheduleFlaggedForReview && (s.TestingCompletedUtc != null || s.ExamToolsClosedUtc != null))
                || (wantCancelled && s.Status == SessionStatus.Cancelled)
                // Must stay identical to NavBadgeCountService.CountSessionsPendingVecSubmissionAsync,
                // which backs the nav badge — the filtered list and the badge count are the same thing.
                || (wantPendingVecSubmission
                    && s.Status == SessionStatus.Active
                    && s.VecSubmissionStatus == VecSubmissionStatus.NotSubmitted
                    && s.Candidates.Any(c => CandidateApplicationStatusExtensions.SubmittableStatuses.Contains(c.ApplicationStatus))));
        }

        var (dateFromUtc, dateToUtc) = ResolveDateRange(now);
        if (dateFromUtc is not null)
        {
            query = query.Where(s => s.ScheduledStartUtc >= dateFromUtc.Value);
        }
        if (dateToUtc is not null)
        {
            query = query.Where(s => s.ScheduledStartUtc <= dateToUtc.Value);
        }

        // A look-back date filter's whole point is finding a *recent* session fast — oldest-first
        // would still bury it on a late page, so flip to newest-first whenever one is active. The
        // "Upcoming" and "Last 7 + Upcoming" presets are the opposite: their whole point is the
        // *soonest* session, so they keep the default ascending sort instead of flipping.
        var isForwardLookingPreset = (DateRange == UpcomingDateRangeKey || DateRange == Last7PlusUpcomingDateRangeKey)
            && DateFrom is null && DateTo is null;
        defaultsToNewestFirst = (dateFromUtc is not null || dateToUtc is not null) && !isForwardLookingPreset;

        return query;
    }

    /// <summary>
    /// Applies the user's chosen column sort, falling back to the date ordering the list has always
    /// used when no column is chosen. Applied to the IQueryable before Skip/Take, so it orders the
    /// whole filtered result set rather than just the page on screen.
    ///
    /// Status/VEC submission sort on a CASE that reproduces the chip label ToRow renders, not on the
    /// underlying columns — those two cells each collapse several fields (Status, the reschedule
    /// flag, TestingCompletedUtc) into one label, and sorting by anything other than what the user
    /// can read in the cell would look broken.
    /// </summary>
    private IQueryable<Session> ApplySort(IQueryable<Session> query, bool defaultsToNewestFirst, DateTime now)
    {
        if (!SortableColumns.Contains(Sort))
        {
            // A look-back date filter's whole point is finding a *recent* session fast — oldest-first
            // would still bury it on a late page, so flip to newest-first whenever one is active. The
            // "Upcoming" and "Last 7 + Upcoming" presets are the opposite: their whole point is the
            // *soonest* session, so they keep the default ascending sort instead of flipping.
            return defaultsToNewestFirst
                ? query.OrderByDescending(s => s.ScheduledStartUtc)
                : query.OrderBy(s => s.ScheduledStartUtc);
        }

        var descending = SortDirection == "desc";

        IOrderedQueryable<Session> Order<TKey>(System.Linq.Expressions.Expression<Func<Session, TKey>> key) =>
            descending ? query.OrderByDescending(key) : query.OrderBy(key);

        var ordered = Sort switch
        {
            "extid" => Order(s => s.ExtId),
            "team" => Order(s => s.Team.Name),
            "vec" => Order(s => s.Vec.Name),
            // Withdrawn candidates are excluded here and in the rendered count below, so this column
            // agrees with the roster on Session Detail. A NotTested row is someone who left the
            // session; counting them made a session look fuller than it is.
            "candidates" => Order(s => s.Candidates.Count(c => c.ApplicationStatus != CandidateApplicationStatus.NotTested)),
            // Both of these used to be written out here, because a sort key runs inside an EF
            // expression tree and cannot call SessionChips. Two copies of one rule, and the guard
            // against them drifting only worked in one direction — see SessionChips' own remarks.
            // They are expressions now, so there is one definition and EF still translates it.
            "status" => Order(SessionChips.StatusSortKey(now)),
            "vecsubmission" => Order(SessionChips.VecSubmissionSortKey(now)),
            _ => Order(s => s.ScheduledStartUtc)
        };

        // Deterministic tiebreak — without one, equal keys (every session sharing a team, say) can
        // come back in a different order on each request, so paging through them silently repeats
        // and skips rows.
        return ordered.ThenBy(s => s.Id);
    }

    /// <summary>
    /// Href for a sortable column header: the next state in the ascending → descending → unsorted
    /// cycle. Always returns to page 1 — the row that was on page 3 is somewhere else entirely once
    /// the ordering changes.
    /// </summary>
    public string BuildSortUrl(string column)
    {
        var (nextSort, nextDirection) = Sort != column ? (column, "asc")
            : SortDirection == "asc" ? (column, "desc")
            : ("", "asc");
        return BuildPageUrl(1, sortOverride: nextSort, sortDirectionOverride: nextDirection);
    }

    /// <summary>The `aria-sort` value for a column header — also what app.css keys the ▲/▼ arrow off, so the server-sorted list looks identical to the client-sorted tables elsewhere.</summary>
    public string SortAria(string column) =>
        Sort != column ? "none" : SortDirection == "desc" ? "descending" : "ascending";

    // ---- Row actions (requested 2026-07-30: bring the session-level actions to the list so routine
    // work doesn't require clicking into each session). Each is a thin wrapper over the same Core
    // service the Detail page's equivalent button calls — no business logic lives here — and each
    // re-resolves the session and re-checks authorization server-side rather than trusting the
    // posted id or the fact that the UI rendered the control.

    public async Task<IActionResult> OnPostMarkSubmittedAsync(int sessionId)
    {
        var auth = await AuthorizeSessionAsync(sessionId);
        if (auth is null) return Forbid();

        Apply(ActionOutcomes.MarkSubmittedToVec(
            await vecSubmissionService.MarkSubmittedAsync(sessionId, auth.Value.User.Id, CancellationToken.None)));
        return RedirectToCurrentView();
    }

    public async Task<IActionResult> OnPostMarkCompletedAsync(int sessionId)
    {
        var auth = await AuthorizeSessionAsync(sessionId);
        if (auth is null) return Forbid();

        Apply(ActionOutcomes.MarkCompleted(
            await sessionActionService.MarkCompletedAsync(sessionId, auth.Value.User.Id, CancellationToken.None)));
        return RedirectToCurrentView();
    }

    public async Task<IActionResult> OnPostClearFlagAsync(int sessionId)
    {
        var auth = await AuthorizeSessionAsync(sessionId);
        if (auth is null) return Forbid();

        Apply(ActionOutcomes.ClearRescheduleFlag(
            await sessionActionService.ClearRescheduleFlagAsync(sessionId, auth.Value.User.Id, CancellationToken.None)));
        return RedirectToCurrentView();
    }

    /// <summary>TeamAdmin/SystemAdmin only — gated on AdminAccessScope.CanManageTeam, deliberately not SessionAccessScope.CanEdit, matching Detail.cshtml.cs's own delete handler.</summary>
    public async Task<IActionResult> OnPostDeleteSessionAsync(int sessionId)
    {
        var user = await userManager.GetUserWithManagerAsync(dbContext, User);
        if (user is null) return Forbid();

        var session = await dbContext.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId);
        if (session is null) return NotFound();
        if (!adminAccessScope.CanManageTeam(user, session.TeamId)) return Forbid();

        Apply(ActionOutcomes.DeleteSession(
            await sessionActionService.DeleteAsync(sessionId, user.Id, CancellationToken.None)));
        return RedirectToCurrentView();
    }

    /// <summary>Re-resolves the session from its posted id and confirms the acting user may edit *that* session — the list spans teams, so a rendered control is never sufficient proof of rights.</summary>
    private async Task<(User User, Session Session)?> AuthorizeSessionAsync(int sessionId)
    {
        var user = await userManager.GetUserWithManagerAsync(dbContext, User);
        if (user is null)
        {
            return null;
        }

        var session = await dbContext.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId);
        return session is null || !accessScope.CanEdit(user, session) ? null : (user, session);
    }

    /// <summary>See Detail.Apply — the wording comes from <see cref="ActionOutcomes"/>, never from here.</summary>
    private void Apply(ActionOutcome outcome) =>
        TempData[outcome.Success ? "StatusMessage" : "ErrorMessage"] = outcome.Message;

    /// <summary>Returns to the exact filtered/paged view the action was launched from — BuildPageUrl already encodes every filter, including Status's multiple values.</summary>
    private IActionResult RedirectToCurrentView() => Redirect(BuildPageUrl(PageNumber));

    /// <summary>
    /// Form action for a row action, carrying the current filter/page state in the query string.
    /// Necessary because `asp-page-handler` builds its action from the route alone and silently drops
    /// the query string — without this the filter properties bind empty on POST and the redirect
    /// afterwards lands on an unfiltered list, losing whatever view the action was launched from.
    /// </summary>
    public string BuildActionUrl(string handler) => $"{BuildPageUrl(PageNumber)}&handler={Uri.EscapeDataString(handler)}";

    /// <summary>Builds a Prev/Next/page-size link preserving every current filter — asp-route-* tag helpers can't represent Status's multiple values, so this constructs the query string directly.</summary>
    public string BuildPageUrl(int page, int? pageSizeOverride = null, string? sortOverride = null, string? sortDirectionOverride = null)
    {
        var qs = new List<string> { "applied=true" };
        qs.AddRange(Status.Select(s => $"status={Uri.EscapeDataString(s)}"));
        if (TeamId is not null)
        {
            qs.Add($"teamId={TeamId}");
        }
        qs.Add($"pageSize={pageSizeOverride ?? PageSize}");
        qs.Add($"pageNumber={page}");
        if (!string.IsNullOrEmpty(DateRange))
        {
            qs.Add($"dateRange={Uri.EscapeDataString(DateRange)}");
        }
        if (DateFrom is not null)
        {
            qs.Add($"dateFrom={DateFrom:yyyy-MM-dd}");
        }
        if (DateTo is not null)
        {
            qs.Add($"dateTo={DateTo:yyyy-MM-dd}");
        }

        var sort = sortOverride ?? Sort;
        if (!string.IsNullOrEmpty(sort))
        {
            qs.Add($"sort={Uri.EscapeDataString(sort)}");
            qs.Add($"dir={Uri.EscapeDataString(sortDirectionOverride ?? SortDirection)}");
        }
        return "/SessionManager/Index?" + string.Join("&", qs);
    }

    /// <summary>Custom DateFrom/DateTo (if either is set) override the DateRange preset entirely — an explicit range is a stronger signal than a leftover preset selection.</summary>
    private (DateTime? From, DateTime? To) ResolveDateRange(DateTime now)
    {
        if (DateFrom is not null || DateTo is not null)
        {
            return (
                DateFrom?.ToDateTime(TimeOnly.MinValue),
                DateTo?.ToDateTime(TimeOnly.MaxValue));
        }

        if (DateRange == UpcomingDateRangeKey)
        {
            return (now, null);
        }

        if (DateRange == Last7PlusUpcomingDateRangeKey)
        {
            return (now.AddDays(-7), null);
        }

        return DateRangePresets.TryGetValue(DateRange, out var preset)
            ? (now.AddDays(-preset.Days), now)
            : (null, null);
    }

    private static bool IsKnownDateRange(string dateRange) =>
        dateRange == UpcomingDateRangeKey || dateRange == Last7PlusUpcomingDateRangeKey || DateRangePresets.ContainsKey(dateRange);

    private static string StatusLabel(string status) => status switch
    {
        // The stored filter value stays "Active" — it is the SQL rule "not cancelled and not
        // completed", which now renders as two different chips depending on whether the session has
        // started. Only the label widens, so a user ticking it gets what the words promise. Adding a
        // separate "Upcoming" status filter was considered and rejected: the Date range dropdown
        // already has an Upcoming preset, and two controls spelled the same would be worse than one
        // honest label.
        "Active" => "Active or upcoming",
        "RescheduleFlagged" => "Reschedule flagged",
        "Completed" => "Completed",
        "Cancelled" => "Cancelled",
        "PendingVecSubmission" => "Pending VEC submission",
        _ => status
    };

    /// <summary>Every field is validated on the way back out, and a cookie written before the sort
    /// fields existed simply has fewer parts — so an older (or hand-edited) cookie degrades to the
    /// defaults rather than being trusted.</summary>
    private (List<string>? Status, int? TeamId, int? PageSize, string? DateRange, string? Sort, string? SortDirection) ReadFilterCookie()
    {
        if (!Request.Cookies.TryGetValue(FilterCookieName, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return (null, null, null, null, null, null);
        }

        var parts = raw.Split('|', 6);
        var status = parts[0].Length == 0 ? [] : parts[0].Split(',').Where(s => KnownStatuses.Contains(s)).ToList();
        int? teamId = parts.Length > 1 && int.TryParse(parts[1], out var id) ? id : null;
        int? pageSize = parts.Length > 2 && int.TryParse(parts[2], out var size) && AllowedPageSizes.Contains(size) ? size : null;
        string? dateRange = parts.Length > 3 && IsKnownDateRange(parts[3]) ? parts[3] : null;
        string? sort = parts.Length > 4 && SortableColumns.Contains(parts[4]) ? parts[4] : null;
        string? sortDirection = parts.Length > 5 && parts[5] == "desc" ? "desc" : null;
        return (status, teamId, pageSize, dateRange, sort, sortDirection);
    }

    private void SaveFilterCookie(List<string> status, int? teamId, int pageSize, string dateRange, string sort, string sortDirection)
    {
        var value = $"{string.Join(",", status)}|{teamId}|{pageSize}|{dateRange}|{sort}|{sortDirection}";
        Response.Cookies.Append(FilterCookieName, value, new CookieOptions
        {
            Expires = DateTimeOffset.UtcNow.AddYears(1),
            // Razor Pages' Index-page convention trims "Index" from the actual route — asp-page="/SessionManager/Index"
            // (used by the nav bar and "← Sessions" breadcrumbs) renders href="/SessionManager", not "/SessionManager/Index".
            // A cookie Path of "/SessionManager/Index" only matches that literal prefix, so the browser never sent it back
            // on those links — the cookie was written but effectively never read. Confirmed live 2026-07-29: both
            // "/SessionManager" and "/SessionManager/Index" route successfully server-side (this page answers either),
            // but the cookie's Path attribute cares about the request path, not which route matched it.
            Path = "/SessionManager",
            SameSite = SameSiteMode.Lax,
            IsEssential = true,
            // Both default to false, and neither was set (L-01). Nothing reads this from JavaScript,
            // so HttpOnly costs nothing and removes it from the reach of any future XSS.
            HttpOnly = true,
            // Secure is conditional, not unconditional: hardcoding true would silently break local
            // development over http://localhost, where the filter row would appear to forget itself
            // on every navigation with nothing to explain why. Request.IsHttps is correct behind the
            // reverse proxy because of UseForwardedHeaders (Program.cs) — the same dependency the
            // rate limiter and the audit log's source address have.
            //
            // The contents are low-value (a status list, a team id, a page size, a sort key) and
            // every one is re-validated against an allowlist on read, so a tampered cookie cannot
            // reach an arbitrary sort expression. This is defence in depth, not a fix for a live
            // path.
            Secure = Request.IsHttps
        });
    }

    /// <summary>
    /// The columns the session list actually renders, and nothing else. Exists so the query can
    /// project instead of materializing entities: the list previously did
    /// <c>Include(s =&gt; s.Candidates)</c> and loaded every candidate row of up to 100 sessions to
    /// display one number per row.
    ///
    /// <para><see cref="Projection"/> is a static expression rather than an inline
    /// <c>Select(...)</c> so it is impossible to have two subtly different versions of it, and so
    /// the candidate-count rule below sits next to the sort key that has to agree with it.</para>
    /// </summary>
    public record SessionListRow(
        int Id,
        int TeamId,
        string? ExtId,
        DateTime ScheduledStartUtc,
        int DurationMinutes,
        bool HasZoom,
        SessionStatus Status,
        bool RescheduleFlaggedForReview,
        DateTime? TestingCompletedUtc,
        DateTime? ExamToolsClosedUtc,
        VecSubmissionStatus VecSubmissionStatus,
        string VecName,
        string TeamName,
        int CandidateCount)
    {
        public static readonly System.Linq.Expressions.Expression<Func<Session, SessionListRow>> Projection =
            s => new SessionListRow(
                s.Id,
                s.TeamId,
                s.ExtId,
                s.ScheduledStartUtc,
                s.DurationMinutes,
                s.ZoomMeetingId != null,
                s.Status,
                s.RescheduleFlaggedForReview,
                s.TestingCompletedUtc,
                s.ExamToolsClosedUtc,
                s.VecSubmissionStatus,
                s.Vec.Name,
                s.Team.Name,
                // Withdrawn candidates are excluded here, in the "candidates" sort key, and on
                // Session Detail's roster — all three must agree. A NotTested row is someone who
                // left the session; counting them made a session look fuller than it is.
                s.Candidates.Count(c => c.ApplicationStatus != CandidateApplicationStatus.NotTested));

        /// <summary>
        /// Mirrors <see cref="Session.CompletedUtc"/> for a projected row. Same rule, and the same
        /// reason it is not <c>Status</c>: Status only ever leaves Active on cancellation.
        /// SessionCompletionRuleTests pins this against the entity and against the query filter.
        /// </summary>
        public bool IsCompleted => SessionCompletion.IsCompleted(TestingCompletedUtc, ExamToolsClosedUtc);

        /// <summary>Mirrors <see cref="Session.HasEnded"/> — see that member for why `now` is passed in.</summary>
        public bool HasEnded(DateTime now) => ScheduledStartUtc.AddMinutes(DurationMinutes) <= now;
    }

    private SessionRow ToRow(SessionListRow s, DateTime now, User user)
    {
        // ExtId gets its own column (issue #35 — "know whose session is whose"), not repeated in
        // the sub-line the way it used to be.
        var subParts = new List<string>();
        if (s.HasZoom)
        {
            subParts.Add("Zoom");
        }
        if (s.Status == SessionStatus.Cancelled)
        {
            subParts.Add("Cancelled");
        }

        // Both chips come from SessionChips so the list, session detail and the sort key below cannot
        // drift apart. The Status *filter* still spells its rule out separately — it has to translate
        // to SQL, which a C# switch cannot.
        var (statusClass, statusLabel) = SessionChips.Status(
            s.Status, s.RescheduleFlaggedForReview, s.IsCompleted, hasStarted: s.ScheduledStartUtc <= now);
        var (vecClass, vecLabel) = SessionChips.VecSubmission(
            s.Status, s.VecSubmissionStatus, hasStarted: s.ScheduledStartUtc <= now);

        // Same availability rules the session Detail page applies to the same actions, so a control
        // never appears here that would be absent (or 403) there. Cancelled sessions expose nothing
        // but Delete — there's nothing left to complete or submit for a session that never ran.
        var canEdit = accessScope.CanEdit(user, s.TeamId);
        var notCancelled = s.Status != SessionStatus.Cancelled;
        var canMarkSubmitted = canEdit && notCancelled && s.VecSubmissionStatus == VecSubmissionStatus.NotSubmitted;
        var canMarkCompleted = canEdit && notCancelled && s.TestingCompletedUtc is null;
        var canClearFlag = canEdit && s.RescheduleFlaggedForReview;
        var canDelete = adminAccessScope.CanManageTeam(user, s.TeamId);

        return new SessionRow(
            s.Id,
            s.ExtId ?? "—",
            EasternTimeFormatter.Format(s.ScheduledStartUtc, "ddd, MMM d"),
            string.Join(" · ", subParts),
            s.VecName,
            s.TeamName,
            s.CandidateCount,
            s.RescheduleFlaggedForReview,
            statusClass, statusLabel,
            vecClass, vecLabel,
            s.HasEnded(now),
            canMarkSubmitted,
            canMarkCompleted,
            canClearFlag,
            canDelete,
            canMarkSubmitted || canMarkCompleted || canClearFlag || canDelete);
    }

    /// <summary>
    /// Can* flags are resolved per row, not per page — the list can span teams, and a user's rights
    /// differ by team (SessionAccessScope.CanEdit for the routine actions, AdminAccessScope.CanManageTeam
    /// for Delete). They only hide controls; every POST handler re-checks the same rules server-side.
    /// </summary>
    public record SessionRow(
        int Id,
        string ExtId,
        string TitleLine,
        string SubLine,
        string VecName,
        string TeamName,
        int CandidateCount,
        bool RescheduleFlagged,
        string StatusChipClass,
        string StatusChipLabel,
        string VecSubmissionChipClass,
        string VecSubmissionChipLabel,
        bool IsPast,
        bool CanMarkSubmitted,
        bool CanMarkCompleted,
        bool CanClearFlag,
        bool CanDelete,
        bool HasAnyAction);
}
