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
/// Bulk VE import (issue #142 phase 4). Two steps, always: upload shows what would happen, and
/// nothing is written until that is confirmed.
///
/// <para><b>The confirm step re-posts the file's text, not the parsed rows.</b> Sending a structure
/// back would mean the thing applied is whatever the browser returned, and the server would have to
/// re-validate all of it anyway. Posting the same text means preview and apply run the identical
/// parse, so what was reviewed is what happens.</para>
///
/// <para>Needs exactly one team, like the tag screen — an imported VE joins a roster, and there is
/// no sensible answer to "which one" while "all teams" is selected.</para>
/// </summary>
[Authorize(Roles = "SystemAdmin,TeamAdmin")]
public class VeImportModel(
    AppDbContext dbContext,
    UserManager<User> userManager,
    SessionAccessScope accessScope,
    AdminAccessScope adminAccessScope,
    VolunteerExaminerImportService importService) : PageModel
{
    /// <summary>Generous for a VE roster, small enough that a mis-picked file fails fast rather than being parsed.</summary>
    private const long MaxUploadBytes = 512 * 1024;

    [BindProperty(SupportsGet = true)]
    public int? TeamId { get; set; }

    [BindProperty]
    public string? CsvText { get; set; }

    public IReadOnlyList<(int Id, string Name)> AvailableTeams { get; private set; } = [];
    public int? ResolvedTeamId { get; private set; }
    public VeImportPreview? Preview { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostPreviewAsync(IFormFile? file)
    {
        await LoadAsync();
        if (ResolvedTeamId is not { } teamId)
        {
            return Forbid();
        }

        if (file is null || file.Length == 0)
        {
            TempData["ErrorMessage"] = "Choose a CSV file first.";
            return RedirectToPage(new { teamId = TeamId });
        }

        if (file.Length > MaxUploadBytes)
        {
            TempData["ErrorMessage"] = $"That file is larger than {MaxUploadBytes / 1024} KB.";
            return RedirectToPage(new { teamId = TeamId });
        }

        using var reader = new StreamReader(file.OpenReadStream());
        CsvText = await reader.ReadToEndAsync(HttpContext.RequestAborted);

        Preview = await importService.ParseAsync(CsvText, teamId, HttpContext.RequestAborted);
        return Page();
    }

    public async Task<IActionResult> OnPostApplyAsync()
    {
        await LoadAsync();
        if (ResolvedTeamId is not { } teamId)
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(CsvText))
        {
            TempData["ErrorMessage"] = "Nothing to import — upload a file first.";
            return RedirectToPage(new { teamId = TeamId });
        }

        var user = await userManager.GetUserWithManagerAsync(dbContext, User)
            ?? throw new InvalidOperationException("No authenticated user for an [Authorize]d page.");

        var result = await importService.ApplyAsync(CsvText, teamId, user.Id, HttpContext.RequestAborted);

        if (result.Error is not null)
        {
            TempData["ErrorMessage"] = result.Error;
            return RedirectToPage(new { teamId = TeamId });
        }

        TempData["StatusMessage"] =
            $"Imported {result.Created} new VE(s), updated {result.Updated}, added {result.AddedToTeam} existing VE(s) to this team" +
            (result.Skipped > 0 ? $", skipped {result.Skipped} row(s) with problems." : ".");

        return RedirectToPage("/SessionManager/VeDirectory", new { teamId = TeamId });
    }

    private async Task LoadAsync()
    {
        var user = await userManager.GetUserWithManagerAsync(dbContext, User)
            ?? throw new InvalidOperationException("No authenticated user for an [Authorize]d page.");

        AvailableTeams = await accessScope.GetAvailableTeamsAsync(dbContext, user);
        ResolvedTeamId = adminAccessScope.TryResolveManageableTeamId(user, TeamId, [.. AvailableTeams.Select(t => t.Id)]);
    }
}
