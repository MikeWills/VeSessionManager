using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using VeSessionManager.Core.VolunteerExaminers;

namespace VeSessionManager.Web.Pages.VeSelfService;

/// <summary>
/// Redeems a sign-in link and starts the VE's short session (issue #142 phase 5).
///
/// <para><b>The token is consumed here, on arrival, not when the VE finishes.</b> A link that still
/// works after being followed is a link sitting in an inbox waiting to be found — and this one is
/// the entire credential.</para>
///
/// <para>Every failure looks the same: expired, already used and never issued are one message. The
/// distinction would tell whoever holds a stale link something about it.</para>
/// </summary>
[AllowAnonymous]
public class EnterModel(VeSelfServiceLinkService linkService) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Token { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var volunteerExaminer = await linkService.RedeemAsync(Token ?? "", HttpContext.RequestAborted);
        if (volunteerExaminer is null)
        {
            return Page();
        }

        await HttpContext.SignInAsync(
            VeSelfServiceAuth.Scheme,
            VeSelfServiceAuth.BuildPrincipal(volunteerExaminer.Id, volunteerExaminer.Name),
            new AuthenticationProperties
            {
                // Absolute, matching the cookie's own ExpireTimeSpan. Not persistent: closing the
                // browser should end it, because this is a five-minute errand and quite possibly on
                // somebody else's machine.
                IsPersistent = false,
                ExpiresUtc = DateTimeOffset.UtcNow + VeSelfServiceLinkService.SessionLifetime
            });

        return RedirectToPage("/VeSelfService/Details");
    }
}
