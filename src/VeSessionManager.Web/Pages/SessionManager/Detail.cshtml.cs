using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Authorization;
using VeSessionManager.Core.CandidateActions;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Notifications;
using VeSessionManager.Core.Sessions;
using VeSessionManager.Core.VecSubmissions;
using VeSessionManager.Core.VolunteerExaminers;

namespace VeSessionManager.Web.Pages.SessionManager;

/// <summary>
/// Phase 9b's session detail — recreated from
/// design_handoff_vesessionmanager_admin_ui/session-detail.html. Every Session Manager action from
/// spec.md's Phase 9 bullet list is a named POST handler here, each a thin wrapper around the
/// relevant Core service (CandidateActionService/SessionActionService/CandidateNotificationService/
/// VolunteerExaminerRosterService/VecSubmissionService) — this page owns no business logic itself,
/// only wiring + the authorization check (SessionAccessScope.CanEdit) that Core services don't do
/// on their own since they're called from elsewhere too (e.g. background jobs have no "acting
/// user" to scope against).
/// </summary>
[Authorize(Roles = "SystemAdmin,TeamAdmin,SessionManager")]
public class DetailModel(
    AppDbContext dbContext,
    UserManager<User> userManager,
    SessionAccessScope accessScope,
    CandidateActionService candidateActionService,
    SessionActionService sessionActionService,
    CandidateNotificationService candidateNotificationService,
    VolunteerExaminerRosterService rosterService,
    VecSubmissionService vecSubmissionService) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public int Id { get; set; }

    public SessionSummary Session { get; private set; } = null!;
    public IReadOnlyList<CandidateRow> Candidates { get; private set; } = [];
    public IReadOnlyList<VeChip> VeRoster { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        var loaded = await LoadForDisplayAsync();
        return loaded ? Page() : NotFound();
    }

    // ---- Session-level actions ----

    public async Task<IActionResult> OnPostClearFlagAsync()
    {
        var auth = await AuthorizeAsync();
        if (auth is null) return Forbid();

        var result = await sessionActionService.ClearRescheduleFlagAsync(Id, auth.Value.User.Id, CancellationToken.None);
        SetStatus(result == SessionActionResult.Success, "Reschedule flag cleared.", "Could not clear reschedule flag.");
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostMarkCompletedAsync()
    {
        var auth = await AuthorizeAsync();
        if (auth is null) return Forbid();

        var result = await sessionActionService.MarkCompletedAsync(Id, auth.Value.User.Id, CancellationToken.None);
        SetStatus(result.Result == SessionActionResult.Success,
            $"Session marked completed — {result.CandidatesTested} candidate(s) tested, {result.FelonyDisclosureEmailsSent} disclosure email(s) sent.",
            "Could not mark session completed.");
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostToggleVecSubmissionAsync()
    {
        var auth = await AuthorizeAsync();
        if (auth is null) return Forbid();

        var result = await vecSubmissionService.MarkSubmittedAsync(Id, auth.Value.User.Id, CancellationToken.None);
        SetStatus(result == VecSubmissionMarkResult.Marked, "Session marked submitted to VEC.", "Session is already marked submitted.");
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostAddVeAsync(string callSign, string? name)
    {
        var auth = await AuthorizeAsync();
        if (auth is null) return Forbid();

        if (string.IsNullOrWhiteSpace(callSign))
        {
            SetStatus(false, "", "VE call sign is required.");
            return RedirectToPage(new { id = Id });
        }

        var result = await rosterService.AddAsync(Id, callSign.Trim(), name?.Trim(), auth.Value.User.Id, CancellationToken.None);
        SetStatus(result == VeRosterActionResult.Success, "VE added to roster.", "That VE is already on the roster.");
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostRemoveVeAsync(int volunteerExaminerId)
    {
        var auth = await AuthorizeAsync();
        if (auth is null) return Forbid();

        var result = await rosterService.RemoveAsync(Id, volunteerExaminerId, auth.Value.User.Id, CancellationToken.None);
        SetStatus(result == VeRosterActionResult.Success, "VE removed from roster.", "Could not remove VE from roster.");
        return RedirectToPage(new { id = Id });
    }

    // ---- Candidate-level actions ----

    public async Task<IActionResult> OnPostResendConfirmationAsync(int candidateId)
    {
        var auth = await AuthorizeAsync();
        if (auth is null) return Forbid();
        if (!await CandidateBelongsToSessionAsync(candidateId)) return Forbid();

        var result = await candidateNotificationService.ResendRegistrationConfirmationAsync(candidateId, CancellationToken.None);
        SetStatus(result == CandidateEmailSendResult.Sent, "Confirmation email resent.", $"Could not resend confirmation email: {result}.");
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostMarkFailedAsync(int candidateId)
    {
        var auth = await AuthorizeAsync();
        if (auth is null) return Forbid();
        if (!await CandidateBelongsToSessionAsync(candidateId)) return Forbid();

        var result = await candidateActionService.MarkFailedAsync(candidateId, auth.Value.User.Id, CancellationToken.None);
        SetStatus(result == CandidateActionResult.Success, "Candidate marked failed.", "Could not mark candidate failed.");
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostDeleteCandidateAsync(int candidateId)
    {
        var auth = await AuthorizeAsync();
        if (auth is null) return Forbid();
        if (!await CandidateBelongsToSessionAsync(candidateId)) return Forbid();

        var result = await candidateActionService.DeleteAsync(candidateId, auth.Value.User.Id, CancellationToken.None);
        SetStatus(result == CandidateActionResult.Success, "Candidate marked as withdrew/no-show; PII cleared.", "Could not delete candidate — testing already completed for this session.");
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostSetFrnAsync(int candidateId, string frn)
    {
        var auth = await AuthorizeAsync();
        if (auth is null) return Forbid();
        if (!await CandidateBelongsToSessionAsync(candidateId)) return Forbid();

        if (string.IsNullOrWhiteSpace(frn))
        {
            SetStatus(false, "", "FRN cannot be blank.");
            return RedirectToPage(new { id = Id });
        }

        var result = await candidateActionService.SetFrnAsync(candidateId, frn.Trim(), auth.Value.User.Id, CancellationToken.None);
        SetStatus(result == CandidateActionResult.Success, "FRN updated.", "Could not update FRN.");
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostMarkPaidAsync(int paymentId)
    {
        var auth = await AuthorizeAsync();
        if (auth is null) return Forbid();
        if (!await PaymentBelongsToSessionAsync(paymentId)) return Forbid();

        var result = await candidateActionService.MarkPaidManuallyAsync(paymentId, auth.Value.User.Id, CancellationToken.None);
        SetStatus(result == CandidateActionResult.Success, "Payment marked paid.", "Could not mark payment paid.");
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostFlagRefundAsync(int paymentId, string? notes)
    {
        var auth = await AuthorizeAsync();
        if (auth is null) return Forbid();
        if (!await PaymentBelongsToSessionAsync(paymentId)) return Forbid();

        var result = await candidateActionService.FlagRefundRequestedAsync(paymentId, auth.Value.User.Id, notes, CancellationToken.None);
        SetStatus(result == CandidateActionResult.Success, "Refund requested flagged.", "Could not flag refund requested.");
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostCreateRetestPaymentAsync(int candidateId)
    {
        var auth = await AuthorizeAsync();
        if (auth is null) return Forbid();
        if (!await CandidateBelongsToSessionAsync(candidateId)) return Forbid();

        var result = await candidateActionService.CreateRetestPaymentAsync(candidateId, auth.Value.User.Id, CancellationToken.None);
        SetStatus(result == CandidateActionResult.Success, "Retest payment created.", "Could not create retest payment — candidate must be marked Failed first.");
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostSendYouthProgramAsync(int candidateId)
    {
        var auth = await AuthorizeAsync();
        if (auth is null) return Forbid();
        if (!await CandidateBelongsToSessionAsync(candidateId)) return Forbid();

        var result = await candidateNotificationService.SendYouthProgramInstructionsAsync(candidateId, CancellationToken.None);
        SetStatus(result == CandidateEmailSendResult.Sent, "Youth program instructions sent.", $"Could not send youth program instructions: {result}.");
        return RedirectToPage(new { id = Id });
    }

    // ---- Shared plumbing ----

    private async Task<(User User, Session Session)?> AuthorizeAsync()
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return null;
        }

        var session = await dbContext.Sessions.FirstOrDefaultAsync(s => s.Id == Id);
        if (session is null || !accessScope.CanEdit(user, session))
        {
            return null;
        }

        return (user, session);
    }

    // AuthorizeAsync only proves the acting user may edit the session named by the page's own Id
    // route parameter — every candidate/payment action also submits a separate candidateId/paymentId
    // form value that must independently be checked to actually belong to that session. Without
    // this, an authorized Session Manager for one session could act on any candidate/payment id in
    // the whole database (cross-tenant IDOR) just by editing the posted form value.
    private Task<bool> CandidateBelongsToSessionAsync(int candidateId) =>
        dbContext.Candidates.AnyAsync(c => c.Id == candidateId && c.SessionId == Id);

    private Task<bool> PaymentBelongsToSessionAsync(int paymentId) =>
        dbContext.Payments.AnyAsync(p => p.Id == paymentId && p.Candidate.SessionId == Id);

    private void SetStatus(bool success, string successMessage, string errorMessage)
    {
        if (success)
        {
            TempData["StatusMessage"] = successMessage;
        }
        else
        {
            TempData["ErrorMessage"] = errorMessage;
        }
    }

    private async Task<bool> LoadForDisplayAsync()
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return false;
        }

        var session = await dbContext.Sessions
            .Include(s => s.Vec)
            .Include(s => s.Team)
            .Include(s => s.FeeConfiguration)
            .Include(s => s.Candidates).ThenInclude(c => c.Payments)
            .Include(s => s.SessionVolunteerExaminers).ThenInclude(l => l.VolunteerExaminer)
            .FirstOrDefaultAsync(s => s.Id == Id);

        if (session is null || !accessScope.CanEdit(user, session))
        {
            return false;
        }

        var discordEventUrl = session.DiscordEventId is not null && session.Team.DiscordGuildId is not (null or 0)
            ? $"https://discord.com/events/{session.Team.DiscordGuildId}/{session.DiscordEventId}"
            : null;

        var feeLine = session.FeeConfiguration.FeeCollectionEnabled
            ? $"${session.FeeConfiguration.ExamFeeAmount:F2} exam · ${session.FeeConfiguration.RetainedAmount:F2} retained"
            : "No fee collected";

        Session = new SessionSummary(
            session.Id,
            session.ExamToolsSessionId,
            $"Session — {session.ScheduledStartUtc.ToString("ddd, MMM d, yyyy · h:mm tt", CultureInfo.InvariantCulture)}",
            session.Vec.Name,
            session.ZoomJoinUrl,
            discordEventUrl,
            feeLine,
            session.TestingCompletedUtc is null ? "Not yet completed" : $"Completed {session.TestingCompletedUtc:MMM d, yyyy}",
            session.VecSubmissionStatus == VecSubmissionStatus.Submitted ? "chip-green" : "chip-neutral",
            session.VecSubmissionStatus == VecSubmissionStatus.Submitted ? "Submitted" : "Not submitted",
            session.VecSubmissionStatus == VecSubmissionStatus.Submitted,
            session.RescheduleFlaggedForReview,
            session.TestingCompletedUtc is not null,
            session.Status == SessionStatus.Cancelled);

        Candidates = session.Candidates
            .OrderBy(c => c.Name)
            .Select(ToRow)
            .ToList();

        VeRoster = session.SessionVolunteerExaminers
            .OrderBy(l => l.VolunteerExaminer.CallSign)
            .Select(l => new VeChip(l.VolunteerExaminer.Id, l.VolunteerExaminer.CallSign ?? "—", l.VolunteerExaminer.Name))
            .ToList();

        return true;
    }

    private static CandidateRow ToRow(Candidate candidate)
    {
        var isWithdrawn = candidate.ApplicationStatus == CandidateApplicationStatus.NotTested;
        var primaryPayment = candidate.Payments.OrderByDescending(p => p.CreatedUtc).FirstOrDefault(p => p.Status == PaymentStatus.Unpaid)
            ?? candidate.Payments.OrderByDescending(p => p.CreatedUtc).FirstOrDefault();

        var (paymentClass, paymentLabel) = primaryPayment is null
            ? ("chip-neutral", "No payment")
            : primaryPayment.Status switch
            {
                PaymentStatus.Paid => ("chip-green", "Paid"),
                PaymentStatus.Unpaid => ("chip-amber", "Unpaid"),
                _ => ("chip-neutral", "Not applicable")
            };

        var meterSegments = candidate.ApplicationStatus switch
        {
            CandidateApplicationStatus.Received => new[] { "on-a", "", "" },
            CandidateApplicationStatus.Granted => new[] { "on-a", "on-g", "on-g" },
            CandidateApplicationStatus.Failed => new[] { "on-r", "", "" },
            CandidateApplicationStatus.NotTested => new[] { "off-dim", "off-dim", "off-dim" },
            _ => new[] { "", "", "" }
        };

        var statusLabel = candidate.ApplicationStatus switch
        {
            CandidateApplicationStatus.NotTested => "Not tested",
            var s => s.ToString()
        };

        var frnLine = isWithdrawn
            ? "record retained for stats"
            : candidate.Frn is not null
                ? $"FRN {candidate.Frn}"
                : candidate.FrnMissingAtRegistration
                    ? "FRN missing at registration"
                    : "No FRN on file";

        return new CandidateRow(
            candidate.Id,
            isWithdrawn,
            isWithdrawn ? "Withdrew — PII cleared" : candidate.Name ?? "—",
            isWithdrawn ? "—" : candidate.CallSign ?? "—",
            frnLine,
            meterSegments,
            statusLabel,
            paymentClass,
            paymentLabel,
            primaryPayment?.RefundRequested ?? false,
            candidate.Tested,
            !isWithdrawn && candidate.Email is not null,
            !isWithdrawn && primaryPayment is { Status: PaymentStatus.Unpaid },
            !isWithdrawn && candidate.ApplicationStatus is CandidateApplicationStatus.Unmatched or CandidateApplicationStatus.Received,
            !isWithdrawn && candidate.ApplicationStatus == CandidateApplicationStatus.Failed,
            !isWithdrawn && primaryPayment is not null,
            !isWithdrawn,
            !isWithdrawn && !candidate.Tested,
            primaryPayment?.Id);
    }

    public record SessionSummary(
        int Id,
        string ExamToolsSessionId,
        string Heading,
        string VecName,
        string? ZoomJoinUrl,
        string? DiscordEventUrl,
        string FeeLine,
        string TestingStatusLine,
        string VecSubmissionChipClass,
        string VecSubmissionChipLabel,
        bool VecSubmitted,
        bool RescheduleFlagged,
        bool TestingCompleted,
        bool Cancelled);

    public record CandidateRow(
        int Id,
        bool IsWithdrawn,
        string DisplayName,
        string CallSignOrDash,
        string FrnLine,
        string[] MeterSegments,
        string StatusLabel,
        string PaymentChipClass,
        string PaymentChipLabel,
        bool RefundRequested,
        bool Tested,
        bool CanResendConfirmation,
        bool CanMarkPaid,
        bool CanMarkFailed,
        bool CanCreateRetestPayment,
        bool CanFlagRefund,
        bool CanSendYouthProgram,
        bool CanDelete,
        int? PrimaryPaymentId);

    public record VeChip(int Id, string CallSign, string Name);
}
