using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Authorization;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Web.Pages.Admin;

/// <summary>
/// Phase 9c: FeeConfiguration CRUD, scoped by Vec. SystemAdmin sees every Vec in the picker;
/// TeamAdmin only sees VECs their own team actually has sessions under (a TeamAdmin has no
/// business editing fee schedules for a VEC their team has never worked with).
/// </summary>
[Authorize(Roles = "SystemAdmin,TeamAdmin")]
public class FeeConfigurationsModel(AppDbContext dbContext, UserManager<User> userManager, SessionAccessScope accessScope, FeeConfigurationService feeConfigurationService) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public int? VecId { get; set; }

    public IReadOnlyList<(int Id, string Name)> AvailableVecs { get; private set; } = [];
    public IReadOnlyList<FeeConfigRow> FeeConfigurations { get; private set; } = [];

    public async Task OnGetAsync()
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return;
        }

        if (user.Role == VeSessionManager.Core.Entities.UserRole.SystemAdmin)
        {
            AvailableVecs = await dbContext.Vecs.OrderBy(v => v.Name).Select(v => new ValueTuple<int, string>(v.Id, v.Name)).ToListAsync();
        }
        else
        {
            var teamId = accessScope.GetEffectiveTeamId(user);
            AvailableVecs = await dbContext.Sessions
                .Where(s => s.TeamId == teamId)
                .Select(s => s.Vec)
                .Distinct()
                .OrderBy(v => v.Name)
                .Select(v => new ValueTuple<int, string>(v.Id, v.Name))
                .ToListAsync();
        }

        if (VecId is null && AvailableVecs.Count > 0)
        {
            VecId = AvailableVecs[0].Id;
        }

        if (VecId is null || !AvailableVecs.Any(v => v.Id == VecId))
        {
            return;
        }

        var referencedIds = await dbContext.Sessions.Select(s => s.FeeConfigurationId).ToListAsync();
        FeeConfigurations = await dbContext.FeeConfigurations
            .Where(f => f.VecId == VecId)
            .OrderByDescending(f => f.EffectiveDate)
            .Select(f => new FeeConfigRow(f.Id, f.EffectiveDate, f.FeeCollectionEnabled, f.ExamFeeAmount, f.RetainedAmount, f.Notes, referencedIds.Contains(f.Id)))
            .ToListAsync();
    }

    public async Task<IActionResult> OnPostCreateAsync(int vecId, DateTime effectiveDate, bool feeCollectionEnabled, decimal? examFeeAmount, decimal? retainedAmount, string? notes)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null || !await IsVecAllowedAsync(user, vecId))
        {
            return Forbid();
        }

        var (result, _) = await feeConfigurationService.CreateAsync(vecId, effectiveDate, feeCollectionEnabled, examFeeAmount, retainedAmount, notes, user.Id, CancellationToken.None);
        TempData[result == FeeConfigActionResult.Success ? "StatusMessage" : "ErrorMessage"] =
            result == FeeConfigActionResult.Success ? "Fee configuration created." : "Could not create fee configuration — VEC not found.";
        return RedirectToPage(new { vecId });
    }

    public async Task<IActionResult> OnPostUpdateAsync(int feeConfigurationId, DateTime effectiveDate, bool feeCollectionEnabled, decimal? examFeeAmount, decimal? retainedAmount, string? notes)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return Forbid();
        }

        // Authorize against the fee configuration's actual VecId, not a client-supplied one —
        // otherwise a TeamAdmin could submit a VEC their team legitimately works with alongside a
        // feeConfigurationId belonging to a different VEC and edit that VEC's fee schedule
        // (cross-tenant IDOR, same class of bug as EmailTemplates' OnPostUpdateAsync).
        var existingFeeConfig = await dbContext.FeeConfigurations.FirstOrDefaultAsync(f => f.Id == feeConfigurationId);
        if (existingFeeConfig is null || !await IsVecAllowedAsync(user, existingFeeConfig.VecId))
        {
            return Forbid();
        }

        var result = await feeConfigurationService.UpdateAsync(feeConfigurationId, effectiveDate, feeCollectionEnabled, examFeeAmount, retainedAmount, notes, user.Id, CancellationToken.None);
        TempData[result == FeeConfigActionResult.Success ? "StatusMessage" : "ErrorMessage"] = result switch
        {
            FeeConfigActionResult.Success => "Fee configuration updated.",
            FeeConfigActionResult.InUse => "Cannot edit — one or more sessions already use this fee configuration. Create a new dated row instead.",
            _ => "Fee configuration not found."
        };
        return RedirectToPage(new { vecId = existingFeeConfig.VecId });
    }

    /// <summary>Mirrors OnGetAsync's AvailableVecs scoping: SystemAdmin may act on any Vec; TeamAdmin only on VECs their own team actually has sessions under.</summary>
    private async Task<bool> IsVecAllowedAsync(User user, int vecId)
    {
        if (user.Role == UserRole.SystemAdmin)
        {
            return await dbContext.Vecs.AnyAsync(v => v.Id == vecId);
        }

        var teamId = accessScope.GetEffectiveTeamId(user);
        return await dbContext.Sessions.AnyAsync(s => s.TeamId == teamId && s.VecId == vecId);
    }

    public record FeeConfigRow(int Id, DateTime EffectiveDate, bool FeeCollectionEnabled, decimal? ExamFeeAmount, decimal? RetainedAmount, string? Notes, bool InUse);
}
