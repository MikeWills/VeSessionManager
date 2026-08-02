using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Web.Pages.Account;

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
        return RedirectToPage(RoleLandingPages.GetPath(user!.Role));
    }

    public IActionResult OnPostExternalLogin(string provider)
    {
        var redirectUrl = Url.Page("./ExternalLoginCallback");
        var properties = signInManager.ConfigureExternalAuthenticationProperties(provider, redirectUrl);
        return new ChallengeResult(provider, properties);
    }
}
