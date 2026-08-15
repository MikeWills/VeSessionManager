using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Text.Json;
using VeSessionManager.Core.Authorization;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Reporting;

namespace VeSessionManager.Web.Pages.SessionManager;

/// <summary>
/// The stats page (#63) — VE testing activity alongside applicant volume, over time.
///
/// <para><b>Not a breakdown by VEC.</b> The original ask read "both of VEC's testing and applicants",
/// which was voice-to-text for <b>VE</b> testing (settled 2026-08-15). Scoping is the ordinary
/// per-team kind and the <c>Vec</c> table is not involved.</para>
///
/// <para>Admin roles only, matching <see cref="VeRosterModel"/> and for the same reason: this shows a
/// per-VE session count, and a visible count-per-person invites comparison between volunteers that
/// nobody asked for. Keep the nav gate in _AppLayout.cshtml in step with this attribute — a role that
/// cannot load the page must not be shown a link that 403s.</para>
///
/// <para>Filters deliberately mirror <c>VeRoster</c> exactly (team picker plus IndexModel's shared
/// <c>DateRangePresets</c>), because VeRoster's own remarks say it "is expected to move into or merge
/// with a stats screen" — matching its controls now is what would make that merge a deletion rather
/// than a reconciliation.</para>
/// </summary>
[Authorize(Roles = RoleGroups.Admins)]
public class StatsModel(
    AppDbContext dbContext,
    UserManager<User> userManager,
    SessionAccessScope accessScope,
    SessionStatsService statsService,
    TimeProvider timeProvider) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public int? TeamId { get; set; }

    /// <summary>One of IndexModel.DateRangePresets' keys, or "" for no date filter.</summary>
    [BindProperty(SupportsGet = true)]
    public string DateRange { get; set; } = "";

    [BindProperty(SupportsGet = true)]
    public DateOnly? DateFrom { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateOnly? DateTo { get; set; }

    public bool HasTeamContext { get; private set; }
    public string TeamSummaryLabel { get; private set; } = "All teams";
    public string DateRangeSummaryLabel { get; private set; } = "Any time";
    public IReadOnlyList<(int Id, string Name)> AvailableTeams { get; private set; } = [];

    public SessionStatsReport Report { get; private set; } =
        new([], 0, 0, 0, 0, 0, 0, 0, 0, 0, null, []);

    /// <summary>
    /// "as of <em>date</em>" for the page head — the earliest session the figures actually include,
    /// so a reader can tell a genuinely quiet 2023 from a 2023 this deployment has no data for. Null
    /// when nothing is in range, where the page has no numbers to qualify anyway.
    /// </summary>
    public string? DataStartLabel => Report.EarliestSessionUtc is { } earliest
        ? EasternTimeFormatter.Format(earliest, "MMMM d, yyyy")
        : null;

    /// <summary>
    /// The monthly series as JSON for the charts.
    ///
    /// <para>Serialized here rather than written into the markup by a Razor loop because the CSP is
    /// <c>script-src 'self'</c>: no inline script can run, so the data has to reach the chart through
    /// an attribute the page's own JS file reads. Same constraint that put Chart.js in
    /// <c>wwwroot/lib</c> instead of a CDN reference.</para>
    /// </summary>
    public string ChartDataJson { get; private set; } = "{}";

    public async Task OnGetAsync()
    {
        // See IndexModel.OnGetAsync's identical guard — [BindProperty(SupportsGet = true)] can leave
        // this null rather than its C# default, and DateRangePresets.TryGetValue throws on a null key.
        DateRange ??= "";

        var user = await userManager.GetRequiredUserAsync(dbContext, User);

        AvailableTeams = await accessScope.GetAvailableTeamsAsync(dbContext, user);

        // null TeamId == every team this user can see, merged — the convention across every scoped
        // list here. TryResolveViewableTeamId would bounce a SystemAdmin who has not picked a team to
        // an empty page, which is the trap CLAUDE.md records for Applicant Status.
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

        if (!HasTeamContext)
        {
            return;
        }

        // RequestAborted: a read with no POST handler on the page, so there is no write a disconnect
        // could tear — only a report query left running against the shared SQLite file (#299).
        Report = await statsService.GetAsync(teamIds, fromUtc, toUtc, HttpContext.RequestAborted);

        ChartDataJson = JsonSerializer.Serialize(new
        {
            labels = Report.Periods.Select(p => p.MonthUtc.ToString("MMM yyyy")).ToList(),
            sessions = Report.Periods.Select(p => p.Sessions).ToList(),
            candidates = Report.Periods.Select(p => p.CandidatesTested).ToList(),
            passed = Report.Periods.Select(p => p.Passed).ToList(),
            failed = Report.Periods.Select(p => p.Failed).ToList(),
            newLicenses = Report.Periods.Select(p => p.NewLicenses).ToList(),
            upgrades = Report.Periods.Select(p => p.Upgrades).ToList(),
            technicians = Report.Periods.Select(p => p.Technicians).ToList(),
            generals = Report.Periods.Select(p => p.Generals).ToList(),
            extras = Report.Periods.Select(p => p.Extras).ToList()
        });
    }

    /// <summary>Custom DateFrom/DateTo (if either is set) override the preset entirely — same reasoning as IndexModel's ResolveDateRange.</summary>
    private (DateTime? From, DateTime? To) ResolveDateRange(DateTime now)
    {
        if (DateFrom is not null || DateTo is not null)
        {
            return (DateFrom?.ToDateTime(TimeOnly.MinValue), DateTo?.ToDateTime(TimeOnly.MaxValue));
        }

        return IndexModel.DateRangePresets.TryGetValue(DateRange, out var preset)
            ? (now.AddDays(-preset.Days), null)
            : (null, null);
    }

    /// <summary>Every filter as route values, so the team picker and range controls round-trip.</summary>
    public Dictionary<string, string?> FilterRoute()
    {
        var values = new Dictionary<string, string?>();
        if (TeamId is { } team) values["teamId"] = team.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(DateRange)) values["dateRange"] = DateRange;
        if (DateFrom is { } from) values["dateFrom"] = from.ToString("yyyy-MM-dd");
        if (DateTo is { } to) values["dateTo"] = to.ToString("yyyy-MM-dd");
        return values;
    }
}
