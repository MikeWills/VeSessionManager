using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace VeSessionManager.Web.Pages.TeamAdmin;

[Authorize(Roles = "SystemAdmin,TeamAdmin")]
public class IndexModel : PageModel
{
    public void OnGet()
    {
    }
}
