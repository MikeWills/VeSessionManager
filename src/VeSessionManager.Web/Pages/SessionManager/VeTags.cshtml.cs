using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Authorization;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Discord;
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
[RemembersFilters]
public class VeTagsModel(
    AppDbContext dbContext,
    UserManager<User> userManager,
    AdminAccessScope adminAccessScope,
    // GetAvailableTeamsAsync lives on SessionAccessScope, not AdminAccessScope — the two are
    // separate classes and only the latter knows how to collapse to one manageable team.
    SessionAccessScope accessScope,
    IDiscordGuildClient discordGuildClient,
    ILogger<VeTagsModel> logger,
    VolunteerExaminerManagementService managementService) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public int? TeamId { get; set; }

    public IReadOnlyList<(int Id, string Name)> AvailableTeams { get; private set; } = [];
    public IReadOnlyList<TagRow> Tags { get; private set; } = [];
    public int? ResolvedTeamId { get; private set; }

    /// <summary>
    /// The team's Discord roles, for the picker beside each tag (#519). Empty means the picker is
    /// replaced by a plain id box — see <see cref="LoadDiscordRolesAsync"/>.
    /// </summary>
    public IReadOnlyList<DiscordRoleSummary> AvailableRoles { get; private set; } = [];

    /// <summary>Deleting a tag takes it off everyone who had it, so the count is shown before someone clicks.</summary>
    public record TagRow(VeTag Tag, int AssignedCount);

    public async Task<IActionResult> OnGetAsync()
    {
        await LoadAsync();

        // Fetched here rather than in LoadAsync, which both verbs call: a POST always redirects to a
        // fresh GET, so fetching it on the way in would be a wasted Discord round trip on every save.
        // Same reasoning as the message rule screen's channel picker (#503). The one POST that does
        // ask Discord is a tag being mapped to a role it did not hold before — see RoleNameAsync.
        await LoadDiscordRolesAsync();
        return Page();
    }

    /// <summary>
    /// The swatch a colour picker starts on before anyone chooses. The app's own panel green, so an
    /// unconsidered "Use" tick still produces something that belongs on these screens.
    /// </summary>
    public const string DefaultSwatch = "#2f4f4a";

    public async Task<IActionResult> OnPostCreateAsync(string name, int sortOrder, string? color, bool useColor, string? discordRoleId)
    {
        await LoadAsync();
        if (ResolvedTeamId is not { } teamId)
        {
            return Forbid();
        }

        var roleId = ParseRoleId(discordRoleId);
        var user = await CurrentUserAsync();
        var (result, _) = await managementService.CreateTagAsync(
            teamId, name ?? "", sortOrder, ChosenColor(color, useColor),
            roleId, await RoleNameAsync(roleId, previousRoleId: null, previousRoleName: null),
            user.Id, HttpContext.RequestAborted);

        TempData[result == VeManagementResult.Success ? "StatusMessage" : "ErrorMessage"] = Describe(result, name, created: true);
        return RedirectToPage(new { teamId = TeamId });
    }

    public async Task<IActionResult> OnPostUpdateAsync(int tagId, string name, int sortOrder, string? color, bool useColor, string? discordRoleId)
    {
        await LoadAsync();

        // Same ownership check as Delete: the posted id must be one this page actually listed, so a
        // tag from another team's vocabulary can't be edited by crafting a form post.
        if (Tags.All(t => t.Tag.Id != tagId))
        {
            return Forbid();
        }

        var existing = Tags.Single(t => t.Tag.Id == tagId).Tag;
        var roleId = ParseRoleId(discordRoleId);
        var user = await CurrentUserAsync();
        var result = await managementService.UpdateTagAsync(
            tagId, name ?? "", sortOrder, ChosenColor(color, useColor),
            roleId, await RoleNameAsync(roleId, existing.DiscordRoleId, existing.DiscordRoleName),
            user.Id, HttpContext.RequestAborted);

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

    /// <summary>
    /// The posted role, or null for "none" — which both the picker's blank option and an empty id box
    /// produce. A value that isn't a number is treated as none rather than rejected: the box exists
    /// only as the fallback when Discord can't be reached, and there is nothing useful to say about a
    /// malformed snowflake that "no role is mapped" doesn't already show.
    /// </summary>
    private static ulong? ParseRoleId(string? posted) =>
        ulong.TryParse(posted?.Trim(), out var id) && id != 0 ? id : null;

    /// <summary>
    /// The display name to store beside <paramref name="roleId"/>.
    ///
    /// <para><b>An unchanged mapping keeps its stored name without asking Discord.</b> That is what
    /// keeps the ordinary save — a rename, a reorder, a colour change — free of a Discord round trip,
    /// and it is also the safer answer: when Discord is unreachable the picker is a typed id box, and
    /// re-saving a tag for an unrelated reason must not blank the name it already had.</para>
    ///
    /// <para>Only a genuinely new mapping costs a lookup, and only then because there is nothing else
    /// to name it with. If that lookup comes back empty the id is stored with a null name, which the
    /// screen renders as the bare number until someone saves it again with Discord reachable.</para>
    /// </summary>
    private async Task<string?> RoleNameAsync(ulong? roleId, ulong? previousRoleId, string? previousRoleName)
    {
        if (roleId is null)
        {
            return null;
        }

        if (roleId == previousRoleId)
        {
            return previousRoleName;
        }

        await LoadDiscordRolesAsync();
        return AvailableRoles.FirstOrDefault(r => r.Id == roleId.Value)?.Name;
    }

    /// <summary>
    /// The guild's roles, or an empty list for every reason one can't be had — no bot token, no
    /// <c>DiscordGuildId</c> on the team, the bot not in the server, or the lookup itself failing.
    /// All four collapse to the same fallback (a typed id box) rather than erroring the page, which
    /// is what keeps a team that doesn't use Discord able to manage its tags.
    /// </summary>
    private async Task LoadDiscordRolesAsync()
    {
        if (AvailableRoles.Count > 0 || ResolvedTeamId is not { } teamId || !discordGuildClient.IsConfigured)
        {
            return;
        }

        var guildId = await dbContext.Teams
            .Where(t => t.Id == teamId)
            .Select(t => t.DiscordGuildId)
            .FirstOrDefaultAsync(HttpContext.RequestAborted);
        if (guildId is not { } guild || guild == 0)
        {
            return;
        }

        try
        {
            AvailableRoles = await discordGuildClient.ListRolesAsync(guild, HttpContext.RequestAborted);
        }
        catch (Exception ex)
        {
            // One warning, then the fallback. A team whose bot was removed from the server should not
            // lose the ability to edit tag names because of it.
            logger.LogWarning(ex, "Could not read Discord roles for team {TeamId} — falling back to a typed role id", teamId);
        }
    }

    private static string Describe(VeManagementResult result, string? name, bool created) => result switch
    {
        VeManagementResult.Success => created ? $"Tag '{name}' created." : $"Tag '{name}' saved.",
        VeManagementResult.DuplicateTagName => "This team already has a tag with that name.",
        VeManagementResult.DuplicateDiscordRole => "Another tag on this team already uses that Discord role.",
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
