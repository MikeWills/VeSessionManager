using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Authorization;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Email;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Notifications;

namespace VeSessionManager.Web.Pages.SessionManager;

/// <summary>
/// Composing one email and sending it to some or all of a team's candidates still waiting on an FCC
/// grant (2026-08-26) — Applicant Status's own worklist, with a compose screen of its own. Built for
/// a real FCC-wide processing stall: reminders can be suppressed team-wide via
/// <c>Admin/FccStatus</c>'s switches, and this is how a human still tells some or all of those people
/// what's going on.
///
/// <para><b>Same shape as <see cref="CandidateEmailModel"/>, scoped by team instead of session</b> — a
/// template picker, an editable draft, checkboxes over the actual recipients. The one real difference
/// is the recipient population: <see cref="CandidateApplicationStatusExtensions.AwaitingFccGrant"/>
/// across every session on the team, not one session's roster.</para>
///
/// <para><b>Requires a specific team, not "All teams."</b> A message has to go out through some team's
/// own SMTP credentials, so Applicant Status only offers this screen once a specific team is picked —
/// see that page's own note.</para>
/// </summary>
[Authorize(Roles = RoleGroups.SessionStaff)]
public class ApplicantStatusEmailModel(
    AppDbContext dbContext,
    UserManager<User> userManager,
    SessionAccessScope accessScope,
    CandidateNotificationService notificationService) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public int? TeamId { get; set; }

    /// <summary>Which stored template the draft was taken from. Empty means a blank draft.</summary>
    [BindProperty(SupportsGet = true, Name = "message")]
    public int SelectedMessageId { get; set; }

    [BindProperty]
    public string Subject { get; set; } = "";

    [BindProperty]
    public string Body { get; set; } = "";

    [BindProperty]
    public int[] SelectedCandidateIds { get; set; } = [];

    public string TeamName { get; private set; } = "";
    public IReadOnlyList<CandidateEmailModel.Recipient> Candidates { get; private set; } = [];
    public IReadOnlyList<CandidateEmailModel.TemplateChoice> Templates { get; private set; } = [];

    /// <summary>Same insertable-chip list the session-scoped compose screen offers.</summary>
    public static IReadOnlyList<string> Placeholders => CandidatePlaceholderValues.Names;

    /// <summary>Same warning as <see cref="CandidateEmailModel.CallSignWarningCount"/>, and for the
    /// same reason — a new licensee's call sign hasn't arrived from the FCC yet, which is the exact
    /// population this screen is built around.</summary>
    public int CallSignWarningCount =>
        Body.Contains(CandidateEmailModel.CallSignPlaceholder, StringComparison.Ordinal) || Subject.Contains(CandidateEmailModel.CallSignPlaceholder, StringComparison.Ordinal)
            ? Candidates.Count(c => c.CanReceive && string.IsNullOrWhiteSpace(c.CallSign))
            : 0;

    public async Task<IActionResult> OnGetAsync()
    {
        var loaded = await LoadAsync();
        if (loaded is not null) return loaded;

        var source = SelectedMessageId == 0
            ? null
            : await dbContext.MessageRules
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.TeamId == TeamId && r.Id == SelectedMessageId
                    && r.Trigger == MessageTrigger.ManualToCandidate, HttpContext.RequestAborted);

        Subject = source?.Subject ?? "";
        Body = source?.Body ?? "";
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var loaded = await LoadAsync();
        if (loaded is not null) return loaded;

        if (SelectedCandidateIds.Length == 0)
        {
            TempData["ErrorMessage"] = "Choose at least one candidate to email.";
            return RedirectToPage(new { teamId = TeamId, message = SelectedMessageId });
        }

        // Only ids this page actually offered — the service re-derives recipients independently, and
        // both checks are deliberate (same reasoning as CandidateEmailModel, #238).
        var offered = Candidates.Select(c => c.Id).ToHashSet();
        if (SelectedCandidateIds.Any(id => !offered.Contains(id)))
        {
            return Forbid();
        }

        var user = await userManager.GetRequiredUserAsync(dbContext, User);
        var label = Templates.FirstOrDefault(t => t.Id == SelectedMessageId)?.Label ?? CandidateEmailModel.CustomMessageLabel;

        var result = await notificationService.SendComposedToPendingCandidatesAsync(
            TeamId!.Value, SelectedCandidateIds, Subject, Body, label, user.Id, HttpContext.RequestAborted);

        if (result.Error is not null)
        {
            TempData["ErrorMessage"] = result.Error;
            return RedirectToPage(new { teamId = TeamId, message = SelectedMessageId });
        }

        var message = $"Sent {result.Sent} email(s).";
        if (result.Failed > 0) message += $" {result.Failed} failed to send.";
        if (result.NoEmailAddress > 0) message += $" {result.NoEmailAddress} had no email address on file.";
        if (result.NotOnSession > 0) message += $" {result.NotOnSession} are no longer pending an FCC grant on this team.";

        TempData[result.Sent > 0 ? "StatusMessage" : "ErrorMessage"] = message;
        return RedirectToPage("/SessionManager/ApplicantStatus", new { teamId = TeamId });
    }

    private async Task<IActionResult?> LoadAsync()
    {
        var user = await userManager.GetUserWithManagerAsync(dbContext, User);
        if (user is null)
        {
            return Forbid();
        }

        if (TeamId is not { } teamId)
        {
            return RedirectToPage("/SessionManager/ApplicantStatus");
        }

        var team = await dbContext.Teams.FirstOrDefaultAsync(t => t.Id == teamId, HttpContext.RequestAborted);
        if (team is null)
        {
            return NotFound();
        }

        // Write-safe team check, same shape as AdminAccessScope.TryResolveManageableTeamIdForWrite's
        // own reasoning: not manageable means no, never "have a different one instead" — this is an
        // action, not a merged list, so a tampered teamId must be refused rather than silently
        // redirected to the user's own team.
        if (user.Role != UserRole.SystemAdmin)
        {
            var effectiveTeamIds = accessScope.GetEffectiveTeamIds(user) ?? [];
            if (!effectiveTeamIds.Contains(teamId))
            {
                return Forbid();
            }
        }

        TeamName = team.Name;

        Templates = [.. (await ComposableMessages.LoadAsync(
            dbContext, teamId, MessageTrigger.ManualToCandidate, HttpContext.RequestAborted))
            .Select(c => new CandidateEmailModel.TemplateChoice(c.Id, c.Label))];

        var candidates = await dbContext.Candidates
            .Include(c => c.Session)
            .Where(c => c.Session.TeamId == teamId)
            .AwaitingFccGrant()
            .OrderBy(c => c.Session.ScheduledStartUtc)
            .ThenBy(c => c.Name)
            .ToListAsync(HttpContext.RequestAborted);

        var label = Templates.FirstOrDefault(t => t.Id == SelectedMessageId)?.Label ?? CandidateEmailModel.CustomMessageLabel;
        var candidateIds = candidates.Select(c => c.Id).ToList();
        var lastSent = await dbContext.CandidateEmailSends
            .Where(s => candidateIds.Contains(s.CandidateId) && s.TemplateLabel == label)
            .GroupBy(s => s.CandidateId)
            .Select(g => new { CandidateId = g.Key, SentUtc = g.Max(s => s.SentUtc) })
            .ToListAsync(HttpContext.RequestAborted);
        var lastSentByCandidate = lastSent.ToDictionary(x => x.CandidateId, x => x.SentUtc);

        Candidates = [.. candidates.Select(c => new CandidateEmailModel.Recipient(
            c.Id,
            CandidatePresentation.DisplayName(c),
            c.Email,
            c.CallSign,
            c.IsWithdrawn,
            lastSentByCandidate.TryGetValue(c.Id, out var sentUtc)
                ? EasternTimeFormatter.Format(sentUtc, "MMM d")
                : null,
            CandidateCapabilities.For(c, vecSupportsYouthProgram: false, hasAnyPayment: false).CanReceiveEmail))];

        return null;
    }
}
