using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
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
public class ForgotPasswordModel(PasswordResetService passwordResetService) : PageModel
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
        // token in the query string. Page(...) with a protocol builds it against the request's own
        // host, so a deployment behind a different hostname needs no extra configuration.
        var result = await passwordResetService.RequestResetAsync(
            Input.Email,
            (userId, token) => Url.Page("/Account/ResetPassword", pageHandler: null,
                values: new { userId, token }, protocol: Request.Scheme)!,
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
