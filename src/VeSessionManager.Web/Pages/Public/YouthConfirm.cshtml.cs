using Microsoft.AspNetCore.Authorization;
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
// Public by design: A candidate confirming a youth rate has no account here.
[AllowAnonymous]
public class YouthConfirmModel(YouthPaymentConfirmationService service) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    public YouthConfirmationOutcome Outcome { get; private set; }

    /// <summary>
    /// The team's own reply-to address, for the COPPA instructions (#192). Null when the team has no
    /// EmailSettings row — the copy then names no address rather than naming a wrong one, since this
    /// page is shared by every team on the deployment.
    /// </summary>
    public string? TeamContactEmail { get; private set; }

    /// <summary>The team's own intro paragraph, or the shipped default — see YouthEligibility's own remarks. Rendered raw; it's team-authored rich text, the same trust level as a message rule body.</summary>
    public string IntroHtml { get; private set; } = YouthConfirmDefaults.IntroHtml;

    private const string ConfirmationRequiredMessage = "You must confirm you are a youth to continue.";
    private const string Under13RequiredMessage = "Please answer whether the candidate is under 13.";
    private const string CoppaFormRequiredMessage = "You must confirm the COPPA consent form has been sent to ExamTools before continuing.";

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

        /// <summary>
        /// "Is the candidate under 13?" (2026-08-26) — a dropdown, not a checkbox, so "never
        /// answered" (null) is distinguishable from "answered No" (false). Unlike ConfirmYouth,
        /// [Required] on a *nullable* bool genuinely does validate server-side (null fails it, false
        /// passes it) — kept for the client-side highlight, with OnPostAsync's manual check as the
        /// authoritative one anyway, so both rules live in one place and produce the same message.
        /// </summary>
        [Required(ErrorMessage = Under13RequiredMessage)]
        public bool? DeclaredUnder13 { get; set; }

        /// <summary>
        /// "I have sent this form to ExamTools" — only enforced when <see cref="DeclaredUnder13"/>
        /// is true. Same [Required]-on-bool caveat as ConfirmYouth: client-side only, the
        /// authoritative check is the manual one in OnPostAsync.
        /// </summary>
        public bool CoppaFormSent { get; set; }
    }

    public async Task OnGetAsync(Guid token, CancellationToken cancellationToken)
    {
        var eligibility = await service.CheckEligibilityAsync(token, cancellationToken);
        Outcome = eligibility.Outcome;
        TeamContactEmail = eligibility.TeamContactEmail;
        IntroHtml = eligibility.IntroHtml;
    }

    public async Task<IActionResult> OnPostAsync(Guid token, CancellationToken cancellationToken)
    {
        // The authoritative attestation checks (2026-08-03, extended 2026-08-26). This page is
        // anonymous and reachable by anyone holding the token, and [Required] on a non-nullable bool
        // does not enforce anything server-side (see InputModel) — so before this, a JS-disabled
        // browser or a direct POST could bypass any of these.
        if (!Input.ConfirmYouth)
        {
            ModelState.AddModelError($"{nameof(Input)}.{nameof(InputModel.ConfirmYouth)}", ConfirmationRequiredMessage);
        }

        if (Input.DeclaredUnder13 is null)
        {
            ModelState.AddModelError($"{nameof(Input)}.{nameof(InputModel.DeclaredUnder13)}", Under13RequiredMessage);
        }
        else if (Input.DeclaredUnder13 == true && !Input.CoppaFormSent)
        {
            ModelState.AddModelError($"{nameof(Input)}.{nameof(InputModel.CoppaFormSent)}", CoppaFormRequiredMessage);
        }

        if (!ModelState.IsValid)
        {
            var eligibility = await service.CheckEligibilityAsync(token, cancellationToken);
            Outcome = eligibility.Outcome;
            TeamContactEmail = eligibility.TeamContactEmail;
            IntroHtml = eligibility.IntroHtml;
            return Page();
        }

        var result = await service.ConfirmAsync(token, Input.DeclaredUnder13!.Value, Input.CoppaFormSent, cancellationToken);
        if (result.Outcome == YouthConfirmationOutcome.Success)
        {
            return Redirect(result.RedirectUrl!);
        }

        Outcome = result.Outcome;
        return Page();
    }
}
