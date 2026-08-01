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
            .Select(v => new VecRow(v.Id, v.Name, v.ExamToolsCode, v.SupportsYouthProgram, v.Notes))
            .ToListAsync();
    }

    public async Task<IActionResult> OnPostCreateAsync(string name, string? examToolsCode, bool supportsYouthProgram, string? notes)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return Forbid();
        }

        var (result, _) = await vecManagementService.CreateAsync(name, examToolsCode, supportsYouthProgram, notes, user.Id, CancellationToken.None);
        TempData[result == VecActionResult.Success ? "StatusMessage" : "ErrorMessage"] = result switch
        {
            VecActionResult.Success => $"VEC '{name}' created.",
            VecActionResult.DuplicateExamToolsCode => DuplicateCodeMessage(examToolsCode ?? name),
            _ => $"A VEC named '{name}' already exists."
        };
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostUpdateAsync(int vecId, string name, string? examToolsCode, bool supportsYouthProgram, string? notes)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return Forbid();
        }

        var result = await vecManagementService.UpdateAsync(vecId, name, examToolsCode, supportsYouthProgram, notes, user.Id, CancellationToken.None);
        TempData[result == VecActionResult.Success ? "StatusMessage" : "ErrorMessage"] = result switch
        {
            VecActionResult.Success => $"VEC '{name}' updated.",
            VecActionResult.DuplicateName => $"A VEC named '{name}' already exists.",
            VecActionResult.DuplicateExamToolsCode => DuplicateCodeMessage(examToolsCode ?? name),
            _ => "VEC not found."
        };
        return RedirectToPage();
    }

    private static string DuplicateCodeMessage(string code) =>
        $"Another VEC already matches the ExamTools code '{code}' — ingestion could not tell them apart.";

    public record VecRow(int Id, string Name, string? ExamToolsCode, bool SupportsYouthProgram, string? Notes);
}
