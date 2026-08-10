using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace VeSessionManager.Web.Pages.VeSelfService;

/// <summary>Static confirmation. Says the same thing whether or not a link was actually sent — see VeSelfServiceLinkService.</summary>
[AllowAnonymous]
public class LinkSentModel : PageModel
{
}
