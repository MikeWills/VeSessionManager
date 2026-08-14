using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using VeSessionManager.Core.Authorization;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.VolunteerExaminers;

namespace VeSessionManager.Web.Pages.SessionManager;

/// <summary>
/// One question, answered on one screen: how are the people we are auditioning getting on?
///
/// <para>The VE Directory can already show this — filter by the Auditioning tag and read the rows —
/// but a report is not a filter someone has to remember to apply. This page opens on the answer, so
/// it can be checked before a session or handed to whoever runs the audition without a set of
/// instructions attached to it.</para>
///
/// <para><b>Ordered by sessions worked, most first.</b> The question being asked is who is ready, so
/// the people nearest the end of their audition are at the top. Every column is still click-to-sort
/// for the other readings.</para>
///
/// <para>Same role gate as the VE Directory (TeamAdmin/SystemAdmin). A per-person session count is
/// a leaderboard, and the deliberate decision recorded in issue #63 is that those stay on
/// admin-only screens — which is also why the stats page is aggregate-only.</para>
/// </summary>
[Authorize(Roles = RoleGroups.Admins)]
public class AuditioningReportModel(
    AppDbContext dbContext,
    UserManager<User> userManager,
    SessionAccessScope accessScope,
    VolunteerExaminerDirectoryService directoryService,
    TimeProvider timeProvider) : PageModel
{
    /// <summary>
    /// The tag this report is about. Matched by NAME rather than id because tags are per-team
    /// vocabulary — each team defines its own, so there is no single "Auditioning" row to point at,
    /// and an "all teams" view has to gather several. The directory collapses same-named tags for
    /// the same reason.
    /// </summary>
    public const string AuditioningTag = "Auditioning";

    [BindProperty(SupportsGet = true)]
    public int? TeamId { get; set; }

    public IReadOnlyList<VeDirectoryRow> Rows { get; private set; } = [];
    public IReadOnlyList<(int Id, string Name)> AvailableTeams { get; private set; } = [];
    public string TeamSummaryLabel { get; private set; } = "All teams";

    /// <summary>
    /// True when nobody in scope has the tag AND no team in scope has even defined it — a different
    /// situation from "nobody is auditioning right now", and one the empty state should say out
    /// loud rather than leaving someone to wonder whether the report is broken.
    /// </summary>
    public bool TagNotDefinedAnywhere { get; private set; }

    /// <summary>One instant for the whole render, so every license status is derived against the same clock.</summary>
    public DateTime UtcNow { get; private set; }

    public async Task OnGetAsync()
    {
        UtcNow = timeProvider.GetUtcNow().UtcDateTime;

        var user = await userManager.GetRequiredCachedUserAsync(dbContext, HttpContext, User);

        AvailableTeams = await accessScope.GetAvailableTeamsAsync(dbContext, user);
        TeamSummaryLabel = TeamId is not null
            ? AvailableTeams.FirstOrDefault(t => t.Id == TeamId).Name ?? "All teams"
            : "All teams";

        var teamIds = accessScope.ResolveViewableTeamIds(user, TeamId);

        Rows = [.. (await directoryService.GetDirectoryAsync(
                teamIds,
                new VeDirectoryFilter { TagName = AuditioningTag },
                UtcNow,
                HttpContext.RequestAborted))
            // Most-progressed first; name breaks ties so the order is stable between renders rather
            // than however the grouping happened to come back.
            .OrderByDescending(r => r.SessionsWorked)
            .ThenBy(r => r.VolunteerExaminer.Name)];

        if (Rows.Count == 0)
        {
            var scoped = teamIds;
            TagNotDefinedAnywhere = !await dbContext.VeTags
                .AnyAsync(t => (scoped == null || scoped.Contains(t.TeamId))
                               && t.Name.ToLower() == AuditioningTag.ToLower());
        }
    }

    /// <summary>
    /// The report as a file, because an audition review usually happens away from the screen.
    ///
    /// <para>Deliberately narrower than the VE Directory's export: this carries no home addresses or
    /// phone numbers. It is a progress report, not a contact list, and the directory already exists
    /// for the other job — with its own audit-log entry, because that one leaves the building.</para>
    /// </summary>
    public async Task<IActionResult> OnGetExportAsync()
    {
        await OnGetAsync();

        var csv = new System.Text.StringBuilder();
        csv.AppendLine("CallSign,Name,Teams,LicenseClass,LicenseExpires,SessionsWorked,LastWorked");

        foreach (var row in Rows)
        {
            var ve = row.VolunteerExaminer;
            csv.AppendLine(CsvExport.Row(
                ve.CallSign,
                ve.Name,
                row.TeamSummary,
                ve.OperatorClass == LicenseClass.None ? "" : ve.OperatorClass.ToString(),
                ve.LicenseExpiresUtc?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                row.SessionsWorked.ToString(CultureInfo.InvariantCulture),
                row.LastWorkedUtc?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        }

        var stamp = UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        return File(CsvExport.ToBytes(csv), "text/csv", $"auditioning-report-{stamp}.csv");
    }

    /// <summary>Route values for a person's detail page, preserving the team filter so "back" returns here.</summary>
    public Dictionary<string, string> DetailRoute(int volunteerExaminerId)
    {
        var route = new Dictionary<string, string> { ["id"] = volunteerExaminerId.ToString(CultureInfo.InvariantCulture) };
        if (TeamId is { } teamId)
        {
            route["teamId"] = teamId.ToString(CultureInfo.InvariantCulture);
        }

        return route;
    }
}
