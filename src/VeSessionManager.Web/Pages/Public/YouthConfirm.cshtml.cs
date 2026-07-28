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

    public class InputModel
    {
        [Required(ErrorMessage = "You must confirm you are a youth to continue.")]
        public bool ConfirmYouth { get; set; }
    }

    public async Task OnGetAsync(Guid token, CancellationToken cancellationToken)
    {
        Outcome = await service.CheckEligibilityAsync(token, cancellationToken);
    }

    public async Task<IActionResult> OnPostAsync(Guid token, CancellationToken cancellationToken)
    {
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
