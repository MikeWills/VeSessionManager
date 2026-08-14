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
/// The per-team VE tag vocabulary (issue #142 phase 2) — "team member", "auditioning", "session
/// manager", "team lead", "admin" to start with, and whatever else a team invents.
///
/// <para><b>Tags grant no access to anything.</b> Several of the starting names deliberately match
/// real roles in this app's access model, because those are the words the team already uses, but a
/// VE tagged "admin" gets nothing from it. They exist for reporting and for choosing who to invite
/// to a session. <c>VeTagsGrantNoAccessTests</c> asserts that no authorization code reads them —
/// this is exactly the kind of promise that erodes the first time reading it would be convenient.</para>
///
/// <para>Unlike the directory, this page needs exactly one team: a tag belongs to one team's
/// vocabulary, so there is nothing to create while "all teams" is selected. That is what
/// <c>AdminAccessScope.TryResolveManageableTeamId</c> resolves — a SystemAdmin picks, a TeamAdmin is
/// locked to their own.</para>
/// </summary>
[Authorize(Roles = RoleGroups.Admins)]
public class VeTagsModel(
    AppDbContext dbContext,
    UserManager<User> userManager,
    AdminAccessScope adminAccessScope,
    // GetAvailableTeamsAsync lives on SessionAccessScope, not AdminAccessScope — the two are
    // separate classes and only the latter knows how to collapse to one manageable team.
    SessionAccessScope accessScope,
    VolunteerExaminerManagementService managementService) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public int? TeamId { get; set; }

    public IReadOnlyList<(int Id, string Name)> AvailableTeams { get; private set; } = [];
    public IReadOnlyList<TagRow> Tags { get; private set; } = [];
    public int? ResolvedTeamId { get; private set; }

    /// <summary>Deleting a tag takes it off everyone who had it, so the count is shown before someone clicks.</summary>
    public record TagRow(VeTag Tag, int AssignedCount);

    public async Task<IActionResult> OnGetAsync()
    {
        await LoadAsync();
        return Page();
    }

    /// <summary>
    /// The swatch a colour picker starts on before anyone chooses. The app's own panel green, so an
    /// unconsidered "Use" tick still produces something that belongs on these screens.
    /// </summary>
    public const string DefaultSwatch = "#2f4f4a";

    public async Task<IActionResult> OnPostCreateAsync(string name, int sortOrder, string? color, bool useColor)
    {
        await LoadAsync();
        if (ResolvedTeamId is not { } teamId)
        {
            return Forbid();
        }

        var user = await CurrentUserAsync();
        var (result, _) = await managementService.CreateTagAsync(teamId, name ?? "", sortOrder, ChosenColor(color, useColor), user.Id, HttpContext.RequestAborted);

        TempData[result == VeManagementResult.Success ? "StatusMessage" : "ErrorMessage"] = Describe(result, name, created: true);
        return RedirectToPage(new { teamId = TeamId });
    }

    public async Task<IActionResult> OnPostUpdateAsync(int tagId, string name, int sortOrder, string? color, bool useColor)
    {
        await LoadAsync();

        // Same ownership check as Delete: the posted id must be one this page actually listed, so a
        // tag from another team's vocabulary can't be edited by crafting a form post.
        if (Tags.All(t => t.Tag.Id != tagId))
        {
            return Forbid();
        }

        var user = await CurrentUserAsync();
        var result = await managementService.UpdateTagAsync(tagId, name ?? "", sortOrder, ChosenColor(color, useColor), user.Id, HttpContext.RequestAborted);

        TempData[result == VeManagementResult.Success ? "StatusMessage" : "ErrorMessage"] = Describe(result, name, created: false);
        return RedirectToPage(new { teamId = TeamId });
    }

    /// <summary>
    /// An <c>&lt;input type="color"&gt;</c> has no empty state — it always posts a value, and
    /// #000000 is a colour a team might genuinely pick, so it can't stand in for "none". The
    /// paired checkbox is what expresses "no colour"; unticked, whatever the picker holds is
    /// discarded.
    /// </summary>
    private static string? ChosenColor(string? color, bool useColor) => useColor ? color : null;

    private static string Describe(VeManagementResult result, string? name, bool created) => result switch
    {
        VeManagementResult.Success => created ? $"Tag '{name}' created." : $"Tag '{name}' saved.",
        VeManagementResult.DuplicateTagName => "This team already has a tag with that name.",
        VeManagementResult.NameRequired => "A tag name is required.",
        VeManagementResult.InvalidColor => "That color wasn't a valid #RRGGBB value.",
        _ => created ? "Could not create that tag." : "Could not save that tag."
    };

    public async Task<IActionResult> OnPostDeleteAsync(int tagId)
    {
        await LoadAsync();

        // Verified against the tags this user can actually manage — a posted id from another team's
        // vocabulary must not be deletable by URL.
        if (Tags.All(t => t.Tag.Id != tagId))
        {
            return Forbid();
        }

        var user = await CurrentUserAsync();
        var result = await managementService.DeleteTagAsync(tagId, user.Id, HttpContext.RequestAborted);

        TempData[result == VeManagementResult.Success ? "StatusMessage" : "ErrorMessage"] =
            result == VeManagementResult.Success ? "Tag deleted." : "Could not delete that tag.";
        return RedirectToPage(new { teamId = TeamId });
    }

    private async Task<User> CurrentUserAsync() =>
        await userManager.GetRequiredUserAsync(dbContext, User);

    private async Task LoadAsync()
    {
        var user = await CurrentUserAsync();
        AvailableTeams = await accessScope.GetAvailableTeamsAsync(dbContext, user);
        ResolvedTeamId = adminAccessScope.TryResolveManageableTeamId(user, TeamId, [.. AvailableTeams.Select(t => t.Id)]);

        if (ResolvedTeamId is not { } teamId)
        {
            Tags = [];
            return;
        }

        Tags = await dbContext.VeTags
            .Where(t => t.TeamId == teamId)
            .OrderBy(t => t.SortOrder).ThenBy(t => t.Name)
            .Select(t => new TagRow(t, t.Assignments.Count))
            .ToListAsync(HttpContext.RequestAborted);
    }
}
