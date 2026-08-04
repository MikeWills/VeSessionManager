using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Authorization;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Email;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Web.Pages.Admin;

/// <summary>Phase 9c: per-Team EmailTemplate Subject/Body editing, with the available-placeholder chip list per Key. Same team-picker/lock pattern as TeamSettings.</summary>
[Authorize(Roles = "SystemAdmin,TeamAdmin")]
public class EmailTemplatesModel(AppDbContext dbContext, UserManager<User> userManager, AdminAccessScope adminAccessScope, EmailTemplateAdminService emailTemplateAdminService) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public int? TeamId { get; set; }

    public bool IsSystemAdmin { get; private set; }

    /// <summary>Label for the team-picker trigger. "Select a team…" rather than "All teams" — this page edits one team's configuration, so there is no merged view to fall back to.</summary>
    public string TeamSummaryLabel { get; private set; } = "Select a team…";

    public IReadOnlyList<(int Id, string Name)> AvailableTeams { get; private set; } = [];
    public IReadOnlyList<TemplateRow> Templates { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await userManager.GetUserWithManagerAsync(dbContext, User);
        if (user is null)
        {
            return Forbid();
        }

        IsSystemAdmin = user.Role == UserRole.SystemAdmin;
        AvailableTeams = await adminAccessScope.ScopeTeams(dbContext.Teams, user)
            .OrderBy(t => t.Name).Select(t => new ValueTuple<int, string>(t.Id, t.Name)).ToListAsync();

        var effectiveTeamId = adminAccessScope.TryResolveManageableTeamId(user, TeamId, AvailableTeams.Select(t => t.Id).ToList());
        // See TeamSettings: keep the picker's rendered state in step with the auto-selection.
        TeamId = effectiveTeamId;
        TeamSummaryLabel = effectiveTeamId is not null
            ? AvailableTeams.FirstOrDefault(t => t.Id == effectiveTeamId).Name ?? "Select a team…"
            : "Select a team…";
        if (effectiveTeamId is null)
        {
            return Page();
        }

        TeamId = effectiveTeamId.Value;
        Templates = await dbContext.EmailTemplates
            .Where(t => t.TeamId == effectiveTeamId.Value)
            .OrderBy(t => t.Key)
            .Select(t => new TemplateRow(t.Id, t.Key, t.Subject, t.Body, t.UpdatedUtc))
            .ToListAsync();

        return Page();
    }

    public async Task<IActionResult> OnPostUpdateAsync(int templateId, int teamId, string subject, string body)
    {
        var user = await userManager.GetUserWithManagerAsync(dbContext, User);
        if (user is null)
        {
            return Forbid();
        }

        // Authorize against the template's actual owning team, not the client-supplied teamId —
        // otherwise a TeamAdmin could submit their own (valid) teamId alongside a templateId that
        // belongs to a different team and edit that team's template (cross-tenant IDOR).
        var existingTemplate = await dbContext.EmailTemplates.FirstOrDefaultAsync(t => t.Id == templateId);
        if (existingTemplate is null || !adminAccessScope.CanManageTeam(user, existingTemplate.TeamId))
        {
            return Forbid();
        }

        var result = await emailTemplateAdminService.UpdateAsync(templateId, subject, body, user.Id, CancellationToken.None);
        if (result == EmailTemplateActionResult.Success)
        {
            var unknown = emailTemplateAdminService.FindUnknownPlaceholders(existingTemplate.Key, subject, body);
            TempData["StatusMessage"] = unknown.Count == 0
                ? "Template updated."
                : $"Template updated — but references unknown placeholder(s): {string.Join(", ", unknown)}. Check for a typo.";
        }
        else
        {
            TempData["ErrorMessage"] = "Template not found.";
        }

        return RedirectToPage(new { teamId = existingTemplate.TeamId });
    }

    public static IReadOnlyList<string> PlaceholdersFor(string key) => EmailTemplatePlaceholders.For(key);

    public record TemplateRow(int Id, string Key, string Subject, string Body, DateTime? UpdatedUtc);
}
