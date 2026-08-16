using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Authorization;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Email;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.VolunteerExaminers;

namespace VeSessionManager.Web.Pages.SessionManager;

/// <summary>
/// Writing to a team's VEs from the directory (#191) — the sibling of the candidate Email screen,
/// and of the per-session VE invitation.
///
/// <para><b>One team, chosen here.</b> A VE can be on several teams, but a message goes out over one
/// team's SMTP with that team's From and Reply-To (Mike, 2026-08-16), so the recipients are that
/// team's active members and nobody else. Same admin gate as the directory itself, which is where
/// this is reached from.</para>
/// </summary>
[Authorize(Roles = RoleGroups.Admins)]
public class VeEmailModel(
    AppDbContext dbContext,
    UserManager<User> userManager,
    AdminAccessScope adminAccessScope,
    VeMessageService messageService) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public int? TeamId { get; set; }

    [BindProperty(SupportsGet = true, Name = "template")]
    public string SelectedTemplateKey { get; set; } = "";

    /// <summary>Narrows the list to VEs who asked to hear about this team's sessions. Only meaningful — and only offered — when the team allows subscriptions at all.</summary>
    [BindProperty(SupportsGet = true)]
    public bool SubscribedOnly { get; set; }

    [BindProperty]
    public string Subject { get; set; } = "";

    [BindProperty]
    public string Body { get; set; } = "";

    [BindProperty]
    public int[] SelectedVeIds { get; set; } = [];

    public IReadOnlyList<(int Id, string Name)> AvailableTeams { get; private set; } = [];
    public string TeamSummaryLabel { get; private set; } = "Select a team…";
    public IReadOnlyList<VeMessageRecipient> Recipients { get; private set; } = [];
    public IReadOnlyList<TemplateChoice> Templates { get; private set; } = [];
    public bool SubscriptionsEnabled { get; private set; }

    public static IReadOnlyList<string> Placeholders => VolunteerExaminerPlaceholderValues.Names;

    public record TemplateChoice(string Key, string Label);

    /// <summary>What the history and audit line record for a draft written from scratch.</summary>
    public const string CustomMessageLabel = "Custom message";

    public async Task<IActionResult> OnGetAsync()
    {
        var loaded = await LoadAsync();
        if (loaded is not null) return loaded;

        if (TeamId is null)
        {
            return Page();
        }

        var template = string.IsNullOrEmpty(SelectedTemplateKey)
            ? null
            : await dbContext.EmailTemplates
                .FirstOrDefaultAsync(t => t.TeamId == TeamId && t.Key == SelectedTemplateKey, HttpContext.RequestAborted);

        Subject = template?.Subject ?? "";
        Body = template?.Body ?? "";
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var loaded = await LoadAsync();
        if (loaded is not null) return loaded;

        if (TeamId is null)
        {
            return Forbid();
        }

        if (SelectedVeIds.Length == 0)
        {
            TempData["ErrorMessage"] = "Choose at least one VE to email.";
            return RedirectToPage(new { teamId = TeamId, template = SelectedTemplateKey, subscribedOnly = SubscribedOnly });
        }

        // Only ids this page offered. The service re-scopes independently — both are deliberate, and
        // the service's is the one that matters (#238).
        var offered = Recipients.Select(r => r.VolunteerExaminer.Id).ToHashSet();
        if (SelectedVeIds.Any(id => !offered.Contains(id)))
        {
            return Forbid();
        }

        var user = await userManager.GetRequiredUserAsync(dbContext, User);
        var label = Templates.FirstOrDefault(t => t.Key == SelectedTemplateKey)?.Label ?? CustomMessageLabel;

        var result = await messageService.SendAsync(
            TeamId.Value, SelectedVeIds, Subject, Body, label, user.Id, HttpContext.RequestAborted);

        if (result.Error is not null)
        {
            TempData["ErrorMessage"] = result.Error;
            return RedirectToPage(new { teamId = TeamId, template = SelectedTemplateKey, subscribedOnly = SubscribedOnly });
        }

        var message = $"Sent {result.Sent} email(s).";
        if (result.Failed > 0) message += $" {result.Failed} failed to send.";
        if (result.NoEmailAddress > 0) message += $" {result.NoEmailAddress} had no email address on file.";
        if (result.Unsubscribed > 0) message += $" {result.Unsubscribed} have unsubscribed and were not emailed.";
        if (result.TextOnlySkipped > 0) message += $" {result.TextOnlySkipped} are set to text only, which isn't available yet.";
        if (result.NotOnTeam > 0) message += $" {result.NotOnTeam} are no longer active on this team.";

        TempData[result.Sent > 0 ? "StatusMessage" : "ErrorMessage"] = message;
        return RedirectToPage("/SessionManager/VeDirectory", new { teamId = TeamId });
    }

    private async Task<IActionResult?> LoadAsync()
    {
        var user = await userManager.GetUserWithManagerAsync(dbContext, User);
        if (user is null)
        {
            return Forbid();
        }

        AvailableTeams = await adminAccessScope.GetAvailableTeamsAsync(dbContext, user, HttpContext.RequestAborted);

        // One team at a time, like Email Templates and Team Settings: there is no merged view,
        // because the message is sent *as* a team.
        var effectiveTeamId = adminAccessScope.TryResolveManageableTeamId(user, TeamId, [.. AvailableTeams.Select(t => t.Id)]);
        TeamId = effectiveTeamId;
        TeamSummaryLabel = effectiveTeamId is not null
            ? AvailableTeams.FirstOrDefault(t => t.Id == effectiveTeamId).Name ?? "Select a team…"
            : "Select a team…";

        if (effectiveTeamId is null)
        {
            return null;
        }

        var team = await dbContext.Teams.FirstOrDefaultAsync(t => t.Id == effectiveTeamId, HttpContext.RequestAborted);
        SubscriptionsEnabled = team?.VeEmailSubscriptionsEnabled ?? false;

        var subscribedIds = SubscriptionsEnabled && SubscribedOnly
            ? await dbContext.VeTeamMemberships
                .Where(m => m.TeamId == effectiveTeamId && m.IsActive && m.EmailSubscribed)
                .Select(m => m.VolunteerExaminerId)
                .ToListAsync(HttpContext.RequestAborted)
            : null;

        Recipients = await messageService.GetRecipientsAsync(effectiveTeamId.Value, HttpContext.RequestAborted);
        if (subscribedIds is not null)
        {
            Recipients = [.. Recipients.Where(r => subscribedIds.Contains(r.VolunteerExaminer.Id))];
        }

        // Only templates written for this audience: a candidate template's {{CandidateFirstName}}
        // resolves to nothing here and would reach a VE as literal text.
        Templates = await dbContext.EmailTemplates
            .Where(t => t.TeamId == effectiveTeamId && t.IsUserDefined && t.Audience == EmailTemplateAudience.VolunteerExaminers)
            .OrderBy(t => t.DisplayName)
            .Select(t => new TemplateChoice(t.Key, t.DisplayName ?? t.Key))
            .ToListAsync(HttpContext.RequestAborted);

        return null;
    }
}
