using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Web.Pages.Admin;

/// <summary>Phase 9c: Vec (shared/global VEC reference data) management — SystemAdmin only.</summary>
[Authorize(Roles = "SystemAdmin")]
public class VecsModel(AppDbContext dbContext, UserManager<User> userManager, VecManagementService vecManagementService) : PageModel
{
    public IReadOnlyList<VecRow> Vecs { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Vecs = await dbContext.Vecs
            .OrderBy(v => v.Name)
            .Select(v => new VecRow(v.Id, v.Name, v.SupportsYouthProgram, v.Notes))
            .ToListAsync();
    }

    public async Task<IActionResult> OnPostCreateAsync(string name, bool supportsYouthProgram, string? notes)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return Forbid();
        }

        var (result, _) = await vecManagementService.CreateAsync(name, supportsYouthProgram, notes, user.Id, CancellationToken.None);
        TempData[result == VecActionResult.Success ? "StatusMessage" : "ErrorMessage"] =
            result == VecActionResult.Success ? $"VEC '{name}' created." : $"A VEC named '{name}' already exists.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostUpdateAsync(int vecId, string name, bool supportsYouthProgram, string? notes)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return Forbid();
        }

        var result = await vecManagementService.UpdateAsync(vecId, name, supportsYouthProgram, notes, user.Id, CancellationToken.None);
        TempData[result == VecActionResult.Success ? "StatusMessage" : "ErrorMessage"] = result switch
        {
            VecActionResult.Success => $"VEC '{name}' updated.",
            VecActionResult.DuplicateName => $"A VEC named '{name}' already exists.",
            _ => "VEC not found."
        };
        return RedirectToPage();
    }

    public record VecRow(int Id, string Name, bool SupportsYouthProgram, string? Notes);
}
