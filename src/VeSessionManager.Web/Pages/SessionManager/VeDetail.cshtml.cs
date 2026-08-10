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
    VolunteerExaminerReportService reportService,
    TimeProvider timeProvider) : PageModel
{
    // ---- Where the user came from -------------------------------------------------------------
    // The directory's filters ride along on the link in and back out again, so returning from a VE
    // lands on the list the user actually had rather than an unfiltered first page. Purely
    // navigational — nothing on this page reads them for anything else.
    //
    // Explicit route values rather than one returnUrl string: nothing to parse, no open-redirect
    // surface, and the tag helper encodes them. The cost is that EVERY handler's redirect has to
    // carry them, which is what SelfRoute exists to stop anyone forgetting.

    [BindProperty(SupportsGet = true)]
    public int? TeamId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? TagName { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool IncludeInactive { get; set; }

    [BindProperty(SupportsGet = true)]
    public WatchedLicenseStatus? LicenseStatus { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Worked { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateTime? WorkedFrom { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateTime? WorkedTo { get; set; }

    /// <summary>The directory's filters, for the link back. Same builder the directory itself uses, so the two cannot disagree about which filters exist.</summary>
    public Dictionary<string, string?> FilterRoute => VeDirectoryFilterRoute.Build(
        TeamId, Search, TagName, IncludeInactive, LicenseStatus, Worked, WorkedFrom, WorkedTo);

    /// <summary>
    /// Route values for a redirect back to <i>this</i> page that keep the directory's filters.
    /// Every POST handler returns through this — dropping them in one handler is exactly how a back
    /// link silently stops working, and only after a save, which is the hardest kind to notice.
    /// </summary>
    private RouteValueDictionary SelfRoute()
    {
        var values = new RouteValueDictionary(FilterRoute) { ["id"] = Id };
        return values;
    }



    [BindProperty(SupportsGet = true)]
    public int Id { get; set; }

    [BindProperty]
    public ContactInput Contact { get; set; } = new();

    public VolunteerExaminer Person { get; private set; } = null!;
    public IReadOnlyList<MembershipView> Memberships { get; private set; } = [];
    public IReadOnlyList<Vec> AvailableVecs { get; private set; } = [];

    /// <summary>Sessions actually worked — total, this year, per team, and the most recent few.</summary>
    public VeSessionHistory SessionHistory { get; private set; } = new(0, 0, 0, [], []);

    /// <summary>The Eastern year the "this year" count covers — stated on the page rather than left for the reader to assume.</summary>
    public int CurrentYear => SessionHistory.Year;

    /// <summary>How many recent sessions the page lists. Mike asked for five.</summary>
    public const int RecentSessionCount = 5;

    /// <summary>One instant for the whole render, so the status chip and the day count cannot disagree.</summary>
    public DateTime UtcNow { get; private set; }

    public class ContactInput
    {
        [Required]
        [Display(Name = "Name")]
        public string Name { get; set; } = "";

        [EmailAddress]
        [Display(Name = "Email")]
        public string? Email { get; set; }

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
            Email = Person.Email,
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
            new VeContactDetails(Contact.Name, Contact.Email, Contact.Phone, Contact.AddressLine1, Contact.AddressLine2,
                Contact.City, Contact.State, Contact.PostalCode, Contact.DiscordUsername,
                Contact.ContactPreference, Contact.Notes),
            (await CurrentUserAsync()).Id,
            HttpContext.RequestAborted);

        SetStatus(result, "Contact details saved.");
        return RedirectToPage(SelfRoute());
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
        return RedirectToPage(SelfRoute());
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
        return RedirectToPage(SelfRoute());
    }

    public async Task<IActionResult> OnPostAddAccreditationAsync(int vecId)
    {
        var loaded = await LoadAsync();
        if (loaded is not null) return loaded;

        var result = await managementService.AddAccreditationAsync(Id, vecId, (await CurrentUserAsync()).Id, HttpContext.RequestAborted);
        SetStatus(result, "Accreditation added.");
        return RedirectToPage(SelfRoute());
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
        return RedirectToPage(SelfRoute());
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

        // Scoped to the teams this admin can see rather than the person's whole history: a TeamAdmin
        // sharing a VE with another team has no business reading that team's session titles.
        SessionHistory = await reportService.GetPersonSessionHistoryAsync(
            person.Id, teamIds, UtcNow, RecentSessionCount, HttpContext.RequestAborted);

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
            VeManagementResult.EmailAlreadyInUse => "Another VE already uses that email address.",
            _ => "Could not save that change."
        };
    }
}
