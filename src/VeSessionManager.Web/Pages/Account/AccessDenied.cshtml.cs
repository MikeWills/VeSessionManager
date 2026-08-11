using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace VeSessionManager.Web.Pages.Account;

// Public by design: Shown *because* authorization failed; requiring it would loop.
[AllowAnonymous]
public class AccessDeniedModel : PageModel
{
    public void OnGet()
    {
    }
}
