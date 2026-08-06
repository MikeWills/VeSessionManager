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

        var effectiveTeamId = adminAccessScope.TryResolveManageableTeamId(user, TeamId, AvailableTeams.Select(t => t.Id).ToList());
        // Reflect an auto-selected single team back into the bound property, so the picker renders it
        // as chosen rather than showing an unchecked radio beside a page that is already displaying
        // that team's data.
        TeamId = effectiveTeamId;
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

    public async Task<IActionResult> OnPostUpdateSquareAsync(string? accessToken, string? locationId, string? webhookSignatureKey, string? webhookNotificationUrl, SquareApiEnvironment environment)
    {
        var auth = await AuthorizeAsync();
        if (auth is null) return Forbid();

        var result = await teamSettingsService.UpdateSquareAsync(auth.Value.Team.Id, accessToken, locationId, webhookSignatureKey, webhookNotificationUrl, environment, auth.Value.User.Id, CancellationToken.None);
        SetStatus(result, "Square credentials updated.");
        return RedirectToPage(new { teamId = auth.Value.Team.Id });
    }

    /// <param name="useStartTls">Non-nullable on purpose: the form always posts an explicit
    /// true/false (see the hidden sibling input in the .cshtml), so a null here would mean the form
    /// was tampered with or changed — and binding it as null used to be indistinguishable from
    /// "unchecked", which is the bug that made STARTTLS impossible to turn off.</param>
    public async Task<IActionResult> OnPostUpdateSmtpAsync(string? host, int? port, string? username, string? password, bool useStartTls)
    {
        var auth = await AuthorizeAsync();
        if (auth is null) return Forbid();

        var result = await teamSettingsService.UpdateSmtpAsync(auth.Value.Team.Id, host, port, username, password, useStartTls, auth.Value.User.Id, CancellationToken.None);
        SetStatus(result, "SMTP credentials updated.");
        return RedirectToPage(new { teamId = auth.Value.Team.Id });
    }

    /// <summary>
    /// Stores the team's email logo. The file is read into memory rather than streamed to disk:
    /// it is capped at 200KB, it is destined for a database column, and there is nowhere on disk to
    /// put it that would survive a deploy (see Team.LogoBytes).
    /// </summary>
    public async Task<IActionResult> OnPostUpdateLogoAsync(IFormFile? logoFile)
    {
        var auth = await AuthorizeAsync();
        if (auth is null) return Forbid();

        if (logoFile is null || logoFile.Length == 0)
        {
            TempData["ErrorMessage"] = "Choose an image file to upload.";
            return RedirectToPage(new { teamId = auth.Value.Team.Id });
        }

        // Checked before reading so an oversized upload is rejected without buffering it; the
        // service re-checks the actual byte count, which is the authoritative test.
        if (logoFile.Length > TeamSettingsService.MaxLogoBytes)
        {
            TempData["ErrorMessage"] = $"That image is {logoFile.Length / 1024}KB. The limit is {TeamSettingsService.MaxLogoBytes / 1024}KB — every email the team sends carries a copy.";
            return RedirectToPage(new { teamId = auth.Value.Team.Id });
        }

        using var buffer = new MemoryStream();
        await logoFile.CopyToAsync(buffer, HttpContext.RequestAborted);

        var result = await teamSettingsService.UpdateLogoAsync(auth.Value.Team.Id, buffer.ToArray(), auth.Value.User.Id, HttpContext.RequestAborted);
        if (result == TeamActionResult.LogoUnsupportedFormat)
        {
            TempData["ErrorMessage"] = "That file isn't a PNG or JPEG. Mail clients don't reliably render anything else.";
            return RedirectToPage(new { teamId = auth.Value.Team.Id });
        }

        SetStatus(result, "Logo updated.");
        return RedirectToPage(new { teamId = auth.Value.Team.Id });
    }

    public async Task<IActionResult> OnPostRemoveLogoAsync()
    {
        var auth = await AuthorizeAsync();
        if (auth is null) return Forbid();

        var result = await teamSettingsService.UpdateLogoAsync(auth.Value.Team.Id, null, auth.Value.User.Id, HttpContext.RequestAborted);
        SetStatus(result, "Logo removed.");
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

        var effectiveTeamId = adminAccessScope.TryResolveManageableTeamId(user, TeamId, AvailableTeams.Select(t => t.Id).ToList());
        if (effectiveTeamId is null)
        {
            return null;
        }

        var team = await dbContext.Teams.FirstOrDefaultAsync(t => t.Id == effectiveTeamId.Value);
        return team is null ? null : (user, team);
    }
}
