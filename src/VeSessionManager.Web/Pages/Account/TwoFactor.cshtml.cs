using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Web.Pages.Account;

/// <summary>
/// Two-factor authentication for your own account (#356): turn it on, turn it off, replace your
/// recovery codes.
///
/// <para><b>Opt-in for everyone, required of nobody</b> — including SystemAdmins, who are instead
/// nudged (see <c>_TwoFactorSuggestionBanner</c>). Enforcement was considered and deliberately not
/// built: this deployment's system SMTP has historically not been configured, so an admin who loses
/// a phone cannot necessarily be emailed a way back in, and the account that would rescue them is
/// the one that would be locked. The nudge is loud; the door stays open.</para>
///
/// <para>Enabling and disabling both rewrite the security stamp, which signs out every other device.
/// That is the correct behavior rather than a side effect: changing whether an account needs a
/// second factor is exactly when sessions issued under the old rule should stop counting.</para>
/// </summary>
public class TwoFactorModel(
    UserManager<User> userManager,
    SignInManager<User> signInManager,
    AppDbContext dbContext,
    TimeProvider timeProvider) : PageModel
{
    public bool IsEnabled { get; private set; }
    public int RecoveryCodesRemaining { get; private set; }
    public bool IsDeviceRemembered { get; private set; }
    public UserRole Role { get; private set; }

    /// <summary>Shown once, immediately after generation, and never retrievable again — Identity
    /// stores only hashes.</summary>
    public IReadOnlyList<string> NewRecoveryCodes { get; private set; } = [];

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public class InputModel
    {
        [Display(Name = "Verification code")]
        public string? Code { get; set; }
    }

    public string? ErrorMessage { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return Forbid();
        }

        await LoadAsync(user);

        // Survives exactly one redirect, which is what "shown once" means here.
        if (TempData["NewRecoveryCodes"] is string joined && joined.Length > 0)
        {
            NewRecoveryCodes = joined.Split('\n');
        }

        return Page();
    }

    public async Task<IActionResult> OnPostDisableAsync()
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return Forbid();
        }

        if (!user.TwoFactorEnabled)
        {
            return RedirectToPage();
        }

        await userManager.SetTwoFactorEnabledAsync(user, false);

        // The key goes too. Leaving it would mean re-enabling silently reuses a secret that has been
        // sitting unused in the database, and any authenticator app still holding it would keep
        // working — which is not what "I turned this off" means.
        await userManager.ResetAuthenticatorKeyAsync(user);
        await signInManager.ForgetTwoFactorClientAsync();
        await userManager.UpdateSecurityStampAsync(user);

        await AuditAsync(user.Id, "TwoFactorDisabled", "Two-factor authentication turned off.");

        TempData["StatusMessage"] = "Two-factor authentication is off. Every other device has been signed out.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRegenerateRecoveryCodesAsync()
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return Forbid();
        }

        if (!user.TwoFactorEnabled)
        {
            // Recovery codes without a second factor to recover from would be a second password.
            return RedirectToPage();
        }

        var codes = await userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, TwoFactorSetup.RecoveryCodeCount);
        TempData["NewRecoveryCodes"] = string.Join('\n', codes ?? []);

        await AuditAsync(user.Id, "TwoFactorRecoveryCodesRegenerated",
            $"Generated {TwoFactorSetup.RecoveryCodeCount} new recovery codes; any previous codes no longer work.");

        TempData["StatusMessage"] = "New recovery codes generated. Your previous codes no longer work.";
        return RedirectToPage();
    }

    private async Task LoadAsync(User user)
    {
        IsEnabled = user.TwoFactorEnabled;
        Role = user.Role;
        RecoveryCodesRemaining = await userManager.CountRecoveryCodesAsync(user);
        IsDeviceRemembered = await signInManager.IsTwoFactorClientRememberedAsync(user);
    }

    private async Task AuditAsync(int userId, string action, string details)
    {
        dbContext.AddAuditLog(userId, action, nameof(User), userId, details,
            timeProvider.GetUtcNow().UtcDateTime, SourceIp.For(HttpContext));
        await dbContext.SaveChangesAsync(HttpContext.RequestAborted);
    }
}
