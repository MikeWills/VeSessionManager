using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using VeSessionManager.Core;
using VeSessionManager.Core.VolunteerExaminers;

namespace VeSessionManager.Web.Pages.VeSelfService;

/// <summary>
/// Where a VE asks for a sign-in link (issue #142 phase 5).
///
/// <para><b>Anonymous by necessity and rate-limited by path.</b> Everything under /VeSelfService is
/// covered by the global limiter registered in Program.cs — the pages carry no per-page attribute so
/// that a new page here is protected the moment it exists rather than when someone remembers.</para>
///
/// <para>The confirmation is identical whether or not the address belongs to a VE. Saying "no such
/// VE" would turn this into a way to discover who volunteers on this deployment.</para>
/// </summary>
[AllowAnonymous]
public class SignInModel(VeSelfServiceLinkService linkService, IOptions<AppOptions> appOptions) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    /// <summary>Only ever set for a deployment misconfiguration — never for anything about an address.</summary>
    public string? ErrorMessage { get; private set; }

    public class InputModel
    {
        [Required]
        [EmailAddress]
        [Display(Name = "Email address")]
        public string Email { get; set; } = "";
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        // Built from App:PublicBaseUrl, never the request's Host header. The password-reset flow
        // learned this the hard way: a forged Host produces a genuine, correctly-signed email whose
        // link hands the attacker the token. See docs/security-hardening-2026-08-03.md.
        var baseUri = new Uri(appOptions.Value.PublicBaseUrl, UriKind.Absolute);

        var result = await linkService.RequestLinkAsync(
            Input.Email,
            token => new Uri(baseUri, Url.Page("/VeSelfService/Enter", pageHandler: null, values: new { token })!).ToString(),
            HttpContext.RequestAborted);

        if (result == VeSelfServiceRequestResult.SystemEmailNotConfigured)
        {
            ErrorMessage = "Self-service isn't set up on this deployment yet. Contact your team admin.";
            return Page();
        }

        return RedirectToPage("/VeSelfService/LinkSent");
    }
}
