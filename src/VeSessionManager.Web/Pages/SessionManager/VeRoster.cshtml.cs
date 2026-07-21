using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
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
/// report, not something that needed its own visual design pass.
/// </summary>
[Authorize(Roles = "SystemAdmin,TeamAdmin,SessionManager")]
public class VeRosterModel(UserManager<User> userManager, SessionAccessScope accessScope, VolunteerExaminerReportService reportService) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public DateTime? From { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateTime? To { get; set; }

    public bool HasTeamContext { get; private set; }
    public IReadOnlyList<VeSessionCount> Counts { get; private set; } = [];

    public async Task OnGetAsync()
    {
        var user = await userManager.GetUserAsync(User) ?? throw new InvalidOperationException("No authenticated user for an [Authorize]d page.");
        var teamId = accessScope.GetEffectiveTeamId(user);
        HasTeamContext = teamId is not null;
        if (teamId is int id)
        {
            Counts = await reportService.GetSessionCountsAsync(id, From, To, CancellationToken.None);
        }
    }
}
