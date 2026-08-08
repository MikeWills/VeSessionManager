using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using VeSessionManager.Core;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.VolunteerExaminers;

namespace VeSessionManager.Web.Pages.VeSelfService;

/// <summary>
/// The VE's own view of their record (issue #142 phase 5) — the only page in the app an
/// unauthenticated visitor can reach that shows a person's home address, which is why the
/// authorisation below is spelled out rather than inherited.
///
/// <para><b>AuthenticationSchemes is named explicitly.</b> A bare [Authorize] would authorise against
/// the default scheme, which is Identity — so this page would demand an admin login and a signed-in
/// VE would be bounced. Naming the scheme is what makes it work, and it is also what stops the
/// reverse: no admin page names this scheme, so a VE cookie opens nothing else.</para>
///
/// <para>What a VE may change is deliberately narrow: their contact details, and their email through
/// a confirmation sent to the address already on file. Not their tags, not their accreditations, not
/// the admin-facing notes — those belong to the team, and one of them they should not even see.</para>
/// </summary>
[Authorize(AuthenticationSchemes = VeSelfServiceAuth.Scheme)]
public class DetailsModel(
    AppDbContext dbContext,
    VolunteerExaminerManagementService managementService,
    VeEmailChangeService emailChangeService,
    IOptions<AppOptions> appOptions) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty]
    public string? NewEmail { get; set; }

    public VolunteerExaminer Person { get; private set; } = null!;
    public IReadOnlyList<string> Teams { get; private set; } = [];

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
        var loaded = await LoadAsync();
        if (loaded is not null) return loaded;

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
        var loaded = await LoadAsync();
        if (loaded is not null) return loaded;

        if (!ModelState.IsValid)
        {
            return Page();
        }

        await managementService.UpdateOwnContactDetailsAsync(
            Person.Id,
            new VeSelfContactDetails(Input.Name, Input.Phone, Input.AddressLine1, Input.AddressLine2,
                Input.City, Input.State, Input.PostalCode, Input.DiscordUsername, Input.ContactPreference),
            HttpContext.RequestAborted);

        TempData["StatusMessage"] = "Your details have been saved. Thank you.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostChangeEmailAsync()
    {
        var loaded = await LoadAsync();
        if (loaded is not null) return loaded;

        var baseUri = new Uri(appOptions.Value.PublicBaseUrl, UriKind.Absolute);

        var result = await emailChangeService.RequestAsync(
            Person.Id,
            NewEmail ?? "",
            token => new Uri(baseUri, Url.Page("/VeSelfService/ConfirmEmail", pageHandler: null, values: new { token })!).ToString(),
            HttpContext.RequestAborted);

        TempData[result == VeEmailChangeResult.ConfirmationSent ? "StatusMessage" : "ErrorMessage"] = result switch
        {
            VeEmailChangeResult.ConfirmationSent =>
                $"Almost done — we've emailed {Person.Email} to confirm the change. Follow the link there and your new address takes effect.",
            VeEmailChangeResult.AlreadyInUse => "Another volunteer examiner already uses that address.",
            VeEmailChangeResult.InvalidEmail => "That doesn't look like an email address.",
            VeEmailChangeResult.Unchanged => "That's already your address.",
            VeEmailChangeResult.Throttled => "We've just sent a confirmation — check your inbox before asking for another.",
            VeEmailChangeResult.SystemEmailNotConfigured => "Email isn't set up on this deployment. Contact your team admin.",
            _ => "Could not start that change."
        };

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSignOutAsync()
    {
        await HttpContext.SignOutAsync(VeSelfServiceAuth.Scheme);
        return RedirectToPage("/VeSelfService/SignIn");
    }

    private async Task<IActionResult?> LoadAsync()
    {
        // From the cookie's own claim, never a route or form value — the session IS the statement of
        // who this is, and accepting an id from the request would let anyone edit anyone.
        var id = VeSelfServiceAuth.GetVolunteerExaminerId(User);
        if (id is null)
        {
            return RedirectToPage("/VeSelfService/SignIn");
        }

        var person = await dbContext.VolunteerExaminers
            .Include(v => v.TeamMemberships).ThenInclude(m => m.Team)
            .FirstOrDefaultAsync(v => v.Id == id.Value, HttpContext.RequestAborted);

        if (person is null)
        {
            // Merged away or otherwise gone since the link was issued. Sign the stale session out
            // rather than leaving a cookie that resolves to nothing.
            await HttpContext.SignOutAsync(VeSelfServiceAuth.Scheme);
            return RedirectToPage("/VeSelfService/SignIn");
        }

        Person = person;
        Teams = [.. person.TeamMemberships.Where(m => m.IsActive).Select(m => m.Team.Name).OrderBy(n => n)];
        return null;
    }
}
