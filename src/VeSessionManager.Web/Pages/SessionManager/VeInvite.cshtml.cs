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
/// Composing an invitation to a session's VEs (issue #142 phase 6).
///
/// <para>Reached from Session Detail, and gated on the same <c>SessionAccessScope.CanEdit</c> the
/// rest of that page's actions use — a Session Manager running the session is exactly who invites
/// people to it, so this is deliberately NOT restricted to admins the way the VE Directory is. It
/// shows names, tags and eligibility; it does not show contact details.</para>
/// </summary>
[Authorize(Roles = "SystemAdmin,TeamAdmin,SessionManager")]
public class VeInviteModel(
    AppDbContext dbContext,
    UserManager<User> userManager,
    SessionAccessScope accessScope,
    VeSessionInvitationService invitationService) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public int Id { get; set; }

    [BindProperty]
    public string Subject { get; set; } = "";

    [BindProperty]
    public string Body { get; set; } = "";

    [BindProperty]
    public int[] SelectedVeIds { get; set; } = [];

    public Session Session { get; private set; } = null!;
    public IReadOnlyList<VeInvitationCandidate> Candidates { get; private set; } = [];

    public static IReadOnlyList<string> Placeholders => VeSessionInvitationService.Placeholders;

    public async Task<IActionResult> OnGetAsync()
    {
        var loaded = await LoadAsync();
        if (loaded is not null) return loaded;

        Subject = $"Can you work {Session.Title}?";
        Body =
            """
            <p>Hi {{VeName}},</p>
            <p>We're looking for VEs for <strong>{{SessionTitle}}</strong> on {{SessionDate}}.</p>
            <p>Join here: {{ZoomJoinUrl}}</p>
            <p>Let us know if you can make it.</p>
            <p>{{TeamName}}</p>
            """;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var loaded = await LoadAsync();
        if (loaded is not null) return loaded;

        if (SelectedVeIds.Length == 0)
        {
            TempData["ErrorMessage"] = "Choose at least one VE to invite.";
            return RedirectToPage(new { id = Id });
        }

        // Only ids this page offered. A posted id from another team's roster must not become a
        // recipient just because someone edited the form.
        var allowed = Candidates.Select(c => c.VolunteerExaminer.Id).ToHashSet();
        if (SelectedVeIds.Any(id => !allowed.Contains(id)))
        {
            return Forbid();
        }

        var user = await userManager.GetUserWithManagerAsync(dbContext, User)
            ?? throw new InvalidOperationException("No authenticated user for an [Authorize]d page.");

        var result = await invitationService.SendAsync(Id, SelectedVeIds, Subject, Body, user.Id, HttpContext.RequestAborted);

        if (result.Error is not null)
        {
            TempData["ErrorMessage"] = result.Error;
            return RedirectToPage(new { id = Id });
        }

        var message = $"Sent {result.Sent} invitation(s).";
        if (result.Failed > 0) message += $" {result.Failed} failed to send.";
        if (result.NoEmailAddress > 0) message += $" {result.NoEmailAddress} had no email address on file.";
        if (result.TextOnlySkipped > 0) message += $" {result.TextOnlySkipped} are set to text only, which isn't available yet.";

        TempData[result.Sent > 0 ? "StatusMessage" : "ErrorMessage"] = message;
        return RedirectToPage("/SessionManager/Detail", new { id = Id });
    }

    private async Task<IActionResult?> LoadAsync()
    {
        var user = await userManager.GetUserWithManagerAsync(dbContext, User)
            ?? throw new InvalidOperationException("No authenticated user for an [Authorize]d page.");

        var session = await dbContext.Sessions
            .Include(s => s.Team)
            .FirstOrDefaultAsync(s => s.Id == Id, HttpContext.RequestAborted);
        if (session is null)
        {
            return NotFound();
        }

        if (!accessScope.CanEdit(user, session))
        {
            return Forbid();
        }

        Session = session;
        Candidates = await invitationService.GetCandidatesAsync(Id, HttpContext.RequestAborted);
        return null;
    }
}
