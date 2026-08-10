using System.Globalization;
using System.Text;
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
/// The VE directory (issue #142 phase 2) — who a team's volunteer examiners are, how to reach them,
/// and whether they are current. Distinct from VE Roster, which is the session-count report; this
/// page is the one issue #142 is actually about, and the issue asks for both to live under the VEs
/// nav dropdown.
///
/// <para><b>TeamAdmin/SystemAdmin only, and that is a data-protection boundary rather than a
/// tidiness one.</b> These rows carry VEs' home addresses and phone numbers, which — unlike call
/// sign, FRN and license class — are not public FCC record data. A VE's public record typically
/// carries a PO box precisely because they chose not to publish where they live. Session Managers
/// and Team Leads get no access at all. Keep the nav gate in _AppLayout.cshtml in step with this
/// attribute; a role that cannot load the page must not be shown a link that 403s.</para>
/// </summary>
[Authorize(Roles = "SystemAdmin,TeamAdmin")]
public class VeDirectoryModel(
    AppDbContext dbContext,
    UserManager<User> userManager,
    SessionAccessScope accessScope,
    VolunteerExaminerDirectoryService directoryService,
    VolunteerExaminerImportService importService,
    TimeProvider timeProvider) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public int? TeamId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    /// <summary>The tag NAME being filtered on — see AvailableTags for why this is not an id. Also carries the guest sentinel.</summary>
    [BindProperty(SupportsGet = true)]
    public string? TagName { get; set; }

    /// <summary>Whether the tag filter is currently the "no tags at all" sentinel rather than a real tag name.</summary>
    public bool IsGuestFilter => string.Equals(TagName, VolunteerExaminerDirectoryService.GuestTagFilter, StringComparison.Ordinal);

    [BindProperty(SupportsGet = true)]
    public bool IncludeInactive { get; set; }

    /// <summary>The derived FCC status a row must have — the same value its License chip shows.</summary>
    [BindProperty(SupportsGet = true)]
    public WatchedLicenseStatus? LicenseStatus { get; set; }

    /// <summary>Which "last worked" bucket is chosen — see VeDirectoryFilterRoute for the keys.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Worked { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateTime? WorkedFrom { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateTime? WorkedTo { get; set; }

    /// <summary>
    /// Every filter as route values, for links that must come back to this exact list. Built in one
    /// place because a link that forgets one filter breaks the round trip silently, and only for the
    /// filter nobody remembered.
    /// </summary>
    public Dictionary<string, string?> FilterRoute => VeDirectoryFilterRoute.Build(
        TeamId, Search, TagName, IncludeInactive, LicenseStatus, Worked, WorkedFrom, WorkedTo);

    /// <summary>
    /// The filters <b>plus</b> the VE being linked to — one dictionary, because
    /// <c>asp-all-route-data</c> and <c>asp-route-id</c> cannot be combined.
    ///
    /// <para><b>They fight rather than merge.</b> Both feed the tag helper's single RouteValues
    /// dictionary, but <c>asp-all-route-data</c> <i>assigns</i> it while <c>asp-route-*</c>
    /// <i>adds</i> an entry — so whichever the generated code reaches last wins. With the dictionary
    /// written second, it replaced the id outright and every row linked to the detail page with no
    /// id at all. Nothing errors: the link renders, and the page it lands on cannot find the VE.</para>
    /// </summary>
    public Dictionary<string, string?> DetailRoute(int volunteerExaminerId)
    {
        var values = new Dictionary<string, string?>(FilterRoute)
        {
            ["id"] = volunteerExaminerId.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        return values;
    }

    /// <summary>Every status a VE row can actually show, for the filter menu. Ordered as the enum declares them: unknown-ish first, then healthy, then increasingly wrong.</summary>
    public static IReadOnlyList<WatchedLicenseStatus> LicenseStatuses => Enum.GetValues<WatchedLicenseStatus>();

    public bool HasTeamContext { get; private set; }
    public string TeamSummaryLabel { get; private set; } = "All teams";
    public IReadOnlyList<(int Id, string Name)> AvailableTeams { get; private set; } = [];
    public IReadOnlyList<VeDirectoryRow> Rows { get; private set; } = [];
    /// <summary>
    /// The tag filter's options, <b>one per distinct name</b>.
    ///
    /// <para>Tags are per-team vocabulary, so on "all teams" the raw list repeated "Member",
    /// "Session Manager" and "Team Lead" once per team that defined them — several identical,
    /// unlabelled radio buttons, where picking one silently excluded the other team's people. The
    /// rows already collapse same-named tags into a single chip, so the filter now agrees with what
    /// it is filtering.</para>
    ///
    /// <para>The colour shown is the one the chips use for that name, resolved by the same
    /// highest-priority rule.</para>
    /// </summary>
    public IReadOnlyList<TagOption> AvailableTags { get; private set; } = [];

    public record TagOption(string Name, string? Color);

    /// <summary>Taken once per request so every row's license status and day count are derived against the same instant — otherwise a list rendered across midnight ET could disagree with itself.</summary>
    public DateTime UtcNow { get; private set; }

    /// <summary>
    /// CSV of exactly what the current filters show — export-what-you-see, so a filtered list and
    /// its export can never disagree about who is on it.
    ///
    /// <para><b>This carries real home addresses and phone numbers out of the database in bulk.</b>
    /// That is the point of the feature, and also why it is audit-logged: the page is already
    /// TeamAdmin/SystemAdmin only, but a screen someone reads and a file they can mail onward are
    /// different kinds of exposure, and only one of them leaves the building. The audit entry
    /// records who exported and how many rows, never the contents.</para>
    /// </summary>
    public async Task<IActionResult> OnGetExportAsync()
    {
        await OnGetAsync();

        var csv = new StringBuilder();
        csv.AppendLine("CallSign,Name,Teams,Tags,Status,Email,Phone,AddressLine1,AddressLine2,City,State,PostalCode,Discord,ContactPreference,LicenseClass,LicenseExpires,Frn,LastWorked");

        foreach (var row in Rows)
        {
            var ve = row.VolunteerExaminer;
            csv.AppendLine(CsvExport.Row(
                ve.CallSign,
                ve.Name,
                row.TeamSummary,
                row.IsGuest ? "Guest" : string.Join("; ", row.Tags.Select(t => t.Name)),
                row.IsActive ? "Active" : "Retired",
                ve.Email,
                ve.Phone,
                ve.AddressLine1,
                ve.AddressLine2,
                ve.City,
                ve.State,
                ve.PostalCode,
                ve.DiscordUsername,
                ve.ContactPreference.ToString(),
                ve.OperatorClass == LicenseClass.None ? "" : ve.OperatorClass.ToString(),
                ve.LicenseExpiresUtc?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ve.Frn,
                row.LastWorkedUtc?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        }

        var user = await userManager.GetUserWithManagerAsync(dbContext, User)
            ?? throw new InvalidOperationException("No authenticated user for an [Authorize]d page.");
        dbContext.AddAuditLog(user.Id, "VeDirectoryExported", nameof(VolunteerExaminer), 0,
            $"Exported {Rows.Count} VE record(s) including contact details.", UtcNow);
        await dbContext.SaveChangesAsync(HttpContext.RequestAborted);

        var stamp = UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        return File(CsvExport.ToBytes(csv), "text/csv", $"ve-directory-{stamp}.csv");
    }

    /// <summary>
    /// Add one VE by hand — someone the team is watching before they ever work a session.
    ///
    /// <para>Runs through the CSV importer's own add path, so a hand-added person matches an existing
    /// record exactly as an imported one would: already here means nothing changes, and someone
    /// serving another team gains a membership rather than a rival record.</para>
    /// </summary>
    public async Task<IActionResult> OnPostAddAsync(int addTeamId, string? callSign, string? name, string? email, string? phone)
    {
        var user = await userManager.GetUserWithManagerAsync(dbContext, User)
            ?? throw new InvalidOperationException("No authenticated user for an [Authorize]d page.");

        // A posted team must be one this user can actually see. Without this, the team id is just a
        // number in a form and anyone could file a VE onto someone else's roster.
        var allowed = await accessScope.GetAvailableTeamsAsync(dbContext, user);
        if (!allowed.Any(t => t.Id == addTeamId))
        {
            return Forbid();
        }

        var result = await importService.AddOneAsync(addTeamId, callSign, name, email, phone, user.Id, HttpContext.RequestAborted);

        if (result.Error is not null)
        {
            TempData["ErrorMessage"] = result.Error;
        }
        else
        {
            TempData["StatusMessage"] = result.Action switch
            {
                VeImportAction.Create => "VE added.",
                VeImportAction.AddToTeam => "That VE already existed on another team and has been added to this one.",
                _ => "That VE is already on this team — no changes made."
            };
        }

        return RedirectToPage(FilterRoute);
    }

    public async Task OnGetAsync()
    {
        UtcNow = timeProvider.GetUtcNow().UtcDateTime;

        // GetUserWithManagerAsync, never the bare GetUserAsync — GetEffectiveTeamIds reads
        // user.UserTeams, which the plain UserManager call leaves unloaded, silently giving a
        // TeamAdmin an empty team set. See CLAUDE.md.
        var user = await userManager.GetUserWithManagerAsync(dbContext, User)
            ?? throw new InvalidOperationException("No authenticated user for an [Authorize]d page.");

        AvailableTeams = await accessScope.GetAvailableTeamsAsync(dbContext, user);
        HasTeamContext = AvailableTeams.Count > 0 || user.Role == UserRole.SystemAdmin;

        // ResolveViewableTeamIds, not TryResolveViewableTeamId: null here means "every team", which
        // is what lets a SystemAdmin see the directory merged rather than being bounced to an empty
        // page for having no single team selected.
        var teamIds = accessScope.ResolveViewableTeamIds(user, TeamId);

        TeamSummaryLabel = TeamId is not null
            ? AvailableTeams.FirstOrDefault(t => t.Id == TeamId).Name ?? "All teams"
            : "All teams";

        var tags = await dbContext.VeTags
            .Where(t => teamIds == null || teamIds.Contains(t.TeamId))
            .OrderBy(t => t.SortOrder).ThenBy(t => t.Name)
            .ToListAsync(HttpContext.RequestAborted);

        AvailableTags = [.. tags
            .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new TagOption(g.First().Name, VeTagColor.ForTags(g)))
            .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)];

        var (workedFromUtc, workedToUtc) = VeDirectoryFilterRoute.Resolve(Worked, WorkedFrom, WorkedTo, UtcNow);

        Rows = await directoryService.GetDirectoryAsync(
            teamIds,
            new VeDirectoryFilter
            {
                Search = Search,
                TagName = TagName,
                IncludeInactive = IncludeInactive,
                LicenseStatus = LicenseStatus,
                WorkedFromUtc = workedFromUtc,
                WorkedToUtc = workedToUtc
            },
            UtcNow,
            HttpContext.RequestAborted);
    }
}
