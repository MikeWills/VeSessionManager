using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Web.Pages.Account;

/// <summary>
/// "Sign out other devices" — the counterweight to a 30-day remembered session (#340).
///
/// <para>Its own page rather than a button on Change password: the two are unrelated actions that
/// happen to both concern an account, and a destructive-feeling one sitting under a heading about
/// passwords is easy to click by accident and hard to find on purpose.</para>
///
/// <para>GET renders a confirmation. Nothing happens without a POST, because the effect — kicking
/// yourself off every other device — is not something a prefetched or mistyped URL should cause.</para>
/// </summary>
public class SignOutOtherDevicesModel(
    UserManager<User> userManager,
    SignInManager<User> signInManager,
    UserManagementService userManagementService) : PageModel
{
    public string? ErrorMessage { get; private set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            ErrorMessage = "Could not load your account.";
            return Page();
        }

        var result = await userManagementService.SignOutOtherSessionsAsync(user.Id, HttpContext.RequestAborted);
        if (result != UserActionResult.Success)
        {
            ErrorMessage = "Could not sign out your other devices. Try again.";
            return Page();
        }

        // Trusted devices are covered by the same stamp rotation (#356), and deliberately WITHOUT
        // calling ForgetTwoFactorClientAsync here. Identity registers its security-stamp validator on
        // the two-factor remember-me cookie as well as the application cookie, so rotating the stamp
        // invalidates other devices' "trust this device" cookies on their next revalidation — the
        // same up-to-30-minutes window this page already warns about.
        //
        // ForgetTwoFactorClientAsync would clear THIS browser's trust instead, which is the one
        // device the button is not about: RefreshSignInAsync below deliberately keeps the person who
        // clicked signed in, and challenging them on their own machine afterwards would be a
        // surprise, not a security gain.

        // The stamp this browser's cookie carries is now stale too, so without re-signing in, the
        // person who just clicked the button is bounced to the login page — which reads as the
        // action having failed rather than having worked. Same reason ChangePassword does it.
        await signInManager.RefreshSignInAsync(user);

        TempData["StatusMessage"] =
            "Your other devices have been signed out. It can take up to 30 minutes to take effect on a device that is not currently being used.";
        return RedirectToPage("/Index");
    }
}
