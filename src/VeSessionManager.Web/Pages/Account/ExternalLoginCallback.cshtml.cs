using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Web.Pages.Account;

/// <summary>
/// Completes an external (Google/Microsoft) sign-in. No self-service registration: an external
/// login only succeeds if it's already linked to a local account, or its email matches an existing
/// account (admin-provisioned — via DevDataSeeder today, Phase 9c's admin UI once it exists) that
/// it can link to. A brand-new email with no matching local account is rejected, not auto-created.
/// </summary>
public class ExternalLoginCallbackModel(SignInManager<User> signInManager, UserManager<User> userManager) : PageModel
{
    public string? ErrorMessage { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var info = await signInManager.GetExternalLoginInfoAsync();
        if (info is null)
        {
            ErrorMessage = "Error loading external login information.";
            return Page();
        }

        var signInResult = await signInManager.ExternalLoginSignInAsync(info.LoginProvider, info.ProviderKey, isPersistent: false, bypassTwoFactor: true);
        if (signInResult.Succeeded)
        {
            var linkedUser = await userManager.FindByLoginAsync(info.LoginProvider, info.ProviderKey);
            return RedirectToPage(RoleLandingPages.GetPath(linkedUser!.Role));
        }

        var email = info.Principal.FindFirstValue(ClaimTypes.Email);
        var existingUser = string.IsNullOrWhiteSpace(email) ? null : await userManager.FindByEmailAsync(email);
        if (existingUser is null)
        {
            ErrorMessage = "No account found for this email. Contact your administrator to be added.";
            return Page();
        }

        // Linking/signing in purely on an email-claim match is only safe if the provider actually
        // verified that email belongs to this visitor. Only Google's handler surfaces this claim
        // today (mapped explicitly in Program.cs); a provider that doesn't send it at all (e.g.
        // Microsoft) falls through unblocked, same as before this check existed.
        var emailVerifiedClaim = info.Principal.FindFirstValue("email_verified");
        if (bool.TryParse(emailVerifiedClaim, out var emailVerified) && !emailVerified)
        {
            ErrorMessage = "This external account's email address is not verified with the provider. Contact your administrator.";
            return Page();
        }

        var linkResult = await userManager.AddLoginAsync(existingUser, info);
        if (!linkResult.Succeeded)
        {
            ErrorMessage = "Could not link this external account.";
            return Page();
        }

        await signInManager.SignInAsync(existingUser, isPersistent: false);
        return RedirectToPage(RoleLandingPages.GetPath(existingUser.Role));
    }
}
