using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Web.Pages.Admin;

/// <summary>
/// A manual escape hatch for a real-world FCC-wide processing problem (2026-08-26) — the FCC itself,
/// or the VEC's own submission to it, can stall for weeks or months (a shutdown, a payment-system
/// outage), and the app has no way to tell that apart from an individual candidate simply not having
/// paid yet. <c>VeSessionManager.Core.Messaging.MessageDispatchService</c> is where the switches
/// actually take effect; this page only sets them.
///
/// <para><b>Deployment-wide, not per-team</b> — reachable by TeamAdmin as well as SystemAdmin
/// (<see cref="RoleGroups.Admins"/>), unlike the rest of <c>SystemSettings</c>. An FCC outage is the
/// same fact for every team on this deployment, and either role may be the one who first notices it.</para>
///
/// <para><b>Only <c>NewLicense</c> and <c>Upgrade</c> do anything.</b> The Renewal switch is stored and
/// shown, but nothing reads it — this app has no renewal-candidate concept at all, so there is no
/// population it could ever suppress. See <see cref="SystemSettings.FccIssueSuppressRenewalReminders"/>.</para>
/// </summary>
[Authorize(Roles = RoleGroups.Admins)]
public class FccStatusModel(UserManager<User> userManager, SystemSettingsService systemSettingsService) : PageModel
{
    public bool FccIssueActive { get; private set; }
    public bool SuppressNewLicenseReminders { get; private set; }
    public bool SuppressUpgradeReminders { get; private set; }
    public bool SuppressRenewalReminders { get; private set; }
    public DateTime? UpdatedUtc { get; private set; }

    public async Task OnGetAsync()
    {
        var settings = await systemSettingsService.GetAsync(HttpContext.RequestAborted);
        FccIssueActive = settings.FccIssueActive;
        SuppressNewLicenseReminders = settings.FccIssueSuppressNewLicenseReminders;
        SuppressUpgradeReminders = settings.FccIssueSuppressUpgradeReminders;
        SuppressRenewalReminders = settings.FccIssueSuppressRenewalReminders;
        UpdatedUtc = settings.UpdatedUtc;
    }

    public async Task<IActionResult> OnPostAsync(
        bool fccIssueActive, bool suppressNewLicenseReminders, bool suppressUpgradeReminders, bool suppressRenewalReminders)
    {
        // Role re-checked here, not just by [Authorize] — same reasoning as every other Admin POST
        // handler in this app (#257): the role in the cookie is a claim baked in at sign-in, the row
        // is the truth, and revalidation is only every 30 minutes by default.
        var user = await userManager.GetUserAsync(User);
        if (user is null || user.Role is not (UserRole.SystemAdmin or UserRole.TeamAdmin))
        {
            return Forbid();
        }

        await systemSettingsService.UpdateFccIssueAsync(
            fccIssueActive, suppressNewLicenseReminders, suppressUpgradeReminders, suppressRenewalReminders,
            user.Id, HttpContext.RequestAborted);

        TempData["StatusMessage"] = fccIssueActive
            ? "FCC issue flagged — suppression is active for the populations checked below."
            : "FCC issue cleared. Reminders will resume normally.";

        return RedirectToPage();
    }
}
