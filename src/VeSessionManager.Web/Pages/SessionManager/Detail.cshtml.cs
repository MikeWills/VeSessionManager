using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Authorization;
using VeSessionManager.Core.CandidateActions;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Ingestion;
using VeSessionManager.Core.Notifications;
using VeSessionManager.Core.Sessions;
using VeSessionManager.Core.VolunteerExaminers;
using VeSessionManager.Core.VecSubmissions;

namespace VeSessionManager.Web.Pages.SessionManager;

/// <summary>
/// Phase 9b's session detail — recreated from
/// design_handoff_vesessionmanager_admin_ui/session-detail.html. Every Session Manager action from
/// spec.md's Phase 9 bullet list is a named POST handler here, each a thin wrapper around the
/// relevant Core service (CandidateActionService/SessionActionService/CandidateNotificationService/
/// VecSubmissionService) — this page owns no business logic itself,
/// only wiring + the authorization check (SessionAccessScope.CanEdit) that Core services don't do
/// on their own since they're called from elsewhere too (e.g. background jobs have no "acting
/// user" to scope against).
///
/// TeamLead access (see docs/admin-auth.md): the page-load gate uses
/// SessionAccessScope.CanView (not CanEdit) so a TeamLead can actually see the page — CanEdit is
/// always false for TeamLead by design. Every POST handler still gates on CanEdit via
/// AuthorizeAsync() below, unchanged, so TeamLead is denied server-side regardless of the UI; the
/// CanEdit property exposed here is only so the Razor view can hide write controls instead of
/// showing a TeamLead a page full of buttons that 403 when clicked.
/// </summary>
[Authorize(Roles = "SystemAdmin,TeamAdmin,SessionManager,TeamLead")]
public class DetailModel(
    AppDbContext dbContext,
    UserManager<User> userManager,
    SessionAccessScope accessScope,
    AdminAccessScope adminAccessScope,
    CandidateActionService candidateActionService,
    SessionActionService sessionActionService,
    CandidateNotificationService candidateNotificationService,
    VecSubmissionService vecSubmissionService,
    ManualCandidateRefreshService manualRefreshService) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public int Id { get; set; }

    public SessionSummary Session { get; private set; } = null!;
    /// <summary>Candidates still on this session. Withdrawn ones are held separately — see <see cref="WithdrawnCandidates"/>.</summary>
    public IReadOnlyList<CandidateRow> Candidates { get; private set; } = [];

    /// <summary>
    /// Candidates who left this session — moved to another one in ExamTools, or withdrawn. They keep
    /// a row for statistics, but their PII has been cleared, so all the roster can show is "Withdrew —
    /// PII cleared": a nameless entry that read as clutter mixed in with real candidates, and inflated
    /// the roster count (reported 2026-08-06).
    /// </summary>
    public IReadOnlyList<CandidateRow> WithdrawnCandidates { get; private set; } = [];

    /// <summary>Every candidate row on this session, withdrawn included — what deleting the session would actually remove.</summary>
    public int TotalCandidateCount => Candidates.Count + WithdrawnCandidates.Count;
    public IReadOnlyList<VeChip> VeRoster { get; private set; } = [];
    public bool CanEdit { get; private set; }

    /// <summary>TeamAdmin/SystemAdmin-only, not a Session Manager action — see AdminAccessScope.CanManageTeam. Gates the "Delete session" control separately from CanEdit.</summary>
    public bool CanDeleteSession { get; private set; }

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

    /// <summary>
    /// Blank overrideAmount clears back to the fee schedule's per-candidate default. A non-blank
    /// value must parse as a non-negative decimal — SessionActionService itself trusts the caller to
    /// have already validated this, same division of responsibility as OnPostSetFrnAsync's blank-check.
    /// </summary>
    public async Task<IActionResult> OnPostSetRetainedAmountOverrideAsync(string? overrideAmount)
    {
        var auth = await AuthorizeAsync();
        if (auth is null) return Forbid();

        decimal? parsedAmount = null;
        if (!string.IsNullOrWhiteSpace(overrideAmount))
        {
            if (!decimal.TryParse(overrideAmount, out var value) || value < 0)
            {
                SetStatus(false, "", "Retained amount must be a non-negative dollar amount.");
                return RedirectToPage(new { id = Id });
            }

            parsedAmount = value;
        }

        var result = await sessionActionService.SetRetainedAmountOverrideAsync(Id, parsedAmount, auth.Value.User.Id, CancellationToken.None);
        SetStatus(result == SessionActionResult.Success,
            parsedAmount is null ? "Retained amount override cleared." : $"Retained amount overridden to ${parsedAmount:F2} for this session.",
            "Could not update retained amount override.");
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostToggleVecSubmissionAsync()
    {
        var auth = await AuthorizeAsync();
        if (auth is null) return Forbid();

        var result = await vecSubmissionService.MarkSubmittedAsync(Id, auth.Value.User.Id, CancellationToken.None);
        // Was a two-outcome SetStatus, so a SessionNotFound reported "already marked submitted" —
        // telling the user the opposite of what happened.
        SetStatus(result == VecSubmissionMarkResult.Marked, "Session marked submitted to VEC.",
            result == VecSubmissionMarkResult.AlreadySubmitted
                ? "Session is already marked submitted."
                : "Session not found.");
        return RedirectToPage(new { id = Id });
    }

    /// <summary>
    /// TeamAdmin/SystemAdmin-only destructive cleanup action (see docs/session-manager-ui.md's "delete a session
    /// outright" feature request) — gated by AdminAccessScope.CanManageTeam, deliberately not
    /// SessionAccessScope.CanEdit, since this is out of scope for routine Session Manager work.
    /// </summary>
    public async Task<IActionResult> OnPostDeleteSessionAsync()
    {
        var user = await userManager.GetUserWithManagerAsync(dbContext, User);
        if (user is null) return Forbid();

        var session = await dbContext.Sessions.FirstOrDefaultAsync(s => s.Id == Id);
        if (session is null) return NotFound();
        if (!adminAccessScope.CanManageTeam(user, session.TeamId)) return Forbid();

        var result = await sessionActionService.DeleteAsync(Id, user.Id, CancellationToken.None);
        if (result.Result == SessionActionResult.Success)
        {
            TempData["StatusMessage"] = $"Session deleted — {result.CandidatesRemoved} candidate(s), {result.PaymentsRemoved} payment(s), and {result.VeAssignmentsRemoved} VE roster assignment(s) removed with it.";
            return RedirectToPage("./Index");
        }

        TempData["ErrorMessage"] = result.Result switch
        {
            SessionActionResult.Blocked =>
                "Could not delete session — one of its payments is still referenced by an unmatched Square payment record. Resolve that first.",
            _ => "Could not delete session."
        };
        return RedirectToPage(new { id = Id });
    }

    // Pulls this session's team through the exact same pipeline SessionIngestionJob runs on its own
    // tick (ingestion, VE roster sync, Zoom/Discord scheduling, Square payment links, confirmation
    // emails) — see ManualCandidateRefreshService. Scoped to THIS session only (changed 2026-08-03;
    // it previously ran the whole team's pipeline, so one click could send emails and mint payment
    // links for every other session the team had) — the rest of the team catches up on the Worker's
    // next scheduled tick, and Team Maintenance's "Refresh now" remains the team-wide button.
    //
    // TODO: refine the confirmation-email flow this (and the background job) triggers — audit how
    // many emails a candidate actually receives and when, across registration/reminder/reschedule
    // paths, before this button trains Session Managers to expect "one click, one email."
    public async Task<IActionResult> OnPostRefreshCandidatesAsync()
    {
        var auth = await AuthorizeAsync();
        if (auth is null) return Forbid();

        var result = await manualRefreshService.RunForSessionAsync(auth.Value.Session.Team, Id, CancellationToken.None);
        SetStatus(true,
            $"Refreshed — {result.CandidatesAdded} new candidate(s), {result.CandidatesUpdated} updated, {result.ConfirmationEmailsSent} confirmation email(s) sent.",
            "");
        return RedirectToPage(new { id = Id });
    }

    // The VE roster is displayed here but not editable: VolunteerExaminerSyncService fully
    // reconciles it against ExamTools on every poll, so an in-app add or remove was undone on the
    // next tick. Removed 2026-08-07 for the same reason as the walk-in/move-candidate actions —
    // see CLAUDE.md's "check whether ExamTools already does it" pattern.

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
        // Must be GetUserWithManagerAsync, not the bare GetUserAsync: CanEdit reads user.UserTeams,
        // which the bare load leaves empty — every POST here would Forbid() for TeamAdmin/
        // SessionManager (SystemAdmin's role short-circuit masked it). See CLAUDE.md Known Constraints.
        var user = await userManager.GetUserWithManagerAsync(dbContext, User);
        if (user is null)
        {
            return null;
        }

        var session = await dbContext.Sessions.Include(s => s.Team).FirstOrDefaultAsync(s => s.Id == Id);
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
        var user = await userManager.GetUserWithManagerAsync(dbContext, User);
        if (user is null)
        {
            return false;
        }

        var session = await dbContext.Sessions
            .Include(s => s.Vec)
            .Include(s => s.Team)
            .Include(s => s.FeeConfiguration)
            .Include(s => s.Candidates).ThenInclude(c => c.Payments)
            .Include(s => s.SessionVolunteerExaminers).ThenInclude(l => l.VolunteerExaminer).ThenInclude(v => v.VecAccreditations)
            .FirstOrDefaultAsync(s => s.Id == Id);

        if (session is null || !accessScope.CanView(user, session))
        {
            return false;
        }

        CanEdit = accessScope.CanEdit(user, session);
        CanDeleteSession = adminAccessScope.CanManageTeam(user, session.TeamId);

        var discordEventUrl = session.DiscordEventId is not null && session.Team.DiscordGuildId is not (null or 0)
            ? $"https://discord.com/events/{session.Team.DiscordGuildId}/{session.DiscordEventId}"
            : null;

        var feeLine = session.FeeConfiguration.FeeCollectionEnabled
            ? $"${session.FeeConfiguration.ExamFeeAmount:F2} exam · ${session.FeeConfiguration.RetainedAmount:F2} retained"
            : "No fee collected";

        var feeSummary = session.GetFeeSummary();

        Session = new SessionSummary(
            session.Id,
            SessionBreadcrumbFormatter.Format(session.ExtId, session.Title),
            $"Session — {EasternTimeFormatter.Format(session.ScheduledStartUtc, "ddd, MMM d, yyyy · h:mm tt")}",
            session.Vec.Name,
            session.ZoomJoinUrl,
            discordEventUrl,
            feeLine,
            $"${feeSummary.TotalCollected:F2}",
            $"${feeSummary.TotalRetained:F2}",
            $"${feeSummary.TotalRemitToVec:F2}",
            session.RetainedAmountOverride is not null,
            session.RetainedAmountOverride?.ToString("F2"),
            // Same rule as the session list's Status chip: completed by either route — a Session
            // Manager marking it, or ExamTools closing it (ExamToolsClosedUtc). Preferring the
            // manual timestamp keeps the more specific fact when both exist.
            session.CompletedUtc is { } completedUtc
                ? $"Completed {EasternTimeFormatter.Format(completedUtc, "MMM d, yyyy")}"
                : "Not yet completed",
            session.VecSubmissionStatus == VecSubmissionStatus.Submitted ? "chip-green" : "chip-neutral",
            session.VecSubmissionStatus == VecSubmissionStatus.Submitted ? "Submitted" : "Not submitted",
            session.VecSubmissionStatus == VecSubmissionStatus.Submitted,
            session.RescheduleFlaggedForReview,
            session.TestingCompletedUtc is not null,
            session.Status == SessionStatus.Cancelled);

        // Split rather than filtered: the withdrawn rows are still rendered, just behind a
        // disclosure, and the delete warning still has to count them.
        var rows = session.Candidates.OrderBy(c => c.Name).Select(ToRow).ToList();
        Candidates = [.. rows.Where(r => !r.IsWithdrawn)];
        WithdrawnCandidates = [.. rows.Where(r => r.IsWithdrawn)];

        // The eligibility check is session-relative on purpose: "expired on the day you have them
        // booked" is the fact that ruins a Saturday, and it is the one thing the Renewal Monitor
        // structurally cannot say. See VeSessionEligibility.
        VeRoster = session.SessionVolunteerExaminers
            .OrderBy(l => l.VolunteerExaminer.CallSign)
            .Select(l => new VeChip(
                l.VolunteerExaminer.Id,
                l.VolunteerExaminer.CallSign ?? "—",
                l.VolunteerExaminer.Name,
                VeSessionEligibility.For(l.VolunteerExaminer, session.ScheduledStartUtc, session.VecId)))
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

        var amountMismatchLine = primaryPayment?.AmountMismatchFlaggedUtc is not null
            ? $"Paid ${primaryPayment.SquareAmountPaidUsd:F2} against ${primaryPayment.Amount:F2} owed"
            : null;

        var emailHistory = CandidateEmailHistoryFormatter.Build(candidate);

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
            amountMismatchLine,
            candidate.Tested,
            !isWithdrawn && candidate.Email is not null,
            !isWithdrawn && primaryPayment is { Status: PaymentStatus.Unpaid },
            !isWithdrawn && candidate.ApplicationStatus is CandidateApplicationStatus.Unmatched or CandidateApplicationStatus.Received,
            !isWithdrawn && candidate.ApplicationStatus == CandidateApplicationStatus.Failed,
            !isWithdrawn && primaryPayment is not null,
            !isWithdrawn,
            !isWithdrawn && !candidate.Tested,
            primaryPayment?.Id,
            emailHistory);
    }

    public record SessionSummary(
        int Id,
        string BreadcrumbLabel,
        string Heading,
        string VecName,
        string? ZoomJoinUrl,
        string? DiscordEventUrl,
        string FeeLine,
        string TotalCollectedLine,
        string TotalRetainedLine,
        string TotalRemitToVecLine,
        bool RetainedAmountOverridden,
        string? RetainedAmountOverrideRawValue,
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
        string? AmountMismatchLine,
        bool Tested,
        bool CanResendConfirmation,
        bool CanMarkPaid,
        bool CanMarkFailed,
        bool CanCreateRetestPayment,
        bool CanFlagRefund,
        bool CanSendYouthProgram,
        bool CanDelete,
        int? PrimaryPaymentId,
        IReadOnlyList<EmailHistoryLine> EmailHistory);

    /// <summary>
    /// <see cref="Eligibility"/> is deliberately shown to every role that can load this page, unlike
    /// the VE Directory's contact details. It is derived from license class, expiry and accreditation
    /// — all public FCC record data or the team's own roster admin — and a Session Manager running
    /// Saturday's session is exactly who needs to know a VE cannot serve it.
    /// </summary>
    public record VeChip(int Id, string CallSign, string Name, VeEligibility Eligibility);
}
