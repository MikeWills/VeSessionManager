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
/// The VE directory (issue #142 phase 2) — who a team's volunteer examiners are, how to reach them,
/// and whether they are current. Distinct from VE Roster, which is the session-count report; this
/// page is the one issue #142 is actually about, and the issue asks for both to live under the VEs
/// nav dropdown.
///
/// <para><b>TeamAdmin/SystemAdmin only, and that is a data-protection boundary rather than a
/// tidiness one.</b> These rows carry VEs' home addresses and phone numbers, which — unlike call
/// sign, FRN and license class — are not public FCC record data. A VE's public record typically
/// carries a PO box precisely because they chose not to publish where they live. Session Managers
/// and Team Leads get no access at all. Keep the nav gate in _AppLayout.cshtml in step with this
/// attribute; a role that cannot load the page must not be shown a link that 403s.</para>
/// </summary>
[Authorize(Roles = "SystemAdmin,TeamAdmin")]
public class VeDirectoryModel(
    AppDbContext dbContext,
    UserManager<User> userManager,
    SessionAccessScope accessScope,
    VolunteerExaminerDirectoryService directoryService,
    TimeProvider timeProvider) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public int? TeamId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public int? TagId { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool IncludeInactive { get; set; }

    public bool HasTeamContext { get; private set; }
    public string TeamSummaryLabel { get; private set; } = "All teams";
    public IReadOnlyList<(int Id, string Name)> AvailableTeams { get; private set; } = [];
    public IReadOnlyList<VeDirectoryRow> Rows { get; private set; } = [];
    public IReadOnlyList<VeTag> AvailableTags { get; private set; } = [];

    /// <summary>Taken once per request so every row's license status and day count are derived against the same instant — otherwise a list rendered across midnight ET could disagree with itself.</summary>
    public DateTime UtcNow { get; private set; }

    public async Task OnGetAsync()
    {
        UtcNow = timeProvider.GetUtcNow().UtcDateTime;

        // GetUserWithManagerAsync, never the bare GetUserAsync — GetEffectiveTeamIds reads
        // user.UserTeams, which the plain UserManager call leaves unloaded, silently giving a
        // TeamAdmin an empty team set. See CLAUDE.md.
        var user = await userManager.GetUserWithManagerAsync(dbContext, User)
            ?? throw new InvalidOperationException("No authenticated user for an [Authorize]d page.");

        AvailableTeams = await accessScope.GetAvailableTeamsAsync(dbContext, user);
        HasTeamContext = AvailableTeams.Count > 0 || user.Role == UserRole.SystemAdmin;

        // ResolveViewableTeamIds, not TryResolveViewableTeamId: null here means "every team", which
        // is what lets a SystemAdmin see the directory merged rather than being bounced to an empty
        // page for having no single team selected.
        var teamIds = accessScope.ResolveViewableTeamIds(user, TeamId);

        TeamSummaryLabel = TeamId is not null
            ? AvailableTeams.FirstOrDefault(t => t.Id == TeamId).Name ?? "All teams"
            : "All teams";

        AvailableTags = await dbContext.VeTags
            .Where(t => teamIds == null || teamIds.Contains(t.TeamId))
            .OrderBy(t => t.SortOrder).ThenBy(t => t.Name)
            .ToListAsync(HttpContext.RequestAborted);

        Rows = await directoryService.GetDirectoryAsync(teamIds, Search, TagId, IncludeInactive, HttpContext.RequestAborted);
    }
}
