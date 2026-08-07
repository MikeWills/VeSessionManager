using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Web.Pages.Account;

/// <summary>
/// Self-service password change for a signed-in user (2026-08-07).
///
/// <para>Until this existed there was <b>no way to change a password at all</b> short of the emailed
/// forgot-password flow — which is for someone who cannot sign in, and which needs system SMTP that
/// has never been configured on any deployment. An admin-created account was therefore stuck forever
/// on the password its admin chose.</para>
///
/// <para>Doubles as the landing page for <see cref="User.MustChangePassword"/>: an account created by
/// an admin is redirected here on every request until the password is replaced. The redirect is done
/// by RequirePasswordChangeMiddleware, not by this page.</para>
/// </summary>
[Authorize]
public class ChangePasswordModel(
    UserManager<User> userManager,
    SignInManager<User> signInManager,
    UserManagementService userManagementService) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    /// <summary>True when an admin set this account's password and it has not been replaced yet.</summary>
    public bool IsForced { get; private set; }

    /// <summary>An external-login account has no local password; the form is not rendered for them.</summary>
    public bool HasLocalPassword { get; private set; } = true;

    public string? ErrorMessage { get; private set; }

    public class InputModel
    {
        [Required(ErrorMessage = "Enter your current password.")]
        [DataType(DataType.Password)]
        [Display(Name = "Current password")]
        public string CurrentPassword { get; set; } = "";

        [Required(ErrorMessage = "Enter a new password.")]
        [DataType(DataType.Password)]
        [Display(Name = "New password")]
        public string NewPassword { get; set; } = "";

        [Required(ErrorMessage = "Confirm the new password.")]
        [DataType(DataType.Password)]
        [Display(Name = "Confirm new password")]
        [Compare(nameof(NewPassword), ErrorMessage = "The two new passwords do not match.")]
        public string ConfirmPassword { get; set; } = "";
    }

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return Forbid();
        }

        IsForced = user.MustChangePassword;
        HasLocalPassword = await userManager.HasPasswordAsync(user);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return Forbid();
        }

        IsForced = user.MustChangePassword;
        HasLocalPassword = await userManager.HasPasswordAsync(user);

        if (!HasLocalPassword)
        {
            return Page();
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var result = await userManagementService.ChangeOwnPasswordAsync(
            user.Id, Input.CurrentPassword, Input.NewPassword, HttpContext.RequestAborted);

        if (result != UserActionResult.Success)
        {
            // One message for a wrong current password and for a rejected new one. Splitting them
            // would confirm the current password to whoever is sitting at the keyboard, which is
            // exactly the thing worth not confirming.
            ErrorMessage = "Could not change the password. Check your current password, and that the new one meets the minimum length.";
            return Page();
        }

        // Without this the security stamp change invalidates the cookie and the user is bounced to
        // the login screen immediately after succeeding — which reads as a failure.
        await signInManager.RefreshSignInAsync(user);

        TempData["StatusMessage"] = "Password changed.";
        return RedirectToPage("/Index");
    }
}
