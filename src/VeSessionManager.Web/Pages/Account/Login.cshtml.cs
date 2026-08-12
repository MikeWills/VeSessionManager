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
public class LoginModel(SignInManager<User> signInManager, UserManager<User> userManager, AppDbContext dbContext) : PageModel
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
    }

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

        var result = await signInManager.PasswordSignInAsync(Input.UserName, Input.Password, isPersistent: false, lockoutOnFailure: true);
        if (!result.Succeeded)
        {
            ErrorMessage = "Invalid username or password.";
            return Page();
        }

        var user = await userManager.FindByNameAsync(Input.UserName);

        // Local URLs only. Url.IsLocalUrl rejects absolute and protocol-relative forms, and
        // LocalRedirect throws rather than leaving the site if one slips past — belt and braces on
        // the one page where an open redirect is worth the most to an attacker.
        if (!string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl))
        {
            return LocalRedirect(ReturnUrl);
        }

        return RedirectToPage(RoleLandingPages.GetPath(user!.Role));
    }

    public IActionResult OnPostExternalLogin(string provider)
    {
        var redirectUrl = Url.Page("./ExternalLoginCallback");
        var properties = signInManager.ConfigureExternalAuthenticationProperties(provider, redirectUrl);
        return new ChallengeResult(provider, properties);
    }
}
