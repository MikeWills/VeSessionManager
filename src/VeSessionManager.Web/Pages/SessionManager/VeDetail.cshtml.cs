using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Authorization;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.VolunteerExaminers;

namespace VeSessionManager.Web.Pages.SessionManager;

/// <summary>
/// One VE's record (issue #142 phase 2): contact details, the teams they serve, per-team tags, and
/// VEC accreditations.
///
/// <para>Same access boundary as the directory — TeamAdmin/SystemAdmin only, because this page shows
/// a home address and phone number, which are not public FCC record data. Every handler re-checks
/// that the person is reachable from a team the user can see, so a guessed id from another
/// deployment's team cannot be opened or edited by URL.</para>
///
/// <para><b>Email is displayed but not editable here.</b> It is the factor phase 5's self-service
/// magic link authenticates against, so changing it decides who receives future links — that needs
/// its own design (parked with Mike, 2026-08-07) rather than riding along in a general contact-details
/// form.</para>
/// </summary>
[Authorize(Roles = "SystemAdmin,TeamAdmin")]
public class VeDetailModel(
    AppDbContext dbContext,
    UserManager<User> userManager,
    SessionAccessScope accessScope,
    VolunteerExaminerDirectoryService directoryService,
    VolunteerExaminerManagementService managementService,
    TimeProvider timeProvider) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public int Id { get; set; }

    [BindProperty]
    public ContactInput Contact { get; set; } = new();

    public VolunteerExaminer Person { get; private set; } = null!;
    public IReadOnlyList<MembershipView> Memberships { get; private set; } = [];
    public IReadOnlyList<Vec> AvailableVecs { get; private set; } = [];

    /// <summary>One instant for the whole render, so the status chip and the day count cannot disagree.</summary>
    public DateTime UtcNow { get; private set; }

    public class ContactInput
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
        public string? Notes { get; set; }
    }

    public record MembershipView(VeTeamMembership Membership, IReadOnlyList<VeTag> TeamTags, IReadOnlyList<int> SelectedTagIds);

    public async Task<IActionResult> OnGetAsync()
    {
        var loaded = await LoadAsync();
        if (loaded is not null) return loaded;

        Contact = new ContactInput
        {
            Name = Person.Name,
            Phone = Person.Phone,
            AddressLine1 = Person.AddressLine1,
            AddressLine2 = Person.AddressLine2,
            City = Person.City,
            State = Person.State,
            PostalCode = Person.PostalCode,
            DiscordUsername = Person.DiscordUsername,
            ContactPreference = Person.ContactPreference,
            Notes = Person.Notes
        };
        return Page();
    }

    public async Task<IActionResult> OnPostContactAsync()
    {
        var loaded = await LoadAsync();
        if (loaded is not null) return loaded;

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var result = await managementService.UpdateContactDetailsAsync(
            Id,
            new VeContactDetails(Contact.Name, Contact.Phone, Contact.AddressLine1, Contact.AddressLine2,
                Contact.City, Contact.State, Contact.PostalCode, Contact.DiscordUsername,
                Contact.ContactPreference, Contact.Notes),
            (await CurrentUserAsync()).Id,
            HttpContext.RequestAborted);

        SetStatus(result, "Contact details saved.");
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostTagsAsync(int membershipId, int[]? tagIds)
    {
        var loaded = await LoadAsync();
        if (loaded is not null) return loaded;

        // The membership must belong to this person, or a posted id could retag someone else.
        if (Memberships.All(m => m.Membership.Id != membershipId))
        {
            return Forbid();
        }

        var result = await managementService.SetTagsAsync(membershipId, tagIds ?? [], (await CurrentUserAsync()).Id, HttpContext.RequestAborted);
        SetStatus(result, "Tags saved.");
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostMembershipActiveAsync(int membershipId, bool isActive)
    {
        var loaded = await LoadAsync();
        if (loaded is not null) return loaded;

        if (Memberships.All(m => m.Membership.Id != membershipId))
        {
            return Forbid();
        }

        var result = await managementService.SetMembershipActiveAsync(membershipId, isActive, (await CurrentUserAsync()).Id, HttpContext.RequestAborted);
        SetStatus(result, isActive ? "VE reactivated on this team." : "VE retired from this team.");
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostAddAccreditationAsync(int vecId, string? accreditationNumber, DateTime? expiresUtc)
    {
        var loaded = await LoadAsync();
        if (loaded is not null) return loaded;

        var result = await managementService.AddAccreditationAsync(Id, vecId, accreditationNumber, expiresUtc, (await CurrentUserAsync()).Id, HttpContext.RequestAborted);
        SetStatus(result, "Accreditation added.");
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostRemoveAccreditationAsync(int accreditationId)
    {
        var loaded = await LoadAsync();
        if (loaded is not null) return loaded;

        if (Person.VecAccreditations.All(a => a.Id != accreditationId))
        {
            return Forbid();
        }

        var result = await managementService.RemoveAccreditationAsync(accreditationId, (await CurrentUserAsync()).Id, HttpContext.RequestAborted);
        SetStatus(result, "Accreditation removed.");
        return RedirectToPage(new { id = Id });
    }

    private Task<User> CurrentUserAsync() =>
        userManager.GetUserWithManagerAsync(dbContext, User)!
            .ContinueWith(t => t.Result ?? throw new InvalidOperationException("No authenticated user for an [Authorize]d page."));

    /// <summary>
    /// Loads the person and the memberships this user is entitled to act on. Returns non-null when
    /// the request must not proceed — every handler calls it first, so the authorization check
    /// cannot be forgotten on a new handler that copies an existing one.
    /// </summary>
    private async Task<IActionResult?> LoadAsync()
    {
        var user = await userManager.GetUserWithManagerAsync(dbContext, User)
            ?? throw new InvalidOperationException("No authenticated user for an [Authorize]d page.");

        UtcNow = timeProvider.GetUtcNow().UtcDateTime;

        var person = await directoryService.GetPersonAsync(Id, HttpContext.RequestAborted);
        if (person is null)
        {
            return NotFound();
        }

        var viewableTeamIds = accessScope.ResolveViewableTeamIds(user, null);

        // null means "every team" for a SystemAdmin — the trap documented in CLAUDE.md, where
        // `GetEffectiveTeamIds(user)?.Contains(id) ?? false` reads as a tidy guard and is always
        // false for exactly the role that should see everything.
        var visibleMemberships = viewableTeamIds is null
            ? person.TeamMemberships
            : [.. person.TeamMemberships.Where(m => viewableTeamIds.Contains(m.TeamId))];

        if (visibleMemberships.Count == 0)
        {
            // Not NotFound: this person exists, but on a team this admin has nothing to do with.
            return Forbid();
        }

        var teamIds = visibleMemberships.Select(m => m.TeamId).ToList();
        var tagsByTeam = (await dbContext.VeTags
                .Where(t => teamIds.Contains(t.TeamId))
                .OrderBy(t => t.SortOrder).ThenBy(t => t.Name)
                .ToListAsync(HttpContext.RequestAborted))
            .GroupBy(t => t.TeamId)
            .ToDictionary(g => g.Key, IReadOnlyList<VeTag> (g) => [.. g]);

        Person = person;
        Memberships = [.. visibleMemberships
            .OrderBy(m => m.Team.Name)
            .Select(m => new MembershipView(
                m,
                tagsByTeam.TryGetValue(m.TeamId, out var tags) ? tags : [],
                [.. m.TagAssignments.Select(a => a.VeTagId)]))];

        AvailableVecs = await dbContext.Vecs.OrderBy(v => v.Name).ToListAsync(HttpContext.RequestAborted);
        return null;
    }

    private void SetStatus(VeManagementResult result, string successMessage)
    {
        if (result == VeManagementResult.Success)
        {
            TempData["StatusMessage"] = successMessage;
            return;
        }

        TempData["ErrorMessage"] = result switch
        {
            VeManagementResult.NotFound => "That record no longer exists.",
            VeManagementResult.TagNotOnThisTeam => "That tag belongs to a different team.",
            VeManagementResult.AlreadyAccredited => "This VE already has an accreditation with that VEC.",
            VeManagementResult.NameRequired => "A name is required.",
            _ => "Could not save that change."
        };
    }
}
