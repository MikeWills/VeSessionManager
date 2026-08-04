using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using VeSessionManager.Core;
using VeSessionManager.Core.Authorization;

namespace VeSessionManager.Web.Pages.Account;

/// <summary>
/// "Forgot password" entry point for local (email + password) accounts, added 2026-08-01 — before
/// it, a forgotten password meant permanent lockout with hand-editing AspNetUsers as the only
/// recovery. OAuth users don't need this; their provider owns the credential.
///
/// [AllowAnonymous] is load-bearing: this page is reached precisely when the user cannot sign in.
/// </summary>
[AllowAnonymous]
public class ForgotPasswordModel(PasswordResetService passwordResetService, IOptions<AppOptions> appOptions) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    /// <summary>Only ever set for a deployment misconfiguration, never for anything about an account — see PasswordResetService.RequestResetAsync.</summary>
    public string? ErrorMessage { get; private set; }

    public class InputModel
    {
        [Required]
        [EmailAddress]
        [Display(Name = "Email address")]
        public string Email { get; set; } = "";
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        // The reset link must be absolute (it is opened from an email client) and must carry the
        // token in the query string.
        //
        // Built against the configured App:PublicBaseUrl, deliberately NOT the request's own host
        // (2026-08-03). The previous version passed protocol: Request.Scheme, which makes
        // Url.Page emit the attacker-supplied Host header: request a reset for a known SystemAdmin
        // with a forged Host and the victim receives a genuine, correctly-signed email whose link
        // hands the attacker a valid single-use reset token. Reading only from configuration makes
        // that impossible regardless of what any proxy in front of this app forwards. This is also
        // the same source the Worker already uses for the youth-confirmation link, so every
        // absolute link this deployment emits now agrees on one host.
        var resetBaseUri = new Uri(appOptions.Value.PublicBaseUrl, UriKind.Absolute);
        var result = await passwordResetService.RequestResetAsync(
            Input.Email,
            (userId, token) => new Uri(resetBaseUri,
                Url.Page("/Account/ResetPassword", pageHandler: null, values: new { userId, token })!).ToString(),
            CancellationToken.None);

        if (result == PasswordResetRequestResult.SystemEmailNotConfigured)
        {
            ErrorMessage = "Password reset email isn't set up on this deployment yet. Contact your system administrator.";
            return Page();
        }

        // Deliberately the same destination whether or not an account exists — see the service.
        return RedirectToPage("/Account/ForgotPasswordConfirmation");
    }
}
