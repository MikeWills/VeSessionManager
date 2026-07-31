using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Authorization;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Web.Pages.Admin;

/// <summary>
/// Phase 9c: per-Team credential editing (ExamTools/Zoom/Discord/Square/SMTP) + that team's
/// EmailSettings. SystemAdmin gets a team-picker; TeamAdmin is locked to their own team regardless
/// of a tampered ?teamId= query string — mirrors Detail.cshtml.cs's AuthorizeAsync() defense-in-depth
/// shape.
/// </summary>
[Authorize(Roles = "SystemAdmin,TeamAdmin")]
public class TeamSettingsModel(AppDbContext dbContext, UserManager<User> userManager, AdminAccessScope adminAccessScope, TeamSettingsService teamSettingsService) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public int? TeamId { get; set; }

    public bool IsSystemAdmin { get; private set; }

    /// <summary>Label for the team-picker trigger. "Select a team…" rather than "All teams" — this page edits one team's configuration, so there is no merged view to fall back to.</summary>
    public string TeamSummaryLabel { get; private set; } = "Select a team…";

    public IReadOnlyList<(int Id, string Name)> AvailableTeams { get; private set; } = [];
    public Team? Team { get; private set; }
    public EmailSettings? EmailSettings { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await userManager.GetUserWithManagerAsync(dbContext, User);
        if (user is null)
        {
            return Forbid();
        }

        IsSystemAdmin = user.Role == UserRole.SystemAdmin;
        AvailableTeams = await adminAccessScope.ScopeTeams(dbContext.Teams, user)
            .OrderBy(t => t.Name).Select(t => new ValueTuple<int, string>(t.Id, t.Name)).ToListAsync();

        var effectiveTeamId = adminAccessScope.TryResolveManageableTeamId(user, TeamId);
        TeamSummaryLabel = effectiveTeamId is not null
            ? AvailableTeams.FirstOrDefault(t => t.Id == effectiveTeamId).Name ?? "Select a team…"
            : "Select a team…";
        if (effectiveTeamId is null)
        {
            // SystemAdmin hasn't picked a team yet, or a TeamAdmin/SessionManager isn't assigned to
            // one — a benign empty state, not a permission failure. (A TeamAdmin requesting another
            // team via a tampered ?teamId= also lands here rather than Forbid() on the GET, matching
            // this page's pre-existing behavior; the POST handlers below are the enforcement point.)
            return Page();
        }

        Team = await dbContext.Teams.FirstOrDefaultAsync(t => t.Id == effectiveTeamId.Value);
        if (Team is null)
        {
            return Page();
        }

        TeamId = Team.Id;
        EmailSettings = await dbContext.EmailSettings.FirstOrDefaultAsync(e => e.TeamId == Team.Id);
        return Page();
    }

    public async Task<IActionResult> OnPostUpdateExamToolsAsync(string? teamCode, string? username, string? password, string? baseUrl)
    {
        var auth = await AuthorizeAsync();
        if (auth is null) return Forbid();

        var result = await teamSettingsService.UpdateExamToolsAsync(auth.Value.Team.Id, teamCode, username, password, baseUrl, auth.Value.User.Id, CancellationToken.None);
        SetStatus(result, "ExamTools credentials updated.");
        return RedirectToPage(new { teamId = auth.Value.Team.Id });
    }

    public async Task<IActionResult> OnPostUpdateZoomAsync(string? accountId, string? clientId, string? clientSecret, string? zoomUserId, int breakoutRoomCount)
    {
        var auth = await AuthorizeAsync();
        if (auth is null) return Forbid();

        var result = await teamSettingsService.UpdateZoomAsync(auth.Value.Team.Id, accountId, clientId, clientSecret, zoomUserId, breakoutRoomCount, auth.Value.User.Id, CancellationToken.None);
        SetStatus(result, "Zoom credentials updated.");
        return RedirectToPage(new { teamId = auth.Value.Team.Id });
    }

    public async Task<IActionResult> OnPostUpdateDiscordAsync(ulong? guildId)
    {
        var auth = await AuthorizeAsync();
        if (auth is null) return Forbid();

        var result = await teamSettingsService.UpdateDiscordAsync(auth.Value.Team.Id, guildId, auth.Value.User.Id, CancellationToken.None);
        SetStatus(result, "Discord settings updated.");
        return RedirectToPage(new { teamId = auth.Value.Team.Id });
    }

    public async Task<IActionResult> OnPostUpdateSquareAsync(string? accessToken, string? locationId, string? webhookSignatureKey, string? webhookNotificationUrl)
    {
        var auth = await AuthorizeAsync();
        if (auth is null) return Forbid();

        var result = await teamSettingsService.UpdateSquareAsync(auth.Value.Team.Id, accessToken, locationId, webhookSignatureKey, webhookNotificationUrl, auth.Value.User.Id, CancellationToken.None);
        SetStatus(result, "Square credentials updated.");
        return RedirectToPage(new { teamId = auth.Value.Team.Id });
    }

    public async Task<IActionResult> OnPostUpdateSmtpAsync(string? host, int? port, string? username, string? password, bool? useStartTls)
    {
        var auth = await AuthorizeAsync();
        if (auth is null) return Forbid();

        var result = await teamSettingsService.UpdateSmtpAsync(auth.Value.Team.Id, host, port, username, password, useStartTls, auth.Value.User.Id, CancellationToken.None);
        SetStatus(result, "SMTP credentials updated.");
        return RedirectToPage(new { teamId = auth.Value.Team.Id });
    }

    public async Task<IActionResult> OnPostUpdatePurgeSettingsAsync(int purgeUnpaidLinkDays)
    {
        var auth = await AuthorizeAsync();
        if (auth is null) return Forbid();

        var result = await teamSettingsService.UpdatePurgeSettingsAsync(auth.Value.Team.Id, purgeUnpaidLinkDays, auth.Value.User.Id, CancellationToken.None);
        SetStatus(result, "Payment link purge settings updated.");
        return RedirectToPage(new { teamId = auth.Value.Team.Id });
    }

    public async Task<IActionResult> OnPostUpdateEmailSettingsAsync(string fromAddress, string? fromDisplayName, string replyToAddress, string privacyPolicyUrl, string adminNotificationEmail)
    {
        var auth = await AuthorizeAsync();
        if (auth is null) return Forbid();

        var result = await teamSettingsService.UpdateEmailSettingsAsync(auth.Value.Team.Id, fromAddress, fromDisplayName, replyToAddress, privacyPolicyUrl, adminNotificationEmail, auth.Value.User.Id, CancellationToken.None);
        SetStatus(result, "Email settings updated.");
        return RedirectToPage(new { teamId = auth.Value.Team.Id });
    }

    private void SetStatus(TeamActionResult result, string successMessage)
    {
        if (result == TeamActionResult.Success)
        {
            TempData["StatusMessage"] = successMessage;
        }
        else
        {
            TempData["ErrorMessage"] = "Could not save changes.";
        }
    }

    private async Task<(User User, Team Team)?> AuthorizeAsync()
    {
        var user = await userManager.GetUserWithManagerAsync(dbContext, User);
        if (user is null)
        {
            return null;
        }

        IsSystemAdmin = user.Role == UserRole.SystemAdmin;
        AvailableTeams = await adminAccessScope.ScopeTeams(dbContext.Teams, user)
            .OrderBy(t => t.Name).Select(t => new ValueTuple<int, string>(t.Id, t.Name)).ToListAsync();

        var effectiveTeamId = adminAccessScope.TryResolveManageableTeamId(user, TeamId);
        if (effectiveTeamId is null)
        {
            return null;
        }

        var team = await dbContext.Teams.FirstOrDefaultAsync(t => t.Id == effectiveTeamId.Value);
        return team is null ? null : (user, team);
    }
}
