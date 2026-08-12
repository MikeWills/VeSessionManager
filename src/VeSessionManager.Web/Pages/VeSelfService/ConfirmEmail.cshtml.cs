using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using VeSessionManager.Core.VolunteerExaminers;

namespace VeSessionManager.Web.Pages.VeSelfService;

/// <summary>
/// Where the confirmation link from the OLD address lands (issue #142 phase 5).
///
/// <para><b>Anonymous, and that is required rather than lax.</b> The link is opened from the mailbox
/// the VE already had, which may well be a different device or browser from the one that asked for
/// the change — demanding the self-service session here would make the flow unusable for exactly the
/// person it is meant to protect. Possession of that mailbox is the proof, and the token carries
/// it.</para>
///
/// <para><b>The GET shows a button; the POST makes the change (#290).</b> It used to apply on GET,
/// which meant link-prefetching mail gateways, corporate URL scanners and browser prefetch could
/// confirm the change without the VE ever deciding to. Moving the write to a POST also gets
/// antiforgery for free. The sibling sign-in link at <c>Enter</c> still consumes its token on GET and
/// should stay that way — see <see cref="VeEmailChangeService.PeekAsync"/> for why the two differ.</para>
/// </summary>
[AllowAnonymous]
public class ConfirmEmailModel(VeEmailChangeService emailChangeService) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Token { get; set; }

    public VeEmailChangeResult Result { get; private set; } = VeEmailChangeResult.NotFound;
    public string? NewEmail { get; private set; }

    /// <summary>True on the GET that offers the button, false once the change has been attempted.</summary>
    public bool AwaitingConfirmation { get; private set; }

    public async Task OnGetAsync()
    {
        var (result, newEmail) = await emailChangeService.PeekAsync(Token ?? "", HttpContext.RequestAborted);
        Result = result;
        NewEmail = newEmail;
        AwaitingConfirmation = result == VeEmailChangeResult.Confirmed;
    }

    public async Task OnPostAsync()
    {
        var (result, newEmail) = await emailChangeService.ConfirmAsync(Token ?? "", HttpContext.RequestAborted);
        Result = result;
        NewEmail = newEmail;
    }
}
