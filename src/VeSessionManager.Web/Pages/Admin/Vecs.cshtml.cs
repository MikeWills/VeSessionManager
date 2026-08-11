using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core;
using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Web.Pages.Admin;

/// <summary>Phase 9c: Vec (shared/global VEC reference data) management — SystemAdmin only.</summary>
[Authorize(Roles = "SystemAdmin")]
public class VecsModel(AppDbContext dbContext, UserManager<User> userManager, VecManagementService vecManagementService, TimeProvider timeProvider) : PageModel
{
    public IReadOnlyList<VecRow> Vecs { get; private set; } = [];

    public async Task OnGetAsync()
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;

        // A Vec with no FeeConfiguration in effect makes SessionIngestionService skip every one of
        // its sessions, with only a log warning to show for it — so the state is surfaced here
        // rather than left for someone to notice in the Worker log. Whole table loaded and grouped
        // in memory: it is a handful of rows per VEC, and picking "newest effective on or before
        // now" per VEC is the same rule ingestion itself applies.
        var effectiveFees = (await dbContext.FeeConfigurations
                .Where(f => f.EffectiveDate <= now)
                .Select(f => new { f.VecId, f.EffectiveDate, f.FeeCollectionEnabled, f.ExamFeeAmount })
                .ToListAsync())
            .GroupBy(f => f.VecId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(f => f.EffectiveDate).First());

        Vecs = (await dbContext.Vecs
                .OrderBy(v => v.Name)
                .Select(v => new { v.Id, v.Name, v.ExamToolsCode, v.SupportsYouthProgram, v.Notes })
                .ToListAsync())
            .Select(v =>
            {
                effectiveFees.TryGetValue(v.Id, out var fee);
                var feeSummary = fee is null
                    ? null
                    : fee.FeeCollectionEnabled
                        ? Usd.Format(fee.ExamFeeAmount ?? 0m)
                        : "Collection off";
                return new VecRow(v.Id, v.Name, v.ExamToolsCode, v.SupportsYouthProgram, v.Notes, feeSummary);
            })
            .ToList();
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
            VecActionResult.DuplicateName => $"A VEC named '{name}' already exists.",
            // NotFound is unreachable on the create path; naming it beats a catch-all that would
            // report "already exists" for something else entirely.
            _ => $"Could not create VEC '{name}'."
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

    /// <summary>FeeSummary is null when this VEC has no fee configuration in effect — the state that silently blocks ingestion.</summary>
    public record VecRow(int Id, string Name, string? ExamToolsCode, bool SupportsYouthProgram, string? Notes, string? FeeSummary)
    {
        /// <summary>
        /// The code ingestion matches ExamTools' session <c>vec</c> field against — the override when
        /// there is one, the name otherwise. Mirrors <see cref="Vec.MatchCode"/>, which cannot be
        /// reached from here because the query projects to an anonymous type rather than materialising
        /// whole <see cref="Vec"/> entities. Keep the two definitions identical.
        /// </summary>
        public string MatchCode => ExamToolsCode ?? Name;
    }
}
