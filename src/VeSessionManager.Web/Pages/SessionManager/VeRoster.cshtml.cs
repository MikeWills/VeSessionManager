using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Authorization;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.VolunteerExaminers;

namespace VeSessionManager.Web.Pages.SessionManager;

/// <summary>
/// **Renamed to "VE Session Counts" in the UI (2026-08-07)** — it was never a roster, and once the
/// VE Directory arrived (issue #142) the old name actively pointed at the wrong page: one is who the
/// team's VEs are, this one is how many sessions each has worked. The route, file name and ActiveNav
/// key deliberately still say VeRoster; this page is expected to move into or merge with a stats
/// screen, so churning URLs for an interim name buys nothing.
///
/// Phase 9b's nav destination — Phase 7's "simple report: session count per VE,
/// filterable by date range" (VolunteerExaminerReportService.GetSessionCountsAsync), finally given
/// a UI. Not part of the design handoff's four mocked screens (only Sessions/session-detail were
/// mocked) — styled with the same design-system components (table/chip/eyebrow) since it's a plain
/// report, not something that needed its own visual design pass. TeamLead was added in the
/// TeamLead-read-only-view fix (see docs/admin-auth.md) — this page is purely a read-only report already, so
/// no write-gating was needed, just the role.
///
/// Multi-team (issue #19): the report is per-team, so a user belonging to more than one team picks
/// which one via TeamId/AvailableTeams — same filter-pill convention as the session list.
///
/// Dropdown filters (issue #38, 2026-07-29): team pills and plain From/To date inputs replaced with
/// the same dropdown pattern as the session list — team is now a &lt;select&gt; and the date range
/// reuses IndexModel's DateRangePresets dictionary directly (same assembly, no need to duplicate it)
/// so both pages' "Last N days" options stay in sync automatically. No cookie persistence here
/// (unlike the session list) — not asked for, and this report is looked at far less often.
///
/// Same issue also reported "VEs aren't showing up" — root cause was in ExamToolsClient, not this
/// page: export/full.json's VE list is wrapped under a "devdoc" key on examtools.dev but NOT wrapped
/// at all on the real prod host (alpha.exam.tools), confirmed live 2026-07-29 against real HRCC
/// data. See ExamToolsFullExport.ResolveVes() for the fix — every real HRCC session had zero VEs
/// synced until that shipped, nothing wrong with this page's query logic.
///
/// **Restricted to admin roles (2026-08-01).** SessionManager and TeamLead were dropped: this page
/// is both a full contact roster for a team's VEs and a per-VE session-count leaderboard, and a
/// visible count-per-person invites comparison between volunteers that no one asked for. The
/// per-session VE chips on the session Detail page are deliberately NOT covered by this — those are
/// the VEs actually serving that one session, which a Session Manager running it needs to see.
/// Keep the nav gate in _AppLayout.cshtml in step with this attribute; a role that can't load the
/// page must not be shown a link that 403s.
/// </summary>
[Authorize(Roles = RoleGroups.Admins)]
[RemembersFilters]
public class VeRosterModel(AppDbContext dbContext, UserManager<User> userManager, SessionAccessScope accessScope, VolunteerExaminerReportService reportService, TimeProvider timeProvider) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public int? TeamId { get; set; }

    /// <summary>One of IndexModel.DateRangePresets' keys, or "" for no date filter (the default — "make the date optional" per issue #38).</summary>
    [BindProperty(SupportsGet = true)]
    public string DateRange { get; set; } = "";

    /// <summary>Explicit custom range — set (either or both) to override DateRange.</summary>
    [BindProperty(SupportsGet = true)]
    public DateOnly? DateFrom { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateOnly? DateTo { get; set; }

    /// <summary>Call sign or name, partial and case-insensitive (issue #135).</summary>
    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    /// <summary>False only when the account belongs to no team at all — a null TeamId now means "all teams merged", not "no context" (2026-07-30, matching the session list).</summary>
    public bool HasTeamContext { get; private set; }

    /// <summary>Label for the team-picker trigger, same shape as the session list's.</summary>
    public string TeamSummaryLabel { get; private set; } = "All teams";
    public IReadOnlyList<(int Id, string Name)> AvailableTeams { get; private set; } = [];
    public IReadOnlyList<VeSessionCount> Counts { get; private set; } = [];
    public string DateRangeSummaryLabel { get; private set; } = "Any time";

    internal static readonly int[] AllowedPageSizes = [25, 50, 100, 200];
    private const int DefaultPageSize = 50;

    /// <summary>
    /// pageNumber, not page: <c>page</c> is a Razor Pages route-value key and the route value
    /// provider runs before the query string one, so <c>?page=2</c> never binds and every page
    /// renders as the first. See #368, where exactly that shipped on the session list and went
    /// unnoticed for weeks — and <c>VeDirectory</c>, which carries the same note for the same reason.
    /// </summary>
    [BindProperty(SupportsGet = true, Name = "pageNumber")]
    public int PageNumber { get; set; } = 1;

    [BindProperty(SupportsGet = true, Name = "pageSize")]
    public int PageSize { get; set; } = DefaultPageSize;

    /// <summary>How many rows match the filters — not how many are on this page.</summary>
    public int TotalCount { get; private set; }
    public int TotalPages { get; private set; } = 1;

    /// <summary>Every active filter as route values, so the pager and the page-size selector keep the current view.</summary>
    public Dictionary<string, string?> FilterRoute()
    {
        var values = new Dictionary<string, string?>();
        if (TeamId is { } team) values["teamId"] = team.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(DateRange)) values["dateRange"] = DateRange;
        if (DateFrom is { } from) values["dateFrom"] = from.ToString("yyyy-MM-dd");
        if (DateTo is { } to) values["dateTo"] = to.ToString("yyyy-MM-dd");
        if (!string.IsNullOrWhiteSpace(Search)) values["search"] = Search;
        return values;
    }

    /// <summary>A pager link: every active filter, plus the page being moved to.</summary>
    public Dictionary<string, string?> PageRoute(int pageNumber)
    {
        var values = FilterRoute();
        values["pageNumber"] = pageNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        values["pageSize"] = PageSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return values;
    }

    public async Task OnGetAsync()
    {
        // See IndexModel.OnGetAsync's identical guard — [BindProperty(SupportsGet = true)] can leave
        // this string property null rather than its C# default when the query string omits the key,
        // and DateRangePresets.TryGetValue throws on a null key.
        DateRange ??= "";

        var user = await userManager.GetRequiredUserAsync(dbContext, User);

        AvailableTeams = await accessScope.GetAvailableTeamsAsync(dbContext, user);

        // null TeamId == every team this user can see, merged — same convention as the session list.
        var teamIds = accessScope.ResolveViewableTeamIds(user, TeamId);
        HasTeamContext = teamIds is null || teamIds.Count > 0;
        TeamSummaryLabel = TeamId is not null
            ? AvailableTeams.FirstOrDefault(t => t.Id == TeamId).Name ?? "All teams"
            : "All teams";

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var (fromUtc, toUtc) = ResolveDateRange(now);
        DateRangeSummaryLabel = (DateFrom, DateTo) switch
        {
            (not null, not null) => $"{DateFrom:MMM d} – {DateTo:MMM d}",
            (not null, null) => $"From {DateFrom:MMM d}",
            (null, not null) => $"Through {DateTo:MMM d}",
            _ => IndexModel.DateRangePresets.TryGetValue(DateRange, out var preset) ? preset.Label : "Any time"
        };

        if (HasTeamContext)
        {
            // An unrecognized pageSize comes from a hand-edited query string — fall back rather than
            // honouring an arbitrary one, which is what stops ?pageSize=100000 being a free way to
            // ask the server for everything.
            PageSize = AllowedPageSizes.Contains(PageSize) ? PageSize : DefaultPageSize;

            // RequestAborted, not None (#299): this page is a read with no POST handler at all, so
            // there is no write for a disconnect to tear — only a report query left running against
            // the shared SQLite file for a reader who has already gone.
            var page = await reportService.GetSessionCountsPageAsync(
                teamIds, fromUtc, toUtc, Search, PageNumber, PageSize, HttpContext.RequestAborted);

            Counts = page.Rows;
            TotalCount = page.TotalCount;
            TotalPages = page.TotalPages;
            // Read back the clamped value, so a stale ?pageNumber=9 highlights the page actually
            // shown rather than a number that no longer exists.
            PageNumber = page.PageNumber;
        }
    }

    /// <summary>Custom DateFrom/DateTo (if either is set) override the DateRange preset entirely — same reasoning as IndexModel's ResolveDateRange.</summary>
    private (DateTime? From, DateTime? To) ResolveDateRange(DateTime now)
    {
        if (DateFrom is not null || DateTo is not null)
        {
            return (
                DateFrom?.ToDateTime(TimeOnly.MinValue),
                DateTo?.ToDateTime(TimeOnly.MaxValue));
        }

        return IndexModel.DateRangePresets.TryGetValue(DateRange, out var preset)
            ? (now.AddDays(-preset.Days), now)
            : (null, null);
    }
}
