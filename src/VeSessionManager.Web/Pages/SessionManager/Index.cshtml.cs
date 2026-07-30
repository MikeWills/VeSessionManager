using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Authorization;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Web.Pages.SessionManager;

/// <summary>
/// Phase 9b's real session list, replacing the Phase 9a placeholder — recreated from
/// design_handoff_vesessionmanager_admin_ui/session-list.html. TeamAdmin is included alongside
/// SessionManager (not just SystemAdmin/SessionManager, the original 9a placeholder's attribute)
/// because SessionAccessScope already treats TeamAdmin as an equal-scope superset of SessionManager
/// for session visibility — see docs/admin-auth.md's role hierarchy. TeamLead was added in the
/// TeamLead-read-only-view fix (see TODO.md) — SessionAccessScope.Scope already resolves a
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
/// Session ID column (issue #35, 2026-07-29): ExamToolsSessionId — the same identifier already shown
/// in the Detail page's breadcrumb/title — now gets its own column instead of being buried inside the
/// title cell's sub-line text, so it's usable to tell sessions apart at a glance and cross-reference
/// against ExamTools' own UI, per the issue's "know whose session is whose" ask.
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
/// </summary>
[Authorize(Roles = "SystemAdmin,TeamAdmin,SessionManager,TeamLead")]
public class IndexModel(AppDbContext dbContext, UserManager<User> userManager, SessionAccessScope accessScope, TimeProvider timeProvider) : PageModel
{
    private const string FilterCookieName = "vsm_session_filters";

    // Realigned 2026-07-29 (reported filter-row confusion) to match the labels ToRow's statusLabel
    // actually shows in the Status column (Active/Reschedule flagged/Completed/Cancelled) instead
    // of the old Upcoming/NeedsReview/Past set, which didn't correspond to anything in the table.
    // "Upcoming" was a time-window concept, not a lifecycle status, so it moved to the Date range
    // filter instead (DateRange == "Upcoming", handled alongside DateRangePresets below).
    private static readonly string[] KnownStatuses = ["Active", "RescheduleFlagged", "Completed", "Cancelled"];
    internal static readonly int[] AllowedPageSizes = [10, 25, 50, 100];
    private const int DefaultPageSize = 10;
    private const string UpcomingDateRangeKey = "Upcoming";

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

    [BindProperty(SupportsGet = true, Name = "page")]
    public int PageNumber { get; set; } = 1;

    /// <summary>One of DateRangePresets' keys, or "" for no date filter.</summary>
    [BindProperty(SupportsGet = true)]
    public string DateRange { get; set; } = "";

    /// <summary>Explicit custom range — set (either or both) to override DateRange.</summary>
    [BindProperty(SupportsGet = true)]
    public DateOnly? DateFrom { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateOnly? DateTo { get; set; }

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
        // [BindProperty(SupportsGet = true)] can leave a string property null rather than its C#
        // default when the request's query string omits the key entirely (confirmed live 2026-07-29,
        // e.g. unchecking every Status checkbox and submitting) — unlike Status (List<string>) and
        // PageSize (int) on this same page, which correctly keep their defaults. Normalize before any
        // DateRangePresets lookup, since Dictionary.ContainsKey/TryGetValue throw on a null key.
        DateRange ??= "";

        if (Applied)
        {
            PageSize = AllowedPageSizes.Contains(PageSize) ? PageSize : DefaultPageSize;
            DateRange = IsKnownDateRange(DateRange) ? DateRange : "";
            SaveFilterCookie(Status, TeamId, PageSize, DateRange);
        }
        else
        {
            var (cookieStatus, cookieTeamId, cookiePageSize, cookieDateRange) = ReadFilterCookie();
            Status = cookieStatus ?? [];
            TeamId = cookieTeamId;
            PageSize = cookiePageSize ?? DefaultPageSize;
            DateRange = cookieDateRange ?? UpcomingDateRangeKey;
            PageNumber = 1;
        }

        Status = Status.Where(s => KnownStatuses.Contains(s)).Distinct().ToList();
        StatusSummaryLabel = Status.Count switch
        {
            0 => "All",
            1 => StatusLabel(Status[0]),
            _ => $"{Status.Count} selected"
        };
        PageNumber = Math.Max(1, PageNumber);

        DateRangeSummaryLabel = (DateFrom, DateTo) switch
        {
            (not null, not null) => $"{DateFrom:MMM d} – {DateTo:MMM d}",
            (not null, null) => $"From {DateFrom:MMM d}",
            (null, not null) => $"Through {DateTo:MMM d}",
            _ when DateRange == UpcomingDateRangeKey => "Upcoming",
            _ => DateRangePresets.TryGetValue(DateRange, out var preset) ? preset.Label : "Any time"
        };

        var user = await userManager.GetUserWithManagerAsync(dbContext, User) ?? throw new InvalidOperationException("No authenticated user for an [Authorize]d page.");
        var now = timeProvider.GetUtcNow().UtcDateTime;

        AvailableTeams = await accessScope.GetAvailableTeamsAsync(dbContext, user);
        TeamSummaryLabel = TeamId is not null
            ? AvailableTeams.FirstOrDefault(t => t.Id == TeamId).Name ?? "All teams"
            : "All teams";

        IQueryable<Session> query = accessScope.Scope(dbContext.Sessions, user, TeamId)
            .Include(s => s.Vec)
            .Include(s => s.Team)
            .Include(s => s.Candidates);

        // These mirror ToRow's statusLabel priority exactly (Cancelled > Reschedule flagged >
        // Completed > Active) so a checked box always matches what the Status column shows.
        var wantActive = Status.Contains("Active");
        var wantRescheduleFlagged = Status.Contains("RescheduleFlagged");
        var wantCompleted = Status.Contains("Completed");
        var wantCancelled = Status.Contains("Cancelled");
        if (wantActive || wantRescheduleFlagged || wantCompleted || wantCancelled)
        {
            query = query.Where(s =>
                (wantActive && s.Status == SessionStatus.Active && !s.RescheduleFlaggedForReview && s.TestingCompletedUtc == null)
                || (wantRescheduleFlagged && s.Status == SessionStatus.Active && s.RescheduleFlaggedForReview)
                || (wantCompleted && s.Status == SessionStatus.Active && !s.RescheduleFlaggedForReview && s.TestingCompletedUtc != null)
                || (wantCancelled && s.Status == SessionStatus.Cancelled));
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
        // "Upcoming" preset is the opposite: its whole point is the *soonest* session, so it keeps
        // the default ascending sort instead of flipping.
        var isUpcomingPreset = DateRange == UpcomingDateRangeKey && DateFrom is null && DateTo is null;
        query = (dateFromUtc is not null || dateToUtc is not null) && !isUpcomingPreset
            ? query.OrderByDescending(s => s.ScheduledStartUtc)
            : query.OrderBy(s => s.ScheduledStartUtc);

        TotalCount = await query.CountAsync();
        TotalPages = Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
        PageNumber = Math.Min(PageNumber, TotalPages);

        var sessions = await query.Skip((PageNumber - 1) * PageSize).Take(PageSize).ToListAsync();
        Sessions = sessions.Select(ToRow).ToList();
    }

    /// <summary>Builds a Prev/Next/page-size link preserving every current filter — asp-route-* tag helpers can't represent Status's multiple values, so this constructs the query string directly.</summary>
    public string BuildPageUrl(int page, int? pageSizeOverride = null)
    {
        var qs = new List<string> { "applied=true" };
        qs.AddRange(Status.Select(s => $"status={Uri.EscapeDataString(s)}"));
        if (TeamId is not null)
        {
            qs.Add($"teamId={TeamId}");
        }
        qs.Add($"pageSize={pageSizeOverride ?? PageSize}");
        qs.Add($"page={page}");
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

        return DateRangePresets.TryGetValue(DateRange, out var preset)
            ? (now.AddDays(-preset.Days), now)
            : (null, null);
    }

    private static bool IsKnownDateRange(string dateRange) =>
        dateRange == UpcomingDateRangeKey || DateRangePresets.ContainsKey(dateRange);

    private static string StatusLabel(string status) => status switch
    {
        "Active" => "Active",
        "RescheduleFlagged" => "Reschedule flagged",
        "Completed" => "Completed",
        "Cancelled" => "Cancelled",
        _ => status
    };

    private (List<string>? Status, int? TeamId, int? PageSize, string? DateRange) ReadFilterCookie()
    {
        if (!Request.Cookies.TryGetValue(FilterCookieName, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return (null, null, null, null);
        }

        var parts = raw.Split('|', 4);
        var status = parts[0].Length == 0 ? [] : parts[0].Split(',').Where(s => KnownStatuses.Contains(s)).ToList();
        int? teamId = parts.Length > 1 && int.TryParse(parts[1], out var id) ? id : null;
        int? pageSize = parts.Length > 2 && int.TryParse(parts[2], out var size) && AllowedPageSizes.Contains(size) ? size : null;
        string? dateRange = parts.Length > 3 && IsKnownDateRange(parts[3]) ? parts[3] : null;
        return (status, teamId, pageSize, dateRange);
    }

    private void SaveFilterCookie(List<string> status, int? teamId, int pageSize, string dateRange)
    {
        var value = $"{string.Join(",", status)}|{teamId}|{pageSize}|{dateRange}";
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
            IsEssential = true
        });
    }

    private static SessionRow ToRow(Session s)
    {
        // ExamToolsSessionId gets its own column (issue #35 — "know whose session is whose"),
        // not repeated in the sub-line the way it used to be.
        var subParts = new List<string>();
        if (s.ZoomMeetingId is not null)
        {
            subParts.Add("Zoom");
        }
        if (s.TestingCompletedUtc is not null)
        {
            subParts.Add("Completed");
        }
        if (s.Status == SessionStatus.Cancelled)
        {
            subParts.Add("Cancelled");
        }

        var (statusClass, statusLabel) = s.Status == SessionStatus.Cancelled ? ("chip-brick", "Cancelled")
            : s.RescheduleFlaggedForReview ? ("chip-amber", "Reschedule flagged")
            : s.TestingCompletedUtc is not null ? ("chip-neutral", "Completed")
            : ("chip-green", "Active");

        var (vecClass, vecLabel) = s.Status == SessionStatus.Cancelled ? ("chip-neutral", "—")
            : s.VecSubmissionStatus == VecSubmissionStatus.Submitted ? ("chip-green", "Submitted")
            : ("chip-neutral", "Not submitted");

        return new SessionRow(
            s.Id,
            s.ExamToolsSessionId,
            s.ScheduledStartUtc.ToString("ddd, MMM d · h:mm tt", CultureInfo.InvariantCulture),
            string.Join(" · ", subParts),
            s.Vec.Name,
            s.Team.Name,
            s.Candidates.Count,
            s.RescheduleFlaggedForReview,
            statusClass, statusLabel,
            vecClass, vecLabel);
    }

    public record SessionRow(
        int Id,
        string ExamToolsSessionId,
        string TitleLine,
        string SubLine,
        string VecName,
        string TeamName,
        int CandidateCount,
        bool RescheduleFlagged,
        string StatusChipClass,
        string StatusChipLabel,
        string VecSubmissionChipClass,
        string VecSubmissionChipLabel);
}
