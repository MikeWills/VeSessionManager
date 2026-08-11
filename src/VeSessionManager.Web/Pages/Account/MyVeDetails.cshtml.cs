using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using VeSessionManager.Core;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.VolunteerExaminers;

namespace VeSessionManager.Web.Pages.Account;

/// <summary>
/// A signed-in user's view of their own VolunteerExaminer record (#226) — the same edits
/// /VeSelfService/Details offers, reached from inside the app instead of through an emailed link.
///
/// <para><b>Why this exists when self-service already does.</b> Self-service is entered by clicking a
/// link sent to the address on file, so it can only ever reach a VE who already has one. One VE of
/// 176 does. Everyone else is unreachable by the exact mechanism meant to reach them, and the loop
/// only opens from inside: a team lead has a login, and their login knows which VE record they are
/// (User.VolunteerExaminerId, #224).</para>
///
/// <para><b>Who this page is about is never a request value.</b> It is the linked record on the
/// signed-in account, so there is nothing to tamper with. A user with no link sees an explanation and
/// no form — an admin has to establish the link first, which is deliberate: a call sign is a
/// suggestion and not proof, and self-asserting the link would let anyone claim any VE's record.</para>
///
/// <para><b>The email field is the one place this diverges from self-service</b>, and only by
/// necessity. Changing a known address still goes through VeEmailChangeService's confirmation, which
/// mails the <i>old</i> address; setting a first address cannot, because there is no old address to
/// mail. See VolunteerExaminerManagementService.SetOwnEmailWhenUnsetAsync for why writing it directly
/// is defensible in that one case and refused in every other.</para>
/// </summary>
[Authorize]
public class MyVeDetailsModel(
    UserManager<User> userManager,
    AppDbContext dbContext,
    VolunteerExaminerManagementService managementService,
    VeEmailChangeService emailChangeService,
    IOptions<AppOptions> appOptions) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty]
    public string? NewEmail { get; set; }

    /// <summary>Null when this login is not linked to a VE record — the page then explains rather than editing.</summary>
    public VolunteerExaminer? Person { get; private set; }

    public IReadOnlyList<string> Teams { get; private set; } = [];
    public IReadOnlyList<Vec> AllVecs { get; private set; } = [];

    public class InputModel
    {
        [Required]
        [Display(Name = "Name")]
        public string Name { get; set; } = "";

        public string? Phone { get; set; }
        public string? AddressLine1 { get; set; }
        public string? AddressLine2 { get; set; }
        public string? City { get; set; }
        public string? State { get; set; }
        public string? PostalCode { get; set; }
        public string? DiscordUsername { get; set; }
        public VeContactPreference ContactPreference { get; set; }
    }

    public async Task<IActionResult> OnGetAsync()
    {
        await LoadAsync();
        if (Person is null) return Page();

        Input = new InputModel
        {
            Name = Person.Name,
            Phone = Person.Phone,
            AddressLine1 = Person.AddressLine1,
            AddressLine2 = Person.AddressLine2,
            City = Person.City,
            State = Person.State,
            PostalCode = Person.PostalCode,
            DiscordUsername = Person.DiscordUsername,
            ContactPreference = Person.ContactPreference
        };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var (person, actingUserId) = await RequireLinkedAsync();
        if (person is null) return Forbid();

        if (!ModelState.IsValid)
        {
            await LoadAsync();
            return Page();
        }

        // actingUserId is what separates this from the self-service call, which passes null on
        // purpose. Here an account really did act, and the trail should say which one.
        await managementService.UpdateOwnContactDetailsAsync(
            person.Id,
            new VeSelfContactDetails(Input.Name, Input.Phone, Input.AddressLine1, Input.AddressLine2,
                Input.City, Input.State, Input.PostalCode, Input.DiscordUsername, Input.ContactPreference),
            HttpContext.RequestAborted,
            actingUserId);

        TempData["StatusMessage"] = "Your VE details have been saved.";
        return RedirectToPage();
    }

    /// <summary>Sets a first address directly; routes a change through the confirmation flow instead.</summary>
    public async Task<IActionResult> OnPostSetEmailAsync()
    {
        var (person, actingUserId) = await RequireLinkedAsync();
        if (person is null) return Forbid();

        if (string.IsNullOrWhiteSpace(person.Email))
        {
            var set = await managementService.SetOwnEmailWhenUnsetAsync(
                person.Id, NewEmail ?? "", actingUserId, HttpContext.RequestAborted);

            TempData[set == VeManagementResult.Success ? "StatusMessage" : "ErrorMessage"] = set switch
            {
                VeManagementResult.Success => "Email address saved. Session emails to you will go here from now on.",
                VeManagementResult.EmailAlreadyInUse => "Another volunteer examiner already uses that address.",
                VeManagementResult.InvalidEmail => "That doesn't look like an email address.",
                // Only reachable if the address was set by someone else between the page rendering and
                // this post. Falling through to the confirmed path would be surprising; say so instead.
                VeManagementResult.EmailAlreadySet => "An address is already on file — reload the page and use the confirmation flow.",
                _ => "Could not save that address."
            };
            return RedirectToPage();
        }

        var baseUri = new Uri(appOptions.Value.PublicBaseUrl, UriKind.Absolute);
        var result = await emailChangeService.RequestAsync(
            person.Id,
            NewEmail ?? "",
            token => new Uri(baseUri, Url.Page("/VeSelfService/ConfirmEmail", pageHandler: null, values: new { token })!).ToString(),
            HttpContext.RequestAborted);

        TempData[result == VeEmailChangeResult.ConfirmationSent ? "StatusMessage" : "ErrorMessage"] = result switch
        {
            VeEmailChangeResult.ConfirmationSent =>
                $"We've emailed {person.Email} to confirm the change. Follow the link there and the new address takes effect.",
            VeEmailChangeResult.AlreadyInUse => "Another volunteer examiner already uses that address.",
            VeEmailChangeResult.InvalidEmail => "That doesn't look like an email address.",
            VeEmailChangeResult.Unchanged => "That's already your address.",
            VeEmailChangeResult.Throttled => "We've just sent a confirmation — check your inbox before asking for another.",
            VeEmailChangeResult.SystemEmailNotConfigured => "Email isn't set up on this deployment. Contact your team admin.",
            _ => "Could not start that change."
        };
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostAddAccreditationAsync(int vecId)
    {
        var (person, actingUserId) = await RequireLinkedAsync();
        if (person is null) return Forbid();

        var result = await managementService.AddAccreditationAsync(person.Id, vecId, actingUserId, HttpContext.RequestAborted);

        TempData[result == VeManagementResult.Success ? "StatusMessage" : "ErrorMessage"] = result switch
        {
            VeManagementResult.Success => "Accreditation added.",
            VeManagementResult.AlreadyAccredited => "That VEC is already on your list.",
            _ => "Could not add that accreditation."
        };
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemoveAccreditationAsync(int accreditationId)
    {
        var (person, actingUserId) = await RequireLinkedAsync();
        if (person is null) return Forbid();

        // mustBelongTo is load-bearing: the id comes from a form, and without it any signed-in user
        // could delete another VE's accreditation by editing the number.
        var result = await managementService.RemoveAccreditationAsync(
            accreditationId, actingUserId, HttpContext.RequestAborted, mustBelongToVolunteerExaminerId: person.Id);

        TempData[result == VeManagementResult.Success ? "StatusMessage" : "ErrorMessage"] =
            result == VeManagementResult.Success ? "Accreditation removed." : "Could not remove that accreditation.";
        return RedirectToPage();
    }

    /// <summary>
    /// Resolves the VE record for the signed-in account on a POST. Person is null when there is none,
    /// so every handler refuses rather than reading an id from the request — see the class note.
    /// </summary>
    private async Task<(VolunteerExaminer? Person, int ActingUserId)> RequireLinkedAsync()
    {
        var user = await LoadAsync();
        return user is null ? (null, 0) : (Person, user.Id);
    }

    private async Task<User?> LoadAsync()
    {
        // The link is read off the signed-in account, never a route or form value.
        var user = await userManager.GetUserAsync(User);
        if (user?.VolunteerExaminerId is not { } veId)
        {
            return user;
        }

        var person = await dbContext.VolunteerExaminers
            .Include(v => v.TeamMemberships).ThenInclude(m => m.Team)
            .Include(v => v.VecAccreditations).ThenInclude(a => a.Vec)
            .FirstOrDefaultAsync(v => v.Id == veId, HttpContext.RequestAborted);

        if (person is null)
        {
            // Merged away since the link was made. Nothing to edit; the page explains, same as unlinked.
            return user;
        }

        Person = person;
        Teams = [.. person.TeamMemberships.Where(m => m.IsActive).Select(m => m.Team.Name).OrderBy(n => n)];
        AllVecs = await dbContext.Vecs.OrderBy(v => v.Name).ToListAsync(HttpContext.RequestAborted);
        return user;
    }
}
