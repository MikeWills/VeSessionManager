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
/// Composing one email and sending it to candidates chosen on a session (#144).
///
/// <para>Reached from Session Detail and gated on the same <c>SessionAccessScope.CanEdit</c> as the
/// rest of that page's actions — a Session Manager running the session is exactly who writes to its
/// candidates, so this is deliberately not admin-only.</para>
///
/// <para><b>The draft starts from a template and is not written back to it.</b> That is the whole
/// difference from Admin → Email Templates: there you maintain the team's standard text, here you
/// take a copy and adjust it for these people, today. Switching templates replaces the draft, which
/// the page says out loud rather than trying to merge two sources of text.</para>
/// </summary>
[Authorize(Roles = RoleGroups.SessionStaff)]
public class CandidateEmailModel(
    AppDbContext dbContext,
    UserManager<User> userManager,
    SessionAccessScope accessScope,
    CandidateNotificationService notificationService) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public int Id { get; set; }

    /// <summary>
    /// Which stored template the draft was taken from. Empty means a blank draft written from
    /// scratch — the case no template anticipated, which is half of why the picker exists.
    /// </summary>
    [BindProperty(SupportsGet = true, Name = "message")]
    public int SelectedMessageId { get; set; }

    [BindProperty]
    public string Subject { get; set; } = "";

    [BindProperty]
    public string Body { get; set; } = "";

    [BindProperty]
    public int[] SelectedCandidateIds { get; set; } = [];

    public Session Session { get; private set; } = null!;
    public IReadOnlyList<Recipient> Candidates { get; private set; } = [];
    public IReadOnlyList<TemplateChoice> Templates { get; private set; } = [];

    /// <summary>What the compose screen offers as insertable chips — the same list the registry advertises for this key.</summary>
    public static IReadOnlyList<string> Placeholders => CandidatePlaceholderValues.Names;

    /// <summary>Blank for most of a session's candidates most of the time — see <see cref="CallSignWarningCount"/>.</summary>
    public const string CallSignPlaceholder = "{{CallSign}}";

    // Which messages can start one lives in ComposableMessages — the session's Email
    // candidates menu offers the same list as shortcuts, and two copies would drift (#394 follow-up).

    /// <param name="LastSentDisplay">When this candidate last had the chosen template, or null. What makes a second pass over a session possible without sending twice.</param>
    /// <param name="CanReceive">From <see cref="CandidateCapabilities"/>, not computed here — that is the one home for "is this action applicable to this candidate" (#274).</param>
    public record Recipient(int Id, string Name, string? Email, string? CallSign, bool IsWithdrawn, string? LastSentDisplay, bool CanReceive);

    public record TemplateChoice(int Id, string Label);

    /// <summary>
    /// How many chosen-but-reachable candidates have no call sign yet, when the draft uses the token.
    /// A new licensee's call sign arrives from the FCC days after the session, so an empty
    /// <c>{{CallSign}}</c> is the normal case here rather than an edge one — worth stopping to read,
    /// like the missing-Zoom warning on the VE invitation screen.
    /// </summary>
    public int CallSignWarningCount =>
        Body.Contains(CallSignPlaceholder, StringComparison.Ordinal) || Subject.Contains(CallSignPlaceholder, StringComparison.Ordinal)
            ? Candidates.Count(c => c.CanReceive && string.IsNullOrWhiteSpace(c.CallSign))
            : 0;

    public async Task<IActionResult> OnGetAsync()
    {
        var loaded = await LoadAsync();
        if (loaded is not null) return loaded;

        // Read at open time, never cached: an edit made in Admin → Email Templates is meant to take
        // effect on the next send, not the next deploy.
        var source = SelectedMessageId == 0
            ? null
            : await dbContext.MessageRules
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.TeamId == Session.TeamId && r.Id == SelectedMessageId
                    && r.Trigger == MessageTrigger.ManualToCandidate, HttpContext.RequestAborted);

        // Starting text, copied. Editing the draft never reaches back and changes the message
        // somebody else starts from.
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
            return RedirectToPage(new { id = Id, message = SelectedMessageId });
        }

        // Only ids this page offered. A posted id from another session must not become a recipient
        // because someone edited the form — the service re-scopes independently, and both are
        // deliberate (#238).
        var offered = Candidates.Select(c => c.Id).ToHashSet();
        if (SelectedCandidateIds.Any(id => !offered.Contains(id)))
        {
            return Forbid();
        }

        var user = await userManager.GetRequiredUserAsync(dbContext, User);
        var label = Templates.FirstOrDefault(t => t.Id == SelectedMessageId)?.Label ?? CustomMessageLabel;

        var result = await notificationService.SendComposedAsync(
            Id, SelectedCandidateIds, Subject, Body, label, user.Id, HttpContext.RequestAborted);

        if (result.Error is not null)
        {
            TempData["ErrorMessage"] = result.Error;
            return RedirectToPage(new { id = Id, message = SelectedMessageId });
        }

        var message = $"Sent {result.Sent} email(s).";
        if (result.Failed > 0) message += $" {result.Failed} failed to send.";
        if (result.NoEmailAddress > 0) message += $" {result.NoEmailAddress} had no email address on file.";
        if (result.NotOnSession > 0) message += $" {result.NotOnSession} are no longer on this session.";

        TempData[result.Sent > 0 ? "StatusMessage" : "ErrorMessage"] = message;
        return RedirectToPage("/SessionManager/Detail", new { id = Id });
    }

    /// <summary>What the history records for a draft written from scratch. A label, not a key — nothing sends it, so there is nothing to look it up by.</summary>
    public const string CustomMessageLabel = "Custom message";

    private async Task<IActionResult?> LoadAsync()
    {
        var user = await userManager.GetUserWithManagerAsync(dbContext, User);
        if (user is null)
        {
            return Forbid();
        }

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

        Templates = [.. (await ComposableMessages.LoadAsync(
            dbContext, session.TeamId, MessageTrigger.ManualToCandidate, HttpContext.RequestAborted))
            .Select(c => new TemplateChoice(c.Id, c.Label))];

        var candidates = await dbContext.Candidates
            .Where(c => c.SessionId == Id)
            .OrderBy(c => c.Name)
            .ToListAsync(HttpContext.RequestAborted);

        // One query for the whole roster rather than one per candidate. Only the chosen template's
        // sends count: "already had one" is a question about this message, not about email in general.
        // Read off Templates, not EmailTemplateLabels: a team-defined template's name lives on its own
        // row, and the registry fallback would answer with the generated key instead.
        var label = Templates.FirstOrDefault(t => t.Id == SelectedMessageId)?.Label ?? CustomMessageLabel;
        var lastSent = await dbContext.CandidateEmailSends
            .Where(s => s.Candidate.SessionId == Id && s.TemplateLabel == label)
            .GroupBy(s => s.CandidateId)
            .Select(g => new { CandidateId = g.Key, SentUtc = g.Max(s => s.SentUtc) })
            .ToListAsync(HttpContext.RequestAborted);
        var lastSentByCandidate = lastSent.ToDictionary(x => x.CandidateId, x => x.SentUtc);

        Candidates = [.. candidates.Select(c => new Recipient(
            c.Id,
            CandidatePresentation.DisplayName(c),
            c.Email,
            c.CallSign,
            c.IsWithdrawn,
            lastSentByCandidate.TryGetValue(c.Id, out var sentUtc)
                ? EasternTimeFormatter.Format(sentUtc, "MMM d")
                : null,
            // Only CanReceiveEmail is read here; the two arguments feed CanSendYouthProgram and
            // CanFlagRefund, which this screen does not offer. Passing false rather than querying for
            // answers nothing consults — the flags they affect are not read on this page.
            CandidateCapabilities.For(c, vecSupportsYouthProgram: false, hasAnyPayment: false).CanReceiveEmail))];

        return null;
    }
}
