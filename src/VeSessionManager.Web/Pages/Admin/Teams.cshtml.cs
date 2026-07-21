using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Web.Pages.Admin;

/// <summary>Phase 9c: Team list + create — SystemAdmin only. Credential editing lives on TeamSettings, not here.</summary>
[Authorize(Roles = "SystemAdmin")]
public class TeamsModel(AppDbContext dbContext, UserManager<User> userManager, TeamSettingsService teamSettingsService) : PageModel
{
    public IReadOnlyList<TeamRow> Teams { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Teams = await dbContext.Teams
            .OrderBy(t => t.Name)
            .Select(t => new TeamRow(t.Id, t.Name, t.CreatedUtc))
            .ToListAsync();
    }

    public async Task<IActionResult> OnPostCreateAsync(string name)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return Forbid();
        }

        var (result, _) = await teamSettingsService.CreateAsync(name, user.Id, CancellationToken.None);
        TempData[result == TeamActionResult.Success ? "StatusMessage" : "ErrorMessage"] =
            result == TeamActionResult.Success ? $"Team '{name}' created." : $"A team named '{name}' already exists.";
        return RedirectToPage();
    }

    public record TeamRow(int Id, string Name, DateTime CreatedUtc);
}
