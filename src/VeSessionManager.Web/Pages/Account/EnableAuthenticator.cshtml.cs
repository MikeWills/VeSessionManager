using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Web.Pages.Account;

/// <summary>
/// Enrolment: scan (or type) the key, prove it works, save the recovery codes (#356).
///
/// <para><b>Two-factor is not switched on until a code verifies.</b> Enabling first and verifying
/// later is the version of this that locks people out — a mistyped secret, a phone with the wrong
/// clock, a QR that never scanned, and the account now demands a code nobody can produce. The key is
/// generated and shown, and only a working code flips the flag.</para>
///
/// <para><b>Recovery codes are generated in the same action</b>, not offered as a later step, for the
/// same reason: the window between "2FA is on" and "I have a way back in" should not exist.</para>
/// </summary>
public class EnableAuthenticatorModel(
    UserManager<User> userManager,
    AppDbContext dbContext,
    TimeProvider timeProvider) : PageModel
{
    /// <summary>Grouped into fours for typing; the QR carries the same secret unformatted.</summary>
    public string DisplayKey { get; private set; } = "";

    public string AuthenticatorUri { get; private set; } = "";

    /// <summary>Inline SVG. No CSP exception needed, and it stays sharp when someone holds a phone
    /// up to a laptop screen.</summary>
    public string QrCodeSvg { get; private set; } = "";

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public class InputModel
    {
        [Required]
        [Display(Name = "Verification code")]
        public string Code { get; set; } = "";
    }

    public string? ErrorMessage { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return Forbid();
        }

        if (user.TwoFactorEnabled)
        {
            // Already on. Re-enrolling means turning it off first, so the "disable resets the key"
            // rule stays the only way a secret is replaced.
            return RedirectToPage("./TwoFactor");
        }

        await LoadAsync(user);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return Forbid();
        }

        if (user.TwoFactorEnabled)
        {
            return RedirectToPage("./TwoFactor");
        }

        if (!ModelState.IsValid)
        {
            await LoadAsync(user);
            return Page();
        }

        var code = TwoFactorSetup.NormalizeCode(Input.Code);
        var verified = await userManager.VerifyTwoFactorTokenAsync(
            user, userManager.Options.Tokens.AuthenticatorTokenProvider, code);

        if (!verified)
        {
            // The clock hint is not padding. A phone whose time has drifted is the single most common
            // cause of this, and it is not something anyone guesses on their own.
            ErrorMessage = "That code is not valid. Check that you entered the key correctly, " +
                "and that your phone's clock is set automatically.";
            await LoadAsync(user);
            return Page();
        }

        await userManager.SetTwoFactorEnabledAsync(user, true);

        var codes = await userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, TwoFactorSetup.RecoveryCodeCount);
        TempData["NewRecoveryCodes"] = string.Join('\n', codes ?? []);

        dbContext.AddAuditLog(user.Id, "TwoFactorEnabled", nameof(User), user.Id,
            $"Two-factor authentication turned on; {TwoFactorSetup.RecoveryCodeCount} recovery codes issued.",
            timeProvider.GetUtcNow().UtcDateTime, SourceIp.For(HttpContext));
        await dbContext.SaveChangesAsync(HttpContext.RequestAborted);

        TempData["StatusMessage"] = "Two-factor authentication is on. Save your recovery codes now — they are shown only once.";
        return RedirectToPage("./TwoFactor");
    }

    private async Task LoadAsync(User user)
    {
        var key = await TwoFactorSetup.GetOrCreateKeyAsync(userManager, user);
        DisplayKey = TwoFactorSetup.FormatKeyForDisplay(key);
        AuthenticatorUri = TwoFactorSetup.BuildAuthenticatorUri(user.Email ?? user.UserName ?? "account", key);
        QrCodeSvg = TwoFactorSetup.BuildQrCodeSvg(AuthenticatorUri);
    }
}
