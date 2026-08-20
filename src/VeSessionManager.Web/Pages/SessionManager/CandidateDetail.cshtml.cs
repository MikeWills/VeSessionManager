using System.Globalization;
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
using VeSessionManager.Core.Notifications;
using VeSessionManager.Core.Payments;

namespace VeSessionManager.Web.Pages.SessionManager;

/// <summary>
/// Applicant detail page — requested so a Session Manager can click into one candidate's full
/// record instead of only seeing it as a row on the session Detail page. Deliberately keyed by
/// Candidate.Id (this one registration/attempt), not by FRN: the same person can register again
/// for a retest or a future session, and each is its own Candidate row — this page shows every
/// *other* Candidate row sharing the same FRN as a read-only cross-reference list instead of
/// merging them, so a repeat test-taker's history is visible without conflating separate attempts.
/// Every write action here reuses the same Core services Detail.cshtml.cs calls — this page owns
/// no business logic of its own, same convention as Detail.cshtml.cs's own doc comment.
/// </summary>
[Authorize(Roles = RoleGroups.AllRoles)]
public class CandidateDetailModel(
    AppDbContext dbContext,
    UserManager<User> userManager,
    SessionAccessScope accessScope,
    CandidateActionService candidateActionService,
    CandidateNotificationService candidateNotificationService,
    RefundService refundService,
    TimeProvider timeProvider) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public int Id { get; set; }

    public CandidateDetailView Candidate { get; private set; } = null!;
    public bool CanEdit { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var loaded = await LoadForDisplayAsync();
        return loaded ? Page() : NotFound();
    }

    public async Task<IActionResult> OnPostResendConfirmationAsync()
    {
        var auth = await AuthorizeAsync();
        if (auth is null) return Forbid();

        Apply(ActionOutcomes.ResendConfirmation(
            await candidateNotificationService.ResendRegistrationConfirmationAsync(Id, CancellationToken.None)));
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostMarkFailedAsync()
    {
        var auth = await AuthorizeAsync();
        if (auth is null) return Forbid();

        Apply(ActionOutcomes.MarkFailed(
            await candidateActionService.MarkFailedAsync(Id, auth.Value.User.Id, CancellationToken.None)));
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostDeleteCandidateAsync()
    {
        var auth = await AuthorizeAsync();
        if (auth is null) return Forbid();

        Apply(ActionOutcomes.DeleteCandidate(
            await candidateActionService.DeleteAsync(Id, auth.Value.User.Id, CancellationToken.None)));
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostSetFrnAsync(string frn)
    {
        var auth = await AuthorizeAsync();
        if (auth is null) return Forbid();

        if (string.IsNullOrWhiteSpace(frn))
        {
            Apply(ActionOutcomes.BlankFrn());
            return RedirectToPage(new { id = Id });
        }

        Apply(ActionOutcomes.SetFrn(
            await candidateActionService.SetFrnAsync(Id, frn.Trim(), auth.Value.User.Id, CancellationToken.None)));
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostMarkPaidAsync(int paymentId)
    {
        var auth = await AuthorizeAsync();
        if (auth is null) return Forbid();
        if (!await PaymentBelongsToCandidateAsync(paymentId)) return Forbid();

        Apply(ActionOutcomes.MarkPaid(
            await candidateActionService.MarkPaidManuallyAsync(paymentId, auth.Value.User.Id, CancellationToken.None)));
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostFlagRefundAsync(int paymentId, string? notes)
    {
        var auth = await AuthorizeAsync();
        if (auth is null) return Forbid();
        if (!await PaymentBelongsToCandidateAsync(paymentId)) return Forbid();

        Apply(ActionOutcomes.FlagRefund(
            await candidateActionService.FlagRefundRequestedAsync(paymentId, auth.Value.User.Id, notes, CancellationToken.None)));
        return RedirectToPage(new { id = Id });
    }

    /// <summary>
    /// Issue a real refund through Square (#375) — as opposed to <c>OnPostFlagRefundAsync</c> above,
    /// which only writes a note. Both survive: the flag is still the right tool for money this API
    /// cannot reach, which is any payment over a year old or taken outside Square.
    ///
    /// <para>The amount is parsed here rather than bound, so a typo is a message rather than a
    /// silently-zeroed decimal — <c>Usd.TryParse</c> because a bare <c>decimal.TryParse</c> reads
    /// "12.50" as 1250 under a comma-decimal culture (CLAUDE.md's Usd entry).</para>
    /// </summary>
    public async Task<IActionResult> OnPostRefundAsync(int paymentId, string? amount, string? reason)
    {
        var auth = await AuthorizeAsync();
        if (auth is null) return Forbid();
        if (!await PaymentBelongsToCandidateAsync(paymentId)) return Forbid();

        if (!Usd.TryParse(amount, out var amountUsd))
        {
            Apply(new ActionOutcome(false, "Enter a refund amount, in dollars."));
            return RedirectToPage(new { id = Id });
        }

        Apply(ActionOutcomes.IssueRefund(
            await refundService.RefundPaymentAsync(paymentId, amountUsd, reason, auth.Value.User.Id, CancellationToken.None),
            amountUsd));
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostCreateRetestPaymentAsync()
    {
        var auth = await AuthorizeAsync();
        if (auth is null) return Forbid();

        Apply(ActionOutcomes.CreateRetestPayment(
            await candidateActionService.CreateRetestPaymentAsync(Id, auth.Value.User.Id, CancellationToken.None)));
        return RedirectToPage(new { id = Id });
    }

    /// <summary>See Detail.OnPostSendFelonyInstructionsAsync — manual since #221.</summary>
    public async Task<IActionResult> OnPostSendFelonyInstructionsAsync()
    {
        var auth = await AuthorizeAsync();
        if (auth is null) return Forbid();

        Apply(ActionOutcomes.SendFelonyInstructions(
            await candidateNotificationService.SendFelonyDisclosureInstructionsAsync(Id, CancellationToken.None)));
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostSendYouthProgramAsync()
    {
        var auth = await AuthorizeAsync();
        if (auth is null) return Forbid();

        Apply(ActionOutcomes.SendYouthProgram(
            await candidateNotificationService.SendYouthProgramInstructionsAsync(Id, CancellationToken.None)));
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

        var candidate = await dbContext.Candidates.Include(c => c.Session).FirstOrDefaultAsync(c => c.Id == Id);
        if (candidate is null || !accessScope.CanEdit(user, candidate.Session))
        {
            return null;
        }

        return (user, candidate.Session);
    }

    private Task<bool> PaymentBelongsToCandidateAsync(int paymentId) =>
        dbContext.Payments.AnyAsync(p => p.Id == paymentId && p.CandidateId == Id);

    /// <summary>See Detail.Apply — the wording comes from <see cref="ActionOutcomes"/>, never from here.</summary>
    private void Apply(ActionOutcome outcome) =>
        TempData[outcome.Success ? "StatusMessage" : "ErrorMessage"] = outcome.Message;

    private async Task<bool> LoadForDisplayAsync()
    {
        var user = await userManager.GetUserWithManagerAsync(dbContext, User);
        if (user is null)
        {
            return false;
        }

        var candidate = await dbContext.Candidates
            .Include(c => c.Session).ThenInclude(s => s.Vec)
            .Include(c => c.ResultMarkedByUser)
            .Include(c => c.Payments).ThenInclude(p => p.Refunds)
            // The hand-composed sends behind this candidate's Email history (#144).
            .Include(c => c.EmailSends)
            .Include(c => c.UlsHistory)
            .FirstOrDefaultAsync(c => c.Id == Id);

        if (candidate is null || !accessScope.CanView(user, candidate.Session))
        {
            return false;
        }

        CanEdit = accessScope.CanEdit(user, candidate.Session);

        // What the rule engine has actually sent this candidate (#415). One candidate, so the batch
        // loader is overkill here — but it is the same query, and having one definition of "which
        // runs count as received" is the point.
        var ruleSends = CandidateRuleSends.For(
            await CandidateRuleSends.LoadAsync(dbContext, [candidate.Id], HttpContext.RequestAborted),
            candidate.Id);

        var isWithdrawn = candidate.IsWithdrawn;
        var can = CandidateCapabilities.For(
            candidate, candidate.Session.Vec.SupportsYouthProgram, candidate.Payments.Count > 0);

        // Materialized first, then mapped in memory (not a server-side .Select() projection) — EF
        // Core can't translate EasternTimeFormatter.Format's TimeZoneInfo conversion to SQL.
        var otherAttempts = candidate.Frn is null
            ? []
            : (await dbContext.Candidates
                .Include(c => c.Session)
                .Where(c => c.Id != Id && c.Frn == candidate.Frn && c.Session.TeamId == candidate.Session.TeamId)
                .OrderByDescending(c => c.Session.ScheduledStartUtc)
                .ToListAsync(HttpContext.RequestAborted))
                .Select(c => new OtherAttemptRow(
                    c.Id,
                    c.Session.Id,
                    c.Name ?? "—",
                    EasternTimeFormatter.Format(c.Session.ScheduledStartUtc, "MMM d, yyyy"),
                    c.Session.ScheduledStartUtc.ToString("o", CultureInfo.InvariantCulture),
                    CandidatePresentation.StatusLabel(c.ApplicationStatus)))
                .ToList();

        Candidate = new CandidateDetailView(
            Id: candidate.Id,
            SessionId: candidate.Session.Id,
            SessionBreadcrumbLabel: SessionBreadcrumbFormatter.Format(candidate.Session.ExtId, candidate.Session.Title),
            SessionDateLine: EasternTimeFormatter.Format(candidate.Session.ScheduledStartUtc, "ddd, MMM d, yyyy · h:mm tt"),
            IsWithdrawn: isWithdrawn,
            DisplayName: CandidatePresentation.DisplayName(candidate),
            FirstName: isWithdrawn ? null : candidate.FirstName,
            Email: isWithdrawn ? null : candidate.Email,
            FrnLine: isWithdrawn
                ? "record retained for stats"
                : candidate.Frn is not null
                    ? candidate.Frn
                    : candidate.FrnMissingAtRegistration
                        ? "Missing at registration"
                        : "No FRN on file",
            CallSign: isWithdrawn ? null : candidate.CallSign,
            FccLicenseUrl: isWithdrawn ? null : FccUlsLinks.License(candidate.FccUlsLicenseKey),
            StatusLabel: CandidatePresentation.StatusLabel(candidate.ApplicationStatus),
            RegisteredLine: EasternTimeFormatter.Format(candidate.DateRegisteredUtc, "M/d/yyyy h:mm tt"),
            // ApplicationDateEnteredUtc/LicenseGrantDateUtc are FCC's own date-only fields (parsed
            // from an MM/dd/yyyy source with no time component, stored as UTC midnight) — NOT run
            // through EasternTimeFormatter. Converting a date-only value shifts it back a calendar
            // day (UTC midnight is 8pm the previous day in ET), which would misreport the actual
            // FCC-reported date rather than just relabel its timezone.
            ApplicationDateLine: candidate.ApplicationDateEnteredUtc?.ToString("M/d/yyyy", CultureInfo.InvariantCulture),
            LicenseGrantDateLine: candidate.LicenseGrantDateUtc?.ToString("M/d/yyyy", CultureInfo.InvariantCulture),
            LicenseNote: candidate.LicenseGrantPredatesSession()
                ? $"Already licensed before this session (held since {candidate.LicenseGrantDateUtc:M/d/yyyy}) — likely a repeat test or class upgrade, not a new grant."
                : null,
            LicenseClassLine: LicenseClassFormatter.FormatTransition(candidate.InitialLicenseClass, candidate.NewLicenseClass),
            // Hidden for a withdrawn candidate, like CallSign and the license link above: their FCC
            // application is not this session's story any more.
            ApplicationTimeline: isWithdrawn
                ? []
                : CandidatePresentation.ApplicationTimeline(candidate.UlsHistory.OrderBy(e => e.LogDateUtc ?? DateTime.MinValue)),
            ResultMarkedLine: FormatUtcOrNull(candidate.ResultMarkedUtc),
            ResultMarkedByName: candidate.ResultMarkedByUser?.Name,
            Tested: candidate.Tested,
            HasFelonyDisclosure: isWithdrawn ? null : candidate.HasFelonyDisclosure,
            Payments: candidate.Payments.OrderByDescending(p => p.CreatedUtc)
                .Select(p => ToPaymentRow(p, timeProvider.GetUtcNow().UtcDateTime)).ToList(),
            EmailHistory: CandidateEmailHistoryFormatter.Build(candidate, ruleSends),
            OtherAttempts: otherAttempts,
            CanResendConfirmation: can.CanResendConfirmation,
            CanDelete: can.CanDelete,
            CanMarkFailed: can.CanMarkFailed,
            CanCreateRetestPayment: can.CanCreateRetestPayment,
            CanFlagRefund: can.CanFlagRefund,
            CanSendYouthProgram: can.CanSendYouthProgram,
            CanSendFelonyInstructions: can.CanSendFelonyInstructions,
            AwaitingFelonyInstructions: can.AwaitingFelonyInstructions);

        return true;
    }

    private static string? FormatUtcOrNull(DateTime? value) =>
        EasternTimeFormatter.Format(value, "M/d/yyyy h:mm tt");

    private static PaymentRow ToPaymentRow(Payment payment, DateTime nowUtc)
    {
        var (chipClass, chipLabel) = SessionChips.Payment(payment.Status);

        var amountMismatchLine = payment.AmountMismatchFlaggedUtc is not null
            ? $"Paid {Usd.Format(payment.SquareAmountPaidUsd!.Value)} against {Usd.Format(payment.Amount)} owed"
            : null;

        // What Square took, which is the ceiling on a refund — not Amount, which is what was owed.
        var collected = payment.SquareAmountPaidUsd ?? payment.Amount;
        var eligibility = RefundEligibility.For(
            payment.Status == PaymentStatus.Paid, payment.SquarePaymentId, collected,
            payment.PaidDateUtc, payment.Refunds, nowUtc);

        return new PaymentRow(
            payment.Id,
            payment.Reason == PaymentReason.Retest ? "Retest" : "Initial exam",
            Usd.Format(payment.Amount),
            chipClass,
            chipLabel,
            payment.PaymentLinkUrl,
            FormatUtcOrNull(payment.PaidDateUtc),
            payment.PaidDateUtc?.ToString("o", CultureInfo.InvariantCulture),
            payment.RefundRequested,
            payment.RefundNotes,
            amountMismatchLine,
            payment.ExpiredUnpaid,
            payment.SquareOrderCompletedUtc is not null,
            payment.Status == PaymentStatus.Unpaid,
            eligibility.CanRefund,
            // Only worth a sentence when there is something to explain. A payment that is simply
            // unpaid needs no note — the Unpaid chip beside it already says why there is nothing to
            // send back.
            RefundBlockedReason(eligibility.Blocker),
            eligibility.RemainingUsd,
            Usd.Raw(eligibility.RemainingUsd),
            [.. payment.Refunds.OrderByDescending(r => r.RequestedUtc).Select(ToRefundRow)]);
    }

    /// <summary>
    /// Why the refund action is unavailable, in the user's words. Null where the reason is already
    /// obvious from the row — an unpaid payment, or one with nothing left to refund, both of which
    /// the table already shows.
    /// </summary>
    private static string? RefundBlockedReason(RefundBlocker blocker) => blocker switch
    {
        // The one that genuinely needs explaining: nothing about the row hints at it, and the answer
        // ("do it in Square") is not guessable.
        RefundBlocker.NoSquarePaymentId =>
            "Recorded before the app stored Square's payment id — refund this one in the Square dashboard.",
        RefundBlocker.TooOld => "Square will not refund a payment taken more than a year ago.",
        RefundBlocker.RefundLimitReached => "Square allows at most 20 refunds against one payment.",
        _ => null
    };

    private static RefundRow ToRefundRow(Refund refund)
    {
        var (chipClass, chipLabel) = refund.Status switch
        {
            RefundStatus.Completed => ("chip-green", "Refunded"),
            RefundStatus.Rejected => ("chip-red", "Rejected by Square"),
            RefundStatus.Failed => ("chip-red", "Failed"),
            // Submitting and Pending read the same to a user — both mean "Square has it, wait" — and
            // the difference between them (whether our call got an answer) is ours, not theirs.
            _ => ("chip-amber", "Pending at Square")
        };

        return new RefundRow(
            refund.Id,
            Usd.Format(refund.AmountUsd),
            chipClass,
            chipLabel,
            FormatUtcOrNull(refund.RequestedUtc),
            refund.RequestedUtc.ToString("o", CultureInfo.InvariantCulture),
            refund.Reason,
            // Shown only on an outcome the user can act on. A FailureDetail on a still-pending refund
            // is a transient network message from a call that is about to be retried; surfacing it
            // would read as a failed refund.
            refund.Status is RefundStatus.Rejected or RefundStatus.Failed ? refund.FailureDetail : null);
    }

    public record CandidateDetailView(
        int Id,
        int SessionId,
        string SessionBreadcrumbLabel,
        string SessionDateLine,
        bool IsWithdrawn,
        string DisplayName,
        string? FirstName,
        string? Email,
        string FrnLine,
        string? CallSign,
        string? FccLicenseUrl,
        string StatusLabel,
        string RegisteredLine,
        string? ApplicationDateLine,
        string? LicenseGrantDateLine,
        string? LicenseNote,
        string? LicenseClassLine,
        IReadOnlyList<CandidatePresentation.TimelineLine> ApplicationTimeline,
        string? ResultMarkedLine,
        string? ResultMarkedByName,
        bool Tested,
        bool? HasFelonyDisclosure,
        IReadOnlyList<PaymentRow> Payments,
        IReadOnlyList<EmailHistoryLine> EmailHistory,
        IReadOnlyList<OtherAttemptRow> OtherAttempts,
        bool CanResendConfirmation,
        bool CanDelete,
        bool CanMarkFailed,
        bool CanCreateRetestPayment,
        bool CanFlagRefund,
        bool CanSendYouthProgram,
        bool CanSendFelonyInstructions,
        /// <summary>Declared a disclosure and has not been sent the instructions — the marker that replaces the automatic send (#221).</summary>
        bool AwaitingFelonyInstructions);

    public record PaymentRow(
        int Id,
        string ReasonLabel,
        string AmountLine,
        string ChipClass,
        string ChipLabel,
        string? PaymentLinkUrl,
        string? PaidDateLine,
        /// <summary>Raw timestamp behind PaidDateLine, for the payments table's click-to-sort header (see app.js) — the displayed M/d/yyyy form sorts wrong as text. Null (an unpaid payment) sorts last in both directions.</summary>
        string? PaidDateSortValue,
        bool RefundRequested,
        string? RefundNotes,
        string? AmountMismatchLine,
        bool ExpiredUnpaid,
        bool SquareOrderCompleted,
        bool CanMarkPaid,
        /// <summary>Whether a refund can be issued from here at all (#375). Re-decided server-side by RefundService — this only governs whether the button is offered.</summary>
        bool CanRefund,
        /// <summary>Why not, where that is not obvious from the row. Null when there is nothing worth saying.</summary>
        string? RefundBlockedReason,
        decimal RemainingRefundableUsd,
        /// <summary>The same number as a plain "12.50" for the amount input's value — Usd.Raw, never a bare :F2, which renders "12,50" under a comma-decimal culture and then fails to parse back.</summary>
        string RemainingRefundableRaw,
        IReadOnlyList<RefundRow> Refunds);

    /// <param name="FailureDetail">Square's reason, shown only on a rejected or failed refund.</param>
    public record RefundRow(
        int Id,
        string AmountLine,
        string ChipClass,
        string ChipLabel,
        string? RequestedLine,
        string RequestedSortValue,
        string? Reason,
        string? FailureDetail);

    /// <summary>SessionDateSortValue is the raw session start behind SessionDateLine, for the table's click-to-sort header (see app.js).</summary>
    public record OtherAttemptRow(int CandidateId, int SessionId, string Name, string SessionDateLine, string SessionDateSortValue, string StatusLabel);
}
