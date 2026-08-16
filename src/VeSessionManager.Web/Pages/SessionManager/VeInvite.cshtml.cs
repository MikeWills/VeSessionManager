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
[Authorize(Roles = RoleGroups.SessionStaff)]
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

    /// <summary>Tags actually in use on this list, for the filter. Built from the candidates rather than the team's whole vocabulary, so the dropdown never offers a tag that would match nothing.</summary>
    public IReadOnlyList<string> TagNames { get; private set; } = [];

    public static IReadOnlyList<string> Placeholders => VeSessionInvitationService.Placeholders;

    /// <summary>
    /// Sentinel for "no tags at all", used as the <c>&lt;option value&gt;</c> of the tag filter's
    /// "Untagged" entry. A real tag can never collide with it: <c>CreateTagAsync</c>/<c>UpdateTagAsync</c>
    /// <c>Trim()</c> the name and reject it when blank, so no stored tag can begin with whitespace.
    ///
    /// <para><b>The leading character is a space, and must stay one — it was a literal U+0000 until
    /// 2026-08-11 (issue #300) and that silently broke this filter.</b> An HTML parser replaces
    /// U+0000 with U+FFFD, and does so whether it arrives raw or as <c>&amp;#x0;</c>, so the value
    /// JavaScript read back was never the value C# emitted. The equality test in <c>app.js</c>
    /// always failed and fell through to "find a tag literally named this", which matches nothing —
    /// so choosing "Untagged" hid every VE instead of showing the untagged ones. Verified against a
    /// real browser rather than reasoned about; a unit test could not have caught it, because the
    /// mangling happens in the parser.</para>
    ///
    /// <para>The literal NUL also made this file <b>binary to ripgrep</b>, so every search silently
    /// skipped it — which is how the bug survived, and how a code review nearly deleted the DI
    /// registration for <c>VeSessionInvitationService</c> on the evidence that nothing referenced it.
    /// <c>NoNulBytesInSourceTests</c> now fails the build if a NUL reappears anywhere under src/.</para>
    ///
    /// <para><b>Keep this in sync with <c>UNTAGGED</c> in <c>wwwroot/js/app.js</c>.</b> Two copies of
    /// one constant with no compiler tying them together; the comment there says the same.</para>
    /// </summary>
    public const string UntaggedFilterValue = " untagged";

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

        var user = await userManager.GetRequiredUserAsync(dbContext, User);

        var result = await invitationService.SendAsync(Id, SelectedVeIds, Subject, Body, user.Id, HttpContext.RequestAborted);

        if (result.Error is not null)
        {
            TempData["ErrorMessage"] = result.Error;
            return RedirectToPage(new { id = Id });
        }

        var message = $"Sent {result.Sent} invitation(s).";
        if (result.Failed > 0) message += $" {result.Failed} failed to send.";
        if (result.NoEmailAddress > 0) message += $" {result.NoEmailAddress} had no email address on file.";
        if (result.Unsubscribed > 0) message += $" {result.Unsubscribed} have unsubscribed from email and were not invited.";
        if (result.TextOnlySkipped > 0) message += $" {result.TextOnlySkipped} are set to text only, which isn't available yet.";

        TempData[result.Sent > 0 ? "StatusMessage" : "ErrorMessage"] = message;
        return RedirectToPage("/SessionManager/Detail", new { id = Id });
    }

    private async Task<IActionResult?> LoadAsync()
    {
        var user = await userManager.GetRequiredUserAsync(dbContext, User);

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
        TagNames = [.. Candidates.SelectMany(c => c.Tags).Distinct().OrderBy(n => n)];
        return null;
    }
}
