using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Authorization;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Web.Pages.Admin;

/// <summary>
/// Creating a template a team wrote for itself (#144), on its own page.
///
/// <para>This was a form at the bottom of the Email Templates list, on the reasoning that editing
/// what already exists is the common case and a "New template" button at the top would suggest
/// otherwise. True, and beside the point once there are eleven shipped templates above it: the form
/// is simply a long way down, and "past the fold" is not a discoverability strategy.</para>
///
/// <para>Authorized against the posted team, which is the only id there is — nothing exists yet to
/// check against, so <c>CanManageTeam</c> is the whole guard, same as the handler it replaces.</para>
/// </summary>
[Authorize(Roles = RoleGroups.Admins)]
public class EmailTemplateNewModel(
    AppDbContext dbContext,
    UserManager<User> userManager,
    AdminAccessScope adminAccessScope,
    EmailTemplateAdminService emailTemplateAdminService) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public int TeamId { get; set; }

    [BindProperty]
    public string Name { get; set; } = "";

    [BindProperty]
    public EmailTemplateAudience Audience { get; set; } = EmailTemplateAudience.Candidates;

    [BindProperty]
    public string Subject { get; set; } = "";

    [BindProperty]
    public string Body { get; set; } = "";

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await userManager.GetUserWithManagerAsync(dbContext, User);
        return user is null || !adminAccessScope.CanManageTeam(user, TeamId) ? Forbid() : Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var user = await userManager.GetUserWithManagerAsync(dbContext, User);
        if (user is null || !adminAccessScope.CanManageTeam(user, TeamId))
        {
            return Forbid();
        }

        var result = await emailTemplateAdminService.CreateAsync(
            TeamId, Name, Subject, Body, Audience, user.Id, HttpContext.RequestAborted);

        if (result == EmailTemplateActionResult.Success)
        {
            TempData["StatusMessage"] = "Template created.";
            return RedirectToPage("/Admin/EmailTemplates", new { teamId = TeamId });
        }

        TempData["ErrorMessage"] = result switch
        {
            EmailTemplateActionResult.NameRequired => "A template needs a name.",
            EmailTemplateActionResult.ContentRequired => "A template needs both a subject and a body.",
            _ => "Template not created."
        };
        return RedirectToPage(new { teamId = TeamId });
    }
}
