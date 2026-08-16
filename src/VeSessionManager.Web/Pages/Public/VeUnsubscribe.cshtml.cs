using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using VeSessionManager.Core.VolunteerExaminers;

namespace VeSessionManager.Web.Pages.Public;

/// <summary>
/// Where the unsubscribe link in a VE's email lands (#191).
///
/// <para><b>Anonymous, and it has to be.</b> CAN-SPAM requires an opt-out that does not make the
/// recipient log in or hunt for anything — a VE has no account here in the first place, so a gated
/// page would be no opt-out at all.</para>
///
/// <para><b>Two clicks, not one.</b> The link only shows the state and a button; the change is a POST.
/// A GET that unsubscribed on sight would be tripped by every mail client and security scanner that
/// prefetches links, silently opting people out of mail they wanted — and the antiforgery token on
/// the POST is what stops a third party doing it from a page of their own.</para>
/// </summary>
[AllowAnonymous]
public class VeUnsubscribeModel(VeUnsubscribeService unsubscribeService) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string Token { get; set; } = "";

    /// <summary>Null when the token matches nobody — the page then says only that, never whether a token exists.</summary>
    public bool? IsUnsubscribed { get; private set; }

    public string? Name { get; private set; }

    public bool Saved { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(bool resubscribe)
    {
        // Idempotent either way: somebody clicking the link in two different old emails must not see
        // a failure, and neither must somebody who changed their mind twice.
        if (resubscribe)
        {
            await unsubscribeService.ResubscribeAsync(Token, HttpContext.RequestAborted);
        }
        else
        {
            await unsubscribeService.UnsubscribeAsync(Token, HttpContext.RequestAborted);
        }

        await LoadAsync();
        Saved = IsUnsubscribed is not null;
        return Page();
    }

    private async Task LoadAsync()
    {
        var volunteerExaminer = await unsubscribeService.ResolveAsync(Token, HttpContext.RequestAborted);
        if (volunteerExaminer is null)
        {
            IsUnsubscribed = null;
            return;
        }

        // The first name only. The page is reachable by anyone holding the link, so it confirms who
        // it is about just enough to be reassuring without printing a full name to whoever opens it.
        Name = volunteerExaminer.Name.Split(' ')[0];
        IsUnsubscribed = volunteerExaminer.EmailUnsubscribedUtc is not null;
    }
}
