using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace VeSessionManager.Web.Pages.SystemAdmin;

[Authorize(Roles = "SystemAdmin")]
public class IndexModel : PageModel
{
    public void OnGet()
    {
    }
}
