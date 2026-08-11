using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Web.Pages;

/// <summary>
/// Public "front door." Most visitors never see this — RoleLandingPages sends a signed-in user
/// straight to their real dashboard right after login — but hitting "/" directly (bookmark, no
/// active session) previously rendered the untouched scaffold-default Bootstrap page: no app
/// styling, no way to actually get anywhere else in the app. Now styled like every other public
/// page (_PublicLayout, matches Login/Privacy/AccessDenied) and redirects an already-signed-in
/// visitor straight to their role's landing page instead of showing them a dead end.
/// </summary>
// Public by design: The public front door.
[AllowAnonymous]
public class IndexModel(UserManager<User> userManager) : PageModel
{
    public async Task<IActionResult> OnGetAsync()
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            var user = await userManager.GetUserAsync(User);
            if (user is not null)
            {
                return RedirectToPage(RoleLandingPages.GetPath(user.Role));
            }
        }

        return Page();
    }
}
