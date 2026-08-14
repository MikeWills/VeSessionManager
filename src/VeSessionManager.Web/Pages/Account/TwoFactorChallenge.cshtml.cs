using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Web.Pages.Account;

/// <summary>
/// The second step: a six-digit code, or a recovery code (#356).
///
/// <para><b>[AllowAnonymous], and that is correct.</b> Nobody is signed in at this point — the whole
/// design is that no app cookie exists until the challenge is passed. What stands in for
/// authentication is the short-lived TwoFactorUserId cookie written by the password step; without
/// one, this page has no user and redirects back to Login. Reaching it directly grants nothing.</para>
///
/// <para><b>Failures are counted against Identity's lockout</b>, exactly like a wrong password.
/// Otherwise the code becomes an unlimited guessing surface for whoever already holds the password —
/// six digits is a million combinations, which is not many when nothing is counting. The per-IP
/// limiter on /Account bounds the rate on top.</para>
/// </summary>
[AllowAnonymous]
public class TwoFactorChallengeModel(
    SignInManager<User> signInManager,
    UserManager<User> userManager,
    AppDbContext dbContext,
    TimeProvider timeProvider,
    ILogger<TwoFactorChallengeModel> logger) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    public class InputModel
    {
        [Required]
        [Display(Name = "Authentication code")]
        public string Code { get; set; } = "";

        [Display(Name = "Trust this device for 30 days")]
        public bool RememberDevice { get; set; }
    }

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    /// <summary>Round-trips the login page's checkbox — the app cookie is written here, so the
    /// choice made there has to survive to this request or "keep me signed in" silently stops
    /// working for anyone with 2FA on.</summary>
    [BindProperty(SupportsGet = true)]
    public bool RememberMe { get; set; }

    /// <summary>Swaps the input's label and help text; the handler accepts either kind regardless.</summary>
    [BindProperty(SupportsGet = true)]
    public bool UseRecoveryCode { get; set; }

    public string? ErrorMessage { get; private set; }

    public static string RememberDeviceDurationLabel => Web.RememberMe.DurationLabel;

    public async Task<IActionResult> OnGetAsync() =>
        await signInManager.GetTwoFactorAuthenticationUserAsync() is null
            ? RedirectToPage("./Login")
            : Page();

    public async Task<IActionResult> OnPostAsync()
    {
        var user = await signInManager.GetTwoFactorAuthenticationUserAsync();
        if (user is null)
        {
            // The pending cookie expired, or someone arrived here cold. Back to the start rather
            // than an error — the password step has to happen again either way.
            return RedirectToPage("./Login");
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        // The two kinds of code are normalized DIFFERENTLY, and conflating them is a real bug this
        // caught: a TOTP code is six digits, so stripping spaces and hyphens only ever helps, while
        // an Identity recovery code CONTAINS a hyphen — stripping it makes redemption fail on a code
        // the user copied correctly. So the authenticator attempt is stripped and the recovery
        // attempt is taken as typed, trimmed.
        var authenticatorCode = TwoFactorSetup.NormalizeCode(Input.Code);
        var recoveryCode = (Input.Code ?? string.Empty).Trim();

        // A recovery code is tried only when the authenticator code fails, so a user who mistypes
        // their six digits does not silently burn a recovery code on the way past. Redemption is
        // one-way: Identity deletes the code as it accepts it.
        var authenticatorAccepted = await userManager.VerifyTwoFactorTokenAsync(
            user, userManager.Options.Tokens.AuthenticatorTokenProvider, authenticatorCode);

        var usedRecoveryCode = false;
        if (!authenticatorAccepted)
        {
            var redemption = await userManager.RedeemTwoFactorRecoveryCodeAsync(user, recoveryCode);
            usedRecoveryCode = redemption.Succeeded;
        }

        if (!authenticatorAccepted && !usedRecoveryCode)
        {
            // Counted against lockout, like a wrong password — see the class remarks.
            await userManager.AccessFailedAsync(user);

            await AuditAsync(user.Id, "TwoFactorFailed", "Incorrect two-factor code.");
            ErrorMessage = "That code is not valid. Check your authenticator app and try again.";
            return Page();
        }

        await userManager.ResetAccessFailedCountAsync(user);

        // Only the authenticator earns device trust. A recovery code is what someone uses when they
        // have LOST the authenticator, so trusting the device on the strength of one would mean a
        // stolen recovery code buys 30 days of unchallenged access.
        if (Input.RememberDevice && !usedRecoveryCode)
        {
            await signInManager.RememberTwoFactorClientAsync(user);
        }

        // The pending cookie goes before the app cookie is written, so a failure between the two
        // cannot leave a resumable half-sign-in behind.
        await TwoFactorSignIn.EndAsync(HttpContext);

        // Same one-Set-Cookie discipline as the password path (#340): the lifetime is set explicitly
        // here rather than left to isPersistent, and this is the only place the app cookie is issued
        // on this journey.
        await signInManager.SignInAsync(
            user,
            RememberMe
                ? Web.RememberMe.Properties(timeProvider.GetUtcNow())
                : new AuthenticationProperties { IsPersistent = false });

        var remaining = await userManager.CountRecoveryCodesAsync(user);
        await AuditAsync(user.Id, "SignedIn",
            usedRecoveryCode
                ? $"Signed in with a recovery code ({remaining} remaining)."
                : "Signed in with two-factor authentication.");

        if (usedRecoveryCode)
        {
            // Said plainly, because the count is the thing that matters and nobody checks a page
            // they were not sent to. Running out means the lost-phone escape hatch is gone.
            TempData["StatusMessage"] = remaining == 0
                ? "You used your last recovery code. Set up your authenticator again to generate more."
                : $"You signed in with a recovery code. {remaining} remaining.";
        }

        if (!string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl))
        {
            return LocalRedirect(ReturnUrl);
        }

        return RedirectToPage(RoleLandingPages.GetPath(user.Role));
    }

    /// <summary>
    /// Mirrors Login's own helper: an audit failure must never become a sign-in failure, because Web
    /// and Worker share one SQLite file and a transient lock is real.
    /// </summary>
    private async Task AuditAsync(int userId, string action, string details)
    {
        try
        {
            dbContext.AddAuditLog(userId, action, nameof(User), userId, details,
                timeProvider.GetUtcNow().UtcDateTime, SourceIp.For(HttpContext));
            await dbContext.SaveChangesAsync(HttpContext.RequestAborted);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to write the {Action} audit entry — the sign-in itself was unaffected", action);
        }
    }
}
