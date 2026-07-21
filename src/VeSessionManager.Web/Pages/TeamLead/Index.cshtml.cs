using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace VeSessionManager.Web.Pages.TeamLead;

[Authorize(Roles = "SystemAdmin,TeamLead")]
public class IndexModel : PageModel
{
    public void OnGet()
    {
    }
}
