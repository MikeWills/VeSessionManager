using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Authorization;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.VecSubmissions;

namespace VeSessionManager.Web.Pages.SessionManager;

/// <summary>
/// The ARRL submission screen (issue #197) — the complete form, filled in, read by a human, and only
/// then sent.
///
/// <para><b>This is the only route to a submission, and that is the safeguard.</b> The issue asks for
/// a preview and an explicit confirmation as two separate guards; making the preview the only way to
/// reach the POST collapses them into one nobody can forget to use. There is no sandbox on ARRL's
/// side, so this screen is the entire feedback loop that would normally come from testing.</para>
///
/// <para><b>Every field is editable.</b> The team configuration and the derived values are prefill,
/// not a locked payload — what is on screen is what gets sent, which is also why the submission
/// record stores the posted values rather than re-deriving them later.</para>
///
/// <para>Reached as a GET from session detail for an ARRL session; every other VEC keeps the plain
/// "I filed this by hand" toggle.</para>
/// </summary>
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Authorize(Roles = RoleGroups.AllRoles)]
public class SubmitToVecModel(
    AppDbContext dbContext,
    UserManager<User> userManager,
    SessionAccessScope accessScope,
    ArrlSubmissionPreviewService previewService,
    ArrlSubmissionService submissionService) : PageModel
{
    /// <summary>ARRL's own list, and the browser hint on the file input. Enforced server-side too — the accept attribute is a convenience, not a check.</summary>
    private static readonly string[] AllowedAttachmentExtensions = [".pdf", ".doc", ".docx", ".json", ".zip"];

    /// <summary>ARRL's stated limit for one upload.</summary>
    private const long MaxAttachmentBytes = 40L * 1024 * 1024;

    [BindProperty(SupportsGet = true)]
    public int Id { get; set; }

    public ArrlSubmissionPreview? Preview { get; private set; }

    /// <summary>False for a TeamLead, who may read this page but takes no action on it — same split as session detail.</summary>
    public bool CanEdit { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        // GetUserWithManagerAsync, never the bare GetUserAsync: the scope classes read user.UserTeams,
        // which the bare load leaves empty — see CLAUDE.md's Known Constraints.
        var user = await userManager.GetUserWithManagerAsync(dbContext, User);
        if (user is null)
        {
            return Forbid();
        }

        var session = await dbContext.Sessions.FirstOrDefaultAsync(s => s.Id == Id);
        if (session is null || !accessScope.CanView(user, session))
        {
            return NotFound();
        }

        CanEdit = accessScope.CanEdit(user, session);
        Preview = await previewService.BuildAsync(Id, HttpContext.RequestAborted);
        return Page();
    }

    /// <summary>
    /// Files the session with ARRL. <b>Irreversible</b> — there is no unsend, and the rollback story
    /// is a phone call.
    ///
    /// <para>Everything posted here is re-resolved server-side: the session, the caller's permission,
    /// and the archive itself. The form's own values are trusted only because a human read them on the
    /// screen that produced them.</para>
    /// </summary>
    public async Task<IActionResult> OnPostSubmitAsync(
        string fullName, string callSign, string email, string phone,
        string sessionDate, string location, ArrlPaymentMethod paymentMethod,
        string amountCharged, string? note, IFormFile? attachment)
    {
        var user = await userManager.GetUserWithManagerAsync(dbContext, User);
        if (user is null)
        {
            return Forbid();
        }

        var session = await dbContext.Sessions.Include(s => s.Vec).FirstOrDefaultAsync(s => s.Id == Id);
        if (session is null)
        {
            return NotFound();
        }

        // Re-checked here rather than relying on the GET having hidden the button (#238's lesson):
        // hiding a control is presentation, never authorization.
        if (!accessScope.CanEdit(user, session))
        {
            return Forbid();
        }

        // And re-checked here too: one submitter, no fallback. A tampered id must not hand another
        // VEC's session to ARRL.
        if (!string.Equals(session.Vec.MatchCode, ArrlSubmissionPreviewService.ArrlMatchCode, StringComparison.OrdinalIgnoreCase))
        {
            return Forbid();
        }

        var archive = await previewService.FetchArchiveFileAsync(Id, HttpContext.RequestAborted);
        if (archive is null)
        {
            TempData["ErrorMessage"] = "The VEC archive could not be downloaded from ExamTools, so nothing was sent.";
            return RedirectToPage(new { id = Id });
        }

        var attachmentFile = await ReadAttachmentAsync();
        if (attachmentFile is null && attachment is { Length: > 0 })
        {
            return RedirectToPage(new { id = Id });
        }

        var fields = new ArrlSubmissionFieldValues
        {
            FullName = fullName?.Trim() ?? "",
            CallSign = callSign?.Trim() ?? "",
            Email = email?.Trim() ?? "",
            Phone = phone?.Trim() ?? "",
            SessionDate = sessionDate?.Trim() ?? "",
            Location = location?.Trim() ?? "",
            PaymentMethod = paymentMethod,
            AmountCharged = amountCharged?.Trim() ?? "",
            Note = note
        };

        var result = await submissionService.SubmitAsync(Id, fields, archive, attachmentFile, user.Id, HttpContext.RequestAborted);

        switch (result)
        {
            case ArrlSubmitResult.Succeeded:
                TempData["StatusMessage"] = "Filed with ARRL-VEC. Their confirmation is kept with the session.";
                return RedirectToPage("Detail", new { id = Id });

            // Not phrased as a failure, because it is not one: the submission may well have landed,
            // and telling someone it failed is what would produce a duplicate filing.
            case ArrlSubmitResult.Unconfirmed:
                TempData["ErrorMessage"] =
                    "This was sent to ARRL, but their confirmation could not be read — so it may or may not have "
                    + "been filed. Check with ARRL before sending it again; the app will not send it twice.";
                return RedirectToPage("Detail", new { id = Id });

            case ArrlSubmitResult.AlreadySubmitted:
            case ArrlSubmitResult.AlreadyAttempted:
                TempData["ErrorMessage"] = "This session has already been sent to ARRL. Nothing was sent again.";
                return RedirectToPage("Detail", new { id = Id });

            case ArrlSubmitResult.NotConfigured:
                TempData["ErrorMessage"] = "This deployment has no ARRL upload address configured, so nothing was sent.";
                return RedirectToPage(new { id = Id });

            default:
                TempData["ErrorMessage"] = "That session could not be found.";
                return RedirectToPage(new { id = Id });
        }

        async Task<ArrlSubmissionFile?> ReadAttachmentAsync()
        {
            if (attachment is null || attachment.Length == 0)
            {
                return null;
            }

            // Extension, not the browser's content type, which is trivially wrong and trivially forged.
            var extension = Path.GetExtension(attachment.FileName);
            if (!AllowedAttachmentExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                TempData["ErrorMessage"] = "ARRL accepts PDF, DOC, DOCX, JSON and ZIP files only. Nothing was sent.";
                return null;
            }

            if (attachment.Length > MaxAttachmentBytes)
            {
                TempData["ErrorMessage"] = "That file is larger than ARRL's 40MB limit. Nothing was sent.";
                return null;
            }

            using var buffer = new MemoryStream();
            await attachment.CopyToAsync(buffer, HttpContext.RequestAborted);

            // Path.GetFileName strips any directory the browser sent — the name travels to ARRL and
            // into the archive's own filename, and neither should ever take a path from a client.
            return new ArrlSubmissionFile(Path.GetFileName(attachment.FileName), buffer.ToArray());
        }
    }
}
