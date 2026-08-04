using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using VeSessionManager.Core.Payments;

namespace VeSessionManager.Web.Pages.Public;

/// <summary>
/// Public, unauthenticated youth-rate confirmation page reached via a link in the registration
/// confirmation email. Honor-system self-attestation only — see
/// docs/youth-payment-confirmation.md and YouthPaymentConfirmationService.
/// </summary>
public class YouthConfirmModel(YouthPaymentConfirmationService service) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    public YouthConfirmationOutcome Outcome { get; private set; }

    private const string ConfirmationRequiredMessage = "You must confirm you are a youth to continue.";

    public class InputModel
    {
        /// <summary>
        /// [Required] on a non-nullable bool is a **client-side-only** guard: jQuery unobtrusive
        /// validation reads it as "must be checked", but server-side it always passes, because the
        /// checkbox tag helper emits a hidden "false" sibling and any bound value satisfies
        /// Required for a value type. It is kept for the browser experience; the authoritative
        /// check lives in OnPostAsync. Do not delete one believing the other covers it.
        /// </summary>
        [Required(ErrorMessage = ConfirmationRequiredMessage)]
        public bool ConfirmYouth { get; set; }
    }

    public async Task OnGetAsync(Guid token, CancellationToken cancellationToken)
    {
        Outcome = await service.CheckEligibilityAsync(token, cancellationToken);
    }

    public async Task<IActionResult> OnPostAsync(Guid token, CancellationToken cancellationToken)
    {
        // The authoritative attestation check (2026-08-03). This page is anonymous and reachable by
        // anyone holding the token, and [Required] above does not enforce anything server-side (see
        // InputModel) — so before this, a JS-disabled browser or a direct POST could claim the
        // reduced youth rate without ever making the attestation the honor system depends on.
        if (!Input.ConfirmYouth)
        {
            ModelState.AddModelError($"{nameof(Input)}.{nameof(InputModel.ConfirmYouth)}", ConfirmationRequiredMessage);
        }

        if (!ModelState.IsValid)
        {
            Outcome = await service.CheckEligibilityAsync(token, cancellationToken);
            return Page();
        }

        var result = await service.ConfirmAsync(token, cancellationToken);
        if (result.Outcome == YouthConfirmationOutcome.Success)
        {
            return Redirect(result.RedirectUrl!);
        }

        Outcome = result.Outcome;
        return Page();
    }
}
