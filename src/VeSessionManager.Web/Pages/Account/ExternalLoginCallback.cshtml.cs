using Microsoft.AspNetCore.Authorization;
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
// Public by design: Where Google/Microsoft return to before a local session exists.
[AllowAnonymous]
public class ExternalLoginCallbackModel(SignInManager<User> signInManager, UserManager<User> userManager) : PageModel
{
    /// <summary>
    /// Providers whose email address is trusted even though they send no <c>email_verified</c>
    /// claim. **Adding a name here is a security decision**, not configuration: it says this
    /// provider verifies addresses itself and will not hand us one its user typed.
    ///
    /// <para>Hoisted out of the handler (audit T37) so it is allocated once and, more importantly,
    /// so it sits somewhere a reader can find when adding a provider — the corresponding
    /// <c>.AddGoogle()</c>/<c>.AddMicrosoftAccount()</c> calls are in Program.cs, and the risk is
    /// someone registering a third provider without ever meeting this list.</para>
    ///
    /// <para>Google is absent on purpose: it sends an affirmative <c>email_verified</c> claim, so it
    /// never needs this fallback.</para>
    /// </summary>
    private static readonly HashSet<string> ProvidersTrustedWithoutAnExplicitEmailVerifiedClaim =
        new(StringComparer.OrdinalIgnoreCase) { "Microsoft" };

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
        // verified that email belongs to this visitor. Only Google's handler surfaces an explicit
        // "email_verified" claim (mapped in Program.cs); Microsoft's doesn't send one at all, but its
        // own `mail`/`userPrincipalName` claim is administratively sourced (Entra ID work/school
        // accounts: set by the tenant against a domain Microsoft has itself verified; personal
        // Microsoft accounts: verified at account-creation time) rather than user-editable at OAuth
        // time, so it's trusted here too.
        //
        // Security review 2026-07-29 found the original version trusted ANY provider that didn't
        // send the claim at all, silently — meaning a brand-new provider added later would default
        // to "trusted" by omission instead of by a deliberate decision. Flipped to an explicit
        // allowlist: a provider not on it (and not carrying an affirmative email_verified=true claim)
        // is now blocked by default, not trusted by accident.
        var trustedWithoutExplicitClaim = ProvidersTrustedWithoutAnExplicitEmailVerifiedClaim;
        var emailVerifiedClaim = info.Principal.FindFirstValue("email_verified");
        var emailVerified = bool.TryParse(emailVerifiedClaim, out var claimedVerified)
            ? claimedVerified
            : trustedWithoutExplicitClaim.Contains(info.LoginProvider);
        if (!emailVerified)
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
