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
[Authorize(Roles = RoleGroups.Admins)]
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
        AvailableTeams = await adminAccessScope.GetAvailableTeamsAsync(dbContext, user, HttpContext.RequestAborted);

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
            .Select(t => new TemplateRow(t.Id, t.Key, t.Subject, t.Body, t.UpdatedUtc, t.IsUserDefined, t.DisplayName, t.Audience))
            .ToListAsync(HttpContext.RequestAborted);

        return Page();
    }

    /// <summary>
    /// Creates a template this team wrote for itself (#144). Authorized against the posted team,
    /// which is the only id available — there is no existing row to check against, so
    /// <c>CanManageTeam</c> is the whole guard here.
    /// </summary>
    public async Task<IActionResult> OnPostCreateAsync(int teamId, string name, string subject, string body, EmailTemplateAudience audience)
    {
        var user = await userManager.GetUserWithManagerAsync(dbContext, User);
        if (user is null || !adminAccessScope.CanManageTeam(user, teamId))
        {
            return Forbid();
        }

        var result = await emailTemplateAdminService.CreateAsync(teamId, name, subject, body, audience, user.Id, CancellationToken.None);
        TempData[result == EmailTemplateActionResult.Success ? "StatusMessage" : "ErrorMessage"] = Describe(result, "created");
        return RedirectToPage(new { teamId });
    }




    private static string Describe(EmailTemplateActionResult result, string verb) => result switch
    {
        EmailTemplateActionResult.Success => $"Template {verb}.",
        EmailTemplateActionResult.NameRequired => "A template needs a name.",
        EmailTemplateActionResult.ContentRequired => "A template needs both a subject and a body.",
        // The one worth spelling out: it is not a permission problem, it is that something in the app
        // sends this template and has no other way to find it.
        EmailTemplateActionResult.NotUserDefined =>
            "That is one of the app's own templates — it can be edited, but not renamed or deleted, because a background job sends it by name.",
        _ => "Template not found."
    };


    /// <summary>
    /// Templates grouped by where they fall in a session's life, in the order those things happen.
    /// A Key with no trigger registry entry falls into a trailing group rather than being dropped —
    /// an unrecognized template must still be editable.
    ///
    /// <para>Team-defined templates land in that trailing group by construction, since nothing
    /// registers a trigger for them. That is the honest place for them: they belong to no phase
    /// because nothing sends them on a schedule.</para>
    /// </summary>
    public IReadOnlyList<TemplateGroup> GroupedTemplates => [.. Templates
        .GroupBy(t => EmailTemplateTriggers.For(t.Key)?.Phase)
        // null (unknown Key) sorts last; otherwise enum declaration order is display order.
        .OrderBy(g => g.Key is null ? int.MaxValue : (int)g.Key)
        .Select(g => new TemplateGroup(
            g.Key?.Label() ?? "Your own templates",
            g.Key switch
            {
                EmailTemplatePhase.AtRegistration => "Sent around the point someone signs up for a session.",
                EmailTemplatePhase.PreSession => "Sent between registration and the session itself.",
                EmailTemplatePhase.PostSession => "Sent after the exam has been sat — including everything waiting on the FCC, which always comes afterwards.",
                _ => "Nothing sends these on its own. Pick one on a session's \"Email candidates\" screen, edit it, and send it to whoever you choose."
            },
            [.. g]))];

    public record TemplateGroup(string Label, string Blurb, IReadOnlyList<TemplateRow> Templates);

    /// <summary>What causes this template to be sent — see EmailTemplateTriggers. Null for a Key with no registry entry, in which case the page shows nothing rather than inventing a description.</summary>
    public static EmailTemplateTrigger? TriggerFor(string key) => EmailTemplateTriggers.For(key);

    /// <summary>
    /// Whether this row is left over from a version that sent it and no longer does. Seeding never
    /// deletes, so the row survives the feature — and an editable template nothing sends is worse
    /// than no template at all, because somebody maintains it and nobody receives it.
    /// </summary>
    public static bool IsRetired(string key) => EmailTemplateTriggers.IsRetired(key);

    /// <param name="DisplayName">Set only for a team's own template; the shipped ones take their label from <c>EmailTemplateLabels</c>, so a name lives in one place rather than in every team's row.</param>
    public record TemplateRow(int Id, string Key, string Subject, string Body, DateTime? UpdatedUtc, bool IsUserDefined, string? DisplayName, EmailTemplateAudience Audience)
    {
        public string Label => DisplayName ?? EmailTemplateLabels.For(Key);
    }
}
