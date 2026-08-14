using Microsoft.AspNetCore.Authorization;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Web.Pages.Account;

// Public by design: Requiring sign-in to reach the sign-in page is the classic redirect loop.
[AllowAnonymous]
public class LoginModel(SignInManager<User> signInManager, UserManager<User> userManager, AppDbContext dbContext, TimeProvider timeProvider) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    public IList<AuthenticationScheme> ExternalLogins { get; set; } = [];

    public string? ErrorMessage { get; set; }

    /// <summary>
    /// True when this deployment has no account anyone could sign in as. Without saying so here, a
    /// fresh install just rejects every credential with "Invalid username or password", which reads
    /// as a forgotten password rather than as "setup was never finished". Checks PasswordHash rather
    /// than a row count: the Worker's DevDataSeeder creates a passwordless "System" user to own
    /// audit-trail foreign keys.
    /// </summary>
    public bool NoAccountsExist { get; private set; }

    public class InputModel
    {
        [Required]
        public string UserName { get; set; } = "";

        [Required]
        [DataType(DataType.Password)]
        public string Password { get; set; } = "";

        /// <summary>
        /// Opt-in, default off. Deliberately not [Required] — a non-nullable bool always satisfies
        /// that, so it would be a client-side-only decoration (see CLAUDE.md).
        /// </summary>
        [Display(Name = "Keep me signed in on this device")]
        public bool RememberMe { get; set; }
    }

    /// <summary>For the checkbox label, so the page cannot claim a window the cookie does not use.</summary>
    public static string RememberMeDurationLabel => RememberMe.DurationLabel;

    public async Task OnGetAsync()
    {
        ExternalLogins = (await signInManager.GetExternalAuthenticationSchemesAsync()).ToList();
        NoAccountsExist = !await dbContext.Users.AnyAsync(u => u.PasswordHash != null);
    }

    /// <summary>
    /// Where the user was heading before the <c>[Authorize]</c> redirect sent them here. Never
    /// honoured before (#272), so every deep link, bookmark and emailed admin link landed on the
    /// role dashboard instead — the common path since the 2026-08-10 FallbackPolicy made every page
    /// authenticated.
    ///
    /// <para><b>Redirected with <see cref="ControllerBase.LocalRedirect"/> and gated on
    /// <c>Url.IsLocalUrl</c>, not <c>Redirect</c>.</b> The absence of this parameter is currently the
    /// entire reason this app has no open-redirect vector; adding it back carelessly would create
    /// one, and a login page is the highest-value place to have that.</para>
    /// </summary>
    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public async Task<IActionResult> OnPostAsync()
    {
        ExternalLogins = (await signInManager.GetExternalAuthenticationSchemesAsync()).ToList();
        if (!ModelState.IsValid)
        {
            return Page();
        }

        // Check the password, then sign in as a separate step, rather than PasswordSignInAsync doing
        // both. The reason is the cookie: PasswordSignInAsync takes only an isPersistent flag, and a
        // persistent cookie still expires after ExpireTimeSpan (eight hours) — there is no overload
        // that accepts an explicit lifetime. Signing in afterwards to correct it works, but emits a
        // SECOND Set-Cookie for the same name, and only the last one counts. Splitting the two
        // issues exactly one cookie, with the right lifetime the first time.
        //
        // CheckPasswordSignInAsync performs the same PreSignInCheck (lockout, CanSignIn) and the
        // same lockoutOnFailure accounting, so nothing about failed-attempt handling changes.
        var user = await userManager.FindByNameAsync(Input.UserName);
        var result = user is null
            ? Microsoft.AspNetCore.Identity.SignInResult.Failed
            : await signInManager.CheckPasswordSignInAsync(user, Input.Password, lockoutOnFailure: true);

        if (!result.Succeeded)
        {
            // One message for "no such user" and "wrong password", as before — the distinction is
            // exactly what an attacker enumerating accounts wants.
            ErrorMessage = "Invalid username or password.";
            return Page();
        }

        // Ticked: an explicit 30-day window. Unticked: a session cookie, byte for byte what this
        // page produced before #340 — the shared-computer behaviour was deliberate and is preserved.
        await signInManager.SignInAsync(
            user!,
            Input.RememberMe
                ? RememberMe.Properties(timeProvider.GetUtcNow())
                : new AuthenticationProperties { IsPersistent = false });

        // Local URLs only. Url.IsLocalUrl rejects absolute and protocol-relative forms, and
        // LocalRedirect throws rather than leaving the site if one slips past — belt and braces on
        // the one page where an open redirect is worth the most to an attacker.
        if (!string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl))
        {
            return LocalRedirect(ReturnUrl);
        }

        return RedirectToPage(RoleLandingPages.GetPath(user!.Role));
    }

    /// <summary>
    /// The checkbox has to survive the trip to Google/Microsoft and back, because the callback is a
    /// fresh GET with no form state. AuthenticationProperties.Items round-trips through the external
    /// scheme's own cookie, so it comes back in ExternalLoginInfo.AuthenticationProperties.
    /// </summary>
    public IActionResult OnPostExternalLogin(string provider)
    {
        var redirectUrl = Url.Page("./ExternalLoginCallback");
        var properties = signInManager.ConfigureExternalAuthenticationProperties(provider, redirectUrl);
        // Input is [BindProperty], so it binds on this handler too — the provider buttons sit inside
        // the same form as the checkbox precisely so this value arrives. UserName/Password come
        // through empty and are not read here.
        properties.Items[RememberMe.ExternalPropertyKey] = Input.RememberMe ? "true" : "false";
        return new ChallengeResult(provider, properties);
    }
}
