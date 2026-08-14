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
/// TeamAdmin only sees VECs any of their own team(s) actually have sessions under (a TeamAdmin has
/// no business editing fee schedules for a VEC none of their teams have ever worked with) — a
/// multi-team TeamAdmin (issue #19) sees the union across every team they belong to, since this
/// page's unit of selection is the VEC, not a single team.
/// </summary>
// SystemAdmin only (2026-08-06): a fee configuration belongs to a VEC, which is shared reference data
// across every team, so one team's admin editing it would change what every other team charges. The
// per-team checks further down are left in place as a second line rather than removed.
[Authorize(Roles = RoleGroups.SystemAdminOnly)]
public class FeeConfigurationsModel(AppDbContext dbContext, UserManager<User> userManager, SessionAccessScope accessScope, FeeConfigurationService feeConfigurationService) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public int? VecId { get; set; }

    public IReadOnlyList<(int Id, string Name)> AvailableVecs { get; private set; } = [];
    public IReadOnlyList<FeeConfigRow> FeeConfigurations { get; private set; } = [];

    /// <summary>
    /// Returns IActionResult so the no-user case can Forbid (L-14). It used to return Task and bare-
    /// return, which rendered a fully authenticated-looking page with every list empty — the reader
    /// cannot tell "you may not see this" from "there is nothing here", and every other page in the
    /// app answers this with Forbid().
    ///
    /// <para><b>Deliberately untested, because the branch is unreachable.</b> Two things already
    /// intercept every route to it: the app-wide FallbackPolicy redirects an anonymous request
    /// before the handler runs, and StaleAuthCookieFilter handles the cookie-names-a-deleted-user
    /// case. A test was written against both and passed with the fix reverted, so it was removed
    /// rather than kept as false comfort. This is consistency and defence in depth — if either of
    /// those guards is ever narrowed, this page fails closed like the rest.</para>
    /// </summary>
    public async Task<IActionResult> OnGetAsync()
    {
        var user = await userManager.GetUserWithManagerAsync(dbContext, User);
        if (user is null)
        {
            return Forbid();
        }

        if (user.Role == VeSessionManager.Core.Entities.UserRole.SystemAdmin)
        {
            AvailableVecs = await dbContext.Vecs.OrderBy(v => v.Name).Select(v => new ValueTuple<int, string>(v.Id, v.Name)).ToListAsync(HttpContext.RequestAborted);
        }
        else
        {
            var teamIds = accessScope.GetEffectiveTeamIds(user) ?? [];
            AvailableVecs = await dbContext.Sessions
                .Where(s => teamIds.Contains(s.TeamId))
                .Select(s => s.Vec)
                .Distinct()
                .OrderBy(v => v.Name)
                .Select(v => new ValueTuple<int, string>(v.Id, v.Name))
                .ToListAsync(HttpContext.RequestAborted);
        }

        if (VecId is null && AvailableVecs.Count > 0)
        {
            VecId = AvailableVecs[0].Id;
        }

        // Page(), not Forbid(): "no VEC chosen yet, or the chosen one is not yours to see" is an
        // empty state rather than a refusal, and the picker above it is how you get out of it.
        if (VecId is null || !AvailableVecs.Any(v => v.Id == VecId))
        {
            return Page();
        }

        // "Is this fee configuration in use?" is a correlated EXISTS, not a list scan. This used to
        // pull every session's FeeConfigurationId in the deployment into memory — a row per session
        // ever run, growing forever — purely to test membership for the handful of rows on screen.
        FeeConfigurations = await dbContext.FeeConfigurations
            .Where(f => f.VecId == VecId)
            .OrderByDescending(f => f.EffectiveDate)
            .Select(f => new FeeConfigRow(f.Id, f.EffectiveDate, f.FeeCollectionEnabled, f.ExamFeeAmount, f.RetainedAmount, f.YouthExamFeeAmount, f.Notes,
                dbContext.Sessions.Any(s => s.FeeConfigurationId == f.Id)))
            .ToListAsync(HttpContext.RequestAborted);

        return Page();
    }

    public async Task<IActionResult> OnPostCreateAsync(int vecId, DateTime effectiveDate, bool feeCollectionEnabled, decimal? examFeeAmount, decimal? retainedAmount, decimal? youthExamFeeAmount, string? notes)
    {
        var user = await userManager.GetUserWithManagerAsync(dbContext, User);
        if (user is null || !await IsVecAllowedAsync(user, vecId))
        {
            return Forbid();
        }

        var (result, _) = await feeConfigurationService.CreateAsync(vecId, effectiveDate, feeCollectionEnabled, examFeeAmount, retainedAmount, youthExamFeeAmount, notes, user.Id, CancellationToken.None);
        TempData[result == FeeConfigActionResult.Success ? "StatusMessage" : "ErrorMessage"] =
            result == FeeConfigActionResult.Success ? "Fee configuration created." : "Could not create fee configuration — VEC not found.";
        return RedirectToPage(new { vecId });
    }

    public async Task<IActionResult> OnPostUpdateAsync(int feeConfigurationId, DateTime effectiveDate, bool feeCollectionEnabled, decimal? examFeeAmount, decimal? retainedAmount, decimal? youthExamFeeAmount, string? notes)
    {
        var user = await userManager.GetUserWithManagerAsync(dbContext, User);
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

        var result = await feeConfigurationService.UpdateAsync(feeConfigurationId, effectiveDate, feeCollectionEnabled, examFeeAmount, retainedAmount, youthExamFeeAmount, notes, user.Id, CancellationToken.None);
        TempData[result == FeeConfigActionResult.Success ? "StatusMessage" : "ErrorMessage"] = result switch
        {
            FeeConfigActionResult.Success => "Fee configuration updated.",
            FeeConfigActionResult.InUse => "Cannot edit — one or more sessions already use this fee configuration. Create a new dated row instead.",
            _ => "Fee configuration not found."
        };
        return RedirectToPage(new { vecId = existingFeeConfig.VecId });
    }

    /// <summary>Mirrors OnGetAsync's AvailableVecs scoping: SystemAdmin may act on any Vec; TeamAdmin only on VECs any of their own team(s) actually have sessions under.</summary>
    private async Task<bool> IsVecAllowedAsync(User user, int vecId)
    {
        if (user.Role == UserRole.SystemAdmin)
        {
            return await dbContext.Vecs.AnyAsync(v => v.Id == vecId);
        }

        var teamIds = accessScope.GetEffectiveTeamIds(user) ?? [];
        return await dbContext.Sessions.AnyAsync(s => teamIds.Contains(s.TeamId) && s.VecId == vecId);
    }

    public record FeeConfigRow(int Id, DateTime EffectiveDate, bool FeeCollectionEnabled, decimal? ExamFeeAmount, decimal? RetainedAmount, decimal? YouthExamFeeAmount, string? Notes, bool InUse);
}
