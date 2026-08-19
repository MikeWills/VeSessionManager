using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core;
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
[Authorize(Roles = RoleGroups.AllRoles)]
public class DetailModel(
    AppDbContext dbContext,
    UserManager<User> userManager,
    SessionAccessScope accessScope,
    AdminAccessScope adminAccessScope,
    CandidateActionService candidateActionService,
    SessionActionService sessionActionService,
    CandidateNotificationService candidateNotificationService,
    VecSubmissionService vecSubmissionService,
    ManualCandidateRefreshService manualRefreshService,
    TimeProvider timeProvider) : PageModel
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

    /// <summary>What the "Email candidates" menu offers as one-click starting points — see ComposableEmailTemplates.</summary>
    public IReadOnlyList<ComposableEmailTemplates.Choice> EmailTemplateChoices { get; private set; } = [];

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

        Apply(ActionOutcomes.ClearRescheduleFlag(
            await sessionActionService.ClearRescheduleFlagAsync(Id, auth.Value.User.Id, CancellationToken.None)));
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostMarkCompletedAsync()
    {
        var auth = await AuthorizeAsync();
        if (auth is null) return Forbid();

        Apply(ActionOutcomes.MarkCompleted(
            await sessionActionService.MarkCompletedAsync(Id, auth.Value.User.Id, CancellationToken.None)));
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
            if (!Usd.TryParse(overrideAmount, out var value) || value < 0)
            {
                Apply(new ActionOutcome(false, "Retained amount must be a non-negative dollar amount."));
                return RedirectToPage(new { id = Id });
            }

            parsedAmount = value;
        }

        Apply(ActionOutcomes.SetRetainedAmountOverride(
            await sessionActionService.SetRetainedAmountOverrideAsync(Id, parsedAmount, auth.Value.User.Id, CancellationToken.None),
            parsedAmount));
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostToggleVecSubmissionAsync()
    {
        var auth = await AuthorizeAsync();
        if (auth is null) return Forbid();

        Apply(ActionOutcomes.MarkSubmittedToVec(
            await vecSubmissionService.MarkSubmittedAsync(Id, auth.Value.User.Id, CancellationToken.None)));
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

        var outcome = ActionOutcomes.DeleteSession(
            await sessionActionService.DeleteAsync(Id, user.Id, CancellationToken.None));
        Apply(outcome);
        // The one Session Manager action that leaves the page it was launched from — the session
        // that page was showing no longer exists.
        return outcome.Success ? RedirectToPage("./Index") : RedirectToPage(new { id = Id });
    }

    // Pulls this session's team through the exact same pipeline SessionIngestionJob runs on its own
    // tick (ingestion, VE roster sync, Zoom/Discord scheduling, Square payment links, confirmation
    // emails) — see ManualCandidateRefreshService. Scoped to THIS session only (changed 2026-08-03;
    // it previously ran the whole team's pipeline, so one click could send emails and mint payment
    // links for every other session the team had) — the rest of the team catches up on the Worker's
    // next scheduled tick, and Team Maintenance's "Refresh now" remains the team-wide button.
    //
    // Audited for #193, since the question was whether this button trains Session Managers to expect
    // "one click, one email". It does not, and the answer is worth stating where the button lives:
    //
    //   * At most ONE registration confirmation per candidate, ever — the scan filters on
    //     RegistrationConfirmationSentUtc == null and stamps it, so a second click sends nothing.
    //   * Only for candidates on a session that has not already ended, in THIS session alone.
    //   * Nothing else in the pipeline emails a candidate. Reminders are separate daily jobs; the
    //     felony-disclosure and youth-program emails are per-candidate buttons.
    //   * A reschedule re-sends nothing: no code path anywhere clears that stamp. Safe rather than a
    //     gap only because ApplyRescheduleRules refuses to move a session that has candidates.
    //
    // Full table, and the two things this audit found stale in it, in docs/email-reference.md.
    public async Task<IActionResult> OnPostRefreshCandidatesAsync()
    {
        var auth = await AuthorizeAsync();
        if (auth is null) return Forbid();

        var result = await manualRefreshService.RunForSessionAsync(auth.Value.Session.Team, Id, CancellationToken.None);

        // Describe(), not a hand-built sentence: a failed pipeline returns zero counts, so the old
        // message rendered "Refreshed — 0 new candidate(s)" in green over a total failure (#242).
        var (success, message) = result.Describe(teamName: null);
        Apply(new ActionOutcome(success, message));
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

        Apply(ActionOutcomes.ResendConfirmation(
            await candidateNotificationService.ResendRegistrationConfirmationAsync(candidateId, CancellationToken.None)));
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostMarkFailedAsync(int candidateId)
    {
        var auth = await AuthorizeAsync();
        if (auth is null) return Forbid();
        if (!await CandidateBelongsToSessionAsync(candidateId)) return Forbid();

        Apply(ActionOutcomes.MarkFailed(
            await candidateActionService.MarkFailedAsync(candidateId, auth.Value.User.Id, CancellationToken.None)));
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostDeleteCandidateAsync(int candidateId)
    {
        var auth = await AuthorizeAsync();
        if (auth is null) return Forbid();
        if (!await CandidateBelongsToSessionAsync(candidateId)) return Forbid();

        Apply(ActionOutcomes.DeleteCandidate(
            await candidateActionService.DeleteAsync(candidateId, auth.Value.User.Id, CancellationToken.None)));
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostSetFrnAsync(int candidateId, string frn)
    {
        var auth = await AuthorizeAsync();
        if (auth is null) return Forbid();
        if (!await CandidateBelongsToSessionAsync(candidateId)) return Forbid();

        if (string.IsNullOrWhiteSpace(frn))
        {
            Apply(ActionOutcomes.BlankFrn());
            return RedirectToPage(new { id = Id });
        }

        Apply(ActionOutcomes.SetFrn(
            await candidateActionService.SetFrnAsync(candidateId, frn.Trim(), auth.Value.User.Id, CancellationToken.None)));
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostMarkPaidAsync(int paymentId)
    {
        var auth = await AuthorizeAsync();
        if (auth is null) return Forbid();
        if (!await PaymentBelongsToSessionAsync(paymentId)) return Forbid();

        Apply(ActionOutcomes.MarkPaid(
            await candidateActionService.MarkPaidManuallyAsync(paymentId, auth.Value.User.Id, CancellationToken.None)));
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostFlagRefundAsync(int paymentId, string? notes)
    {
        var auth = await AuthorizeAsync();
        if (auth is null) return Forbid();
        if (!await PaymentBelongsToSessionAsync(paymentId)) return Forbid();

        Apply(ActionOutcomes.FlagRefund(
            await candidateActionService.FlagRefundRequestedAsync(paymentId, auth.Value.User.Id, notes, CancellationToken.None)));
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostCreateRetestPaymentAsync(int candidateId)
    {
        var auth = await AuthorizeAsync();
        if (auth is null) return Forbid();
        if (!await CandidateBelongsToSessionAsync(candidateId)) return Forbid();

        Apply(ActionOutcomes.CreateRetestPayment(
            await candidateActionService.CreateRetestPaymentAsync(candidateId, auth.Value.User.Id, CancellationToken.None)));
        return RedirectToPage(new { id = Id });
    }

    /// <summary>
    /// Tells a candidate that their declared felony disclosure means extra FCC steps (#221). Manual
    /// and per-candidate on purpose: this was an automatic side effect of marking a session complete,
    /// which both sent it too late to be useful and sent it without anyone deciding to.
    /// </summary>
    public async Task<IActionResult> OnPostSendFelonyInstructionsAsync(int candidateId)
    {
        var auth = await AuthorizeAsync();
        if (auth is null) return Forbid();
        if (!await CandidateBelongsToSessionAsync(candidateId)) return Forbid();

        Apply(ActionOutcomes.SendFelonyInstructions(
            await candidateNotificationService.SendFelonyDisclosureInstructionsAsync(candidateId, CancellationToken.None)));
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostSendYouthProgramAsync(int candidateId)
    {
        var auth = await AuthorizeAsync();
        if (auth is null) return Forbid();
        if (!await CandidateBelongsToSessionAsync(candidateId)) return Forbid();

        Apply(ActionOutcomes.SendYouthProgram(
            await candidateNotificationService.SendYouthProgramInstructionsAsync(candidateId, CancellationToken.None)));
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

    /// <summary>
    /// Puts an outcome where the layout's banner will find it. The wording itself comes from
    /// <see cref="ActionOutcomes"/> and is never written here — see that class for why.
    /// </summary>
    private void Apply(ActionOutcome outcome) =>
        TempData[outcome.Success ? "StatusMessage" : "ErrorMessage"] = outcome.Message;

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
            // The hand-composed sends behind each candidate's Email history line (#144).
            .Include(s => s.Candidates).ThenInclude(c => c.EmailSends)
            .Include(s => s.SessionVolunteerExaminers).ThenInclude(l => l.VolunteerExaminer).ThenInclude(v => v.VecAccreditations)
            .AsSplitQuery()
            .FirstOrDefaultAsync(s => s.Id == Id);

        if (session is null || !accessScope.CanView(user, session))
        {
            return false;
        }

        CanEdit = accessScope.CanEdit(user, session);
        // Shortcuts straight into the compose screen with a template already chosen (#394 follow-up).
        // Only loaded for someone who can act on them.
        EmailTemplateChoices = CanEdit
            ? await ComposableEmailTemplates.LoadAsync(dbContext, session.TeamId, HttpContext.RequestAborted)
            : [];
        CanDeleteSession = adminAccessScope.CanManageTeam(user, session.TeamId);

        // Same comparison the session list makes, so a session on the boundary cannot render one
        // chip here and a different one there.
        var hasStarted = session.ScheduledStartUtc <= timeProvider.GetUtcNow().UtcDateTime;

        var discordEventUrl = session.DiscordEventId is not null && session.Team.DiscordGuildId is not (null or 0)
            ? $"https://discord.com/events/{session.Team.DiscordGuildId}/{session.DiscordEventId}"
            : null;

        var feeLine = session.FeeConfiguration.FeeCollectionEnabled
            ? $"{Usd.Format(session.FeeConfiguration.ExamFeeAmount)} exam · {Usd.Format(session.FeeConfiguration.RetainedAmount)} retained"
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
            Usd.Format(feeSummary.TotalCollected),
            Usd.Format(feeSummary.TotalRetained),
            Usd.Format(feeSummary.TotalRemitToVec),
            session.RetainedAmountOverride is not null,
            session.RetainedAmountOverride is { } ov ? Usd.Raw(ov) : null,
            // Same rule as the session list's Status chip: completed by either route — a Session
            // Manager marking it, or ExamTools closing it (ExamToolsClosedUtc). Preferring the
            // manual timestamp keeps the more specific fact when both exist.
            session.CompletedUtc is { } completedUtc
                ? $"Completed {EasternTimeFormatter.Format(completedUtc, "MMM d, yyyy")}"
                : "Not yet completed",
            // Was its own copy of this switch, and had drifted: it lacked the list's cancelled
            // branch, so a cancelled session read "Not submitted" here and "—" there. hasStarted is
            // the #338 half — a session that has not run has produced nothing to submit.
            SessionChips.VecSubmission(session.Status, session.VecSubmissionStatus, hasStarted).Class,
            SessionChips.VecSubmission(session.Status, session.VecSubmissionStatus, hasStarted).Label,
            session.VecSubmissionStatus == VecSubmissionStatus.Submitted,
            session.RescheduleFlaggedForReview,
            session.TestingCompletedUtc is not null,
            session.Status == SessionStatus.Cancelled,
            string.Equals(session.Vec.MatchCode, ArrlSubmissionPreviewService.ArrlMatchCode, StringComparison.OrdinalIgnoreCase));

        // Split rather than filtered: the withdrawn rows are still rendered, just behind a
        // disclosure, and the delete warning still has to count them.
        // session.Vec is Included above; passing the flag rather than reading it inside ToRow is what
        // makes the requirement visible here — omitting it is exactly what #274 was.
        // One query for the whole roster, not one per row (#415): this page renders a row per
        // candidate, so a per-candidate history lookup would be an N+1 across a full session.
        var ruleSends = await CandidateRuleSends.LoadAsync(
            dbContext, [.. session.Candidates.Select(c => c.Id)], HttpContext.RequestAborted);

        var rows = session.Candidates.OrderBy(c => c.Name)
            .Select(c => ToRow(c, session.Vec.SupportsYouthProgram, CandidateRuleSends.For(ruleSends, c.Id))).ToList();
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

    private static CandidateRow ToRow(Candidate candidate, bool vecSupportsYouthProgram, IReadOnlyList<RuleSend> ruleSends)
    {
        var isWithdrawn = candidate.IsWithdrawn;
        var primaryPayment = candidate.Payments.OrderByDescending(p => p.CreatedUtc).FirstOrDefault(p => p.Status == PaymentStatus.Unpaid)
            ?? candidate.Payments.OrderByDescending(p => p.CreatedUtc).FirstOrDefault();

        var (paymentClass, paymentLabel) = SessionChips.Payment(primaryPayment?.Status);

        var meterSegments = candidate.ApplicationStatus switch
        {
            CandidateApplicationStatus.Received => new[] { "on-a", "", "" },
            CandidateApplicationStatus.Granted => new[] { "on-a", "on-g", "on-g" },
            CandidateApplicationStatus.Failed => new[] { "on-r", "", "" },
            CandidateApplicationStatus.NotTested => new[] { "off-dim", "off-dim", "off-dim" },
            _ => new[] { "", "", "" }
        };

        var statusLabel = CandidatePresentation.StatusLabel(candidate.ApplicationStatus);

        var frnLine = isWithdrawn
            ? "record retained for stats"
            : candidate.Frn is not null
                ? $"FRN {candidate.Frn}"
                : candidate.FrnMissingAtRegistration
                    ? "FRN missing at registration"
                    : "No FRN on file";

        var amountMismatchLine = primaryPayment?.AmountMismatchFlaggedUtc is not null
            ? $"Paid {Usd.Format(primaryPayment.SquareAmountPaidUsd!.Value)} against {Usd.Format(primaryPayment.Amount)} owed"
            : null;

        var emailHistory = CandidateEmailHistoryFormatter.Build(candidate, ruleSends);
        var can = CandidateCapabilities.For(candidate, vecSupportsYouthProgram, primaryPayment is not null);

        return new CandidateRow(
            candidate.Id,
            isWithdrawn,
            CandidatePresentation.DisplayName(candidate),
            isWithdrawn ? "—" : candidate.CallSign ?? "—",
            frnLine,
            meterSegments,
            statusLabel,
            paymentClass,
            paymentLabel,
            primaryPayment?.RefundRequested ?? false,
            amountMismatchLine,
            candidate.Tested,
            can.CanResendConfirmation,
            // The one capability that is not shared: the roster acts on the row's primary payment,
            // where the detail page offers it per payment.
            !isWithdrawn && primaryPayment is { Status: PaymentStatus.Unpaid },
            can.CanMarkFailed,
            can.CanCreateRetestPayment,
            can.CanFlagRefund,
            can.CanSendYouthProgram,
            can.CanSendFelonyInstructions,
            can.AwaitingFelonyInstructions,
            can.CanDelete,
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
        bool Cancelled,
        /// <summary>
        /// True when this session's VEC is ARRL, matched on <c>Vec.MatchCode</c> and never the display
        /// name ("ARRL" here, "ARRL-VEC" upstream). Decides whether the VEC-submission control opens
        /// the ARRL preview or stays the plain "I filed this by hand" toggle every other VEC uses
        /// (#197) — one submitter, no fallback.
        /// </summary>
        bool IsArrlSession);

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
        bool CanSendFelonyInstructions,
        /// <summary>Declared a disclosure and has not been sent the instructions — the marker that replaces the automatic send (#221).</summary>
        bool AwaitingFelonyInstructions,
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
