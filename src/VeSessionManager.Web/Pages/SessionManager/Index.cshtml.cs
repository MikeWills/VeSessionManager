using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace VeSessionManager.Web.Pages.SessionManager;

[Authorize(Roles = "SystemAdmin,SessionManager")]
public class IndexModel : PageModel
{
    public void OnGet()
    {
    }
}
