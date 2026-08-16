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

/// <summary>
/// Editing one email template (#395).
///
/// <para>This was every template's editor stacked on the list page — trigger panel, placeholder
/// chips, subject, Quill, save, per template, all rendered at once. Finding the one you wanted meant
/// scrolling past the rest, and the page grew with every template added. Splitting it means the list
/// answers "what have we got" and this answers "what does this one say".</para>
///
/// <para>Authorization is against the template's <b>own</b> team, never a posted one — the same
/// re-check the list page's handlers do, and for the same reason: a TeamAdmin posting their own valid
/// teamId alongside another team's templateId is the cross-tenant hole.</para>
/// </summary>
[Authorize(Roles = RoleGroups.Admins)]
public class EmailTemplateEditModel(
    AppDbContext dbContext,
    UserManager<User> userManager,
    AdminAccessScope adminAccessScope,
    EmailTemplateAdminService emailTemplateAdminService) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public int Id { get; set; }

    [BindProperty]
    public string Subject { get; set; } = "";

    [BindProperty]
    public string Body { get; set; } = "";

    public EmailTemplate Template { get; private set; } = null!;

    public string Label => Template.DisplayName ?? EmailTemplateLabels.For(Template.Key);

    public EmailTemplateTrigger? Trigger => EmailTemplateTriggers.For(Template.Key);

    public bool IsRetired => EmailTemplateTriggers.IsRetired(Template.Key);

    public IReadOnlyList<string> Placeholders => Template.IsUserDefined
        ? Template.Audience == EmailTemplateAudience.VolunteerExaminers
            ? [.. VolunteerExaminerPlaceholderValues.Names, .. EmailTemplatePlaceholders.Universal]
            : [.. EmailTemplatePlaceholders.ForUserDefined(), .. EmailTemplatePlaceholders.Universal]
        : EmailTemplatePlaceholders.ForEditor(Template.Key);

    public async Task<IActionResult> OnGetAsync()
    {
        var loaded = await LoadAsync();
        if (loaded is not null) return loaded;

        Subject = Template.Subject;
        Body = Template.Body;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var loaded = await LoadAsync();
        if (loaded is not null) return loaded;

        var user = await userManager.GetRequiredUserAsync(dbContext, User);
        var result = await emailTemplateAdminService.UpdateAsync(Id, Subject, Body, user.Id, HttpContext.RequestAborted);

        if (result == EmailTemplateActionResult.Success)
        {
            var unknown = emailTemplateAdminService.FindUnknownPlaceholders(Template.Key, Subject, Body);
            TempData["StatusMessage"] = unknown.Count == 0
                ? "Template updated."
                : $"Template updated — but references unknown placeholder(s): {string.Join(", ", unknown)}. Check for a typo.";
            return RedirectToPage("/Admin/EmailTemplates", new { teamId = Template.TeamId });
        }

        TempData["ErrorMessage"] = result == EmailTemplateActionResult.ContentRequired
            ? "A template needs both a subject and a body."
            : "Template not found.";
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostRenameAsync(string name)
    {
        var loaded = await LoadAsync();
        if (loaded is not null) return loaded;

        var user = await userManager.GetRequiredUserAsync(dbContext, User);
        var result = await emailTemplateAdminService.RenameAsync(Id, name, user.Id, HttpContext.RequestAborted);
        SetStatus(result, "Template renamed.");
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var loaded = await LoadAsync();
        if (loaded is not null) return loaded;

        var teamId = Template.TeamId;
        var user = await userManager.GetRequiredUserAsync(dbContext, User);
        var result = await emailTemplateAdminService.DeleteAsync(Id, user.Id, HttpContext.RequestAborted);
        SetStatus(result, "Template deleted.");

        return result == EmailTemplateActionResult.Success
            ? RedirectToPage("/Admin/EmailTemplates", new { teamId })
            : RedirectToPage(new { id = Id });
    }

    private void SetStatus(EmailTemplateActionResult result, string success) =>
        TempData[result == EmailTemplateActionResult.Success ? "StatusMessage" : "ErrorMessage"] = result switch
        {
            EmailTemplateActionResult.Success => success,
            EmailTemplateActionResult.NameRequired => "A template needs a name.",
            EmailTemplateActionResult.ContentRequired => "A template needs both a subject and a body.",
            // Worth spelling out: not a permission problem, but that something in the app sends this
            // one and has no other way to find it.
            EmailTemplateActionResult.NotUserDefined =>
                "That is one of the app's own templates — it can be edited, but not renamed or deleted, because a background job sends it by name.",
            _ => "Template not found."
        };

    private async Task<IActionResult?> LoadAsync()
    {
        var user = await userManager.GetUserWithManagerAsync(dbContext, User);
        if (user is null)
        {
            return Forbid();
        }

        var template = await dbContext.EmailTemplates.FirstOrDefaultAsync(t => t.Id == Id, HttpContext.RequestAborted);
        if (template is null)
        {
            return NotFound();
        }

        if (!adminAccessScope.CanManageTeam(user, template.TeamId))
        {
            return Forbid();
        }

        Template = template;
        return null;
    }
}
