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
/// Phase 9b's "VE Roster" nav destination — Phase 7's "simple report: session count per VE,
/// filterable by date range" (VolunteerExaminerReportService.GetSessionCountsAsync), finally given
/// a UI. Not part of the design handoff's four mocked screens (only Sessions/session-detail were
/// mocked) — styled with the same design-system components (table/chip/eyebrow) since it's a plain
/// report, not something that needed its own visual design pass. TeamLead was added in the
/// TeamLead-read-only-view fix (see TODO.md) — this page is purely a read-only report already, so
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
/// </summary>
[Authorize(Roles = "SystemAdmin,TeamAdmin,SessionManager,TeamLead")]
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

    public bool HasTeamContext { get; private set; }
    public IReadOnlyList<(int Id, string Name)> AvailableTeams { get; private set; } = [];
    public IReadOnlyList<VeSessionCount> Counts { get; private set; } = [];
    public string DateRangeSummaryLabel { get; private set; } = "Any time";

    public async Task OnGetAsync()
    {
        // See IndexModel.OnGetAsync's identical guard — [BindProperty(SupportsGet = true)] can leave
        // this string property null rather than its C# default when the query string omits the key,
        // and DateRangePresets.TryGetValue throws on a null key.
        DateRange ??= "";

        var user = await userManager.GetUserWithManagerAsync(dbContext, User) ?? throw new InvalidOperationException("No authenticated user for an [Authorize]d page.");

        AvailableTeams = await accessScope.GetAvailableTeamsAsync(dbContext, user);

        var teamId = accessScope.TryResolveViewableTeamId(user, TeamId);
        TeamId = teamId;
        HasTeamContext = teamId is not null;

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var (fromUtc, toUtc) = ResolveDateRange(now);
        DateRangeSummaryLabel = (DateFrom, DateTo) switch
        {
            (not null, not null) => $"{DateFrom:MMM d} – {DateTo:MMM d}",
            (not null, null) => $"From {DateFrom:MMM d}",
            (null, not null) => $"Through {DateTo:MMM d}",
            _ => IndexModel.DateRangePresets.TryGetValue(DateRange, out var preset) ? preset.Label : "Any time"
        };

        if (teamId is int id)
        {
            Counts = await reportService.GetSessionCountsAsync(id, fromUtc, toUtc, CancellationToken.None);
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
