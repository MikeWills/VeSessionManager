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
[Authorize(Roles = "SystemAdmin,TeamAdmin,SessionManager,TeamLead")]
public class CandidateDetailModel(
    AppDbContext dbContext,
    UserManager<User> userManager,
    SessionAccessScope accessScope,
    CandidateActionService candidateActionService,
    CandidateNotificationService candidateNotificationService) : PageModel
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

        var result = await candidateNotificationService.ResendRegistrationConfirmationAsync(Id, CancellationToken.None);
        SetStatus(result == CandidateEmailSendResult.Sent, "Confirmation email resent.", $"Could not resend confirmation email: {result}.");
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostMarkFailedAsync()
    {
        var auth = await AuthorizeAsync();
        if (auth is null) return Forbid();

        var result = await candidateActionService.MarkFailedAsync(Id, auth.Value.User.Id, CancellationToken.None);
        SetStatus(result == CandidateActionResult.Success, "Candidate marked failed.", "Could not mark candidate failed.");
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostDeleteCandidateAsync()
    {
        var auth = await AuthorizeAsync();
        if (auth is null) return Forbid();

        var result = await candidateActionService.DeleteAsync(Id, auth.Value.User.Id, CancellationToken.None);
        SetStatus(result == CandidateActionResult.Success, "Candidate marked as withdrew/no-show; PII cleared.", "Could not delete candidate — testing already completed for this session.");
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostSetFrnAsync(string frn)
    {
        var auth = await AuthorizeAsync();
        if (auth is null) return Forbid();

        if (string.IsNullOrWhiteSpace(frn))
        {
            SetStatus(false, "", "FRN cannot be blank.");
            return RedirectToPage(new { id = Id });
        }

        var result = await candidateActionService.SetFrnAsync(Id, frn.Trim(), auth.Value.User.Id, CancellationToken.None);
        SetStatus(result == CandidateActionResult.Success, "FRN updated.", "Could not update FRN.");
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostMarkPaidAsync(int paymentId)
    {
        var auth = await AuthorizeAsync();
        if (auth is null) return Forbid();
        if (!await PaymentBelongsToCandidateAsync(paymentId)) return Forbid();

        var result = await candidateActionService.MarkPaidManuallyAsync(paymentId, auth.Value.User.Id, CancellationToken.None);
        SetStatus(result == CandidateActionResult.Success, "Payment marked paid.", "Could not mark payment paid.");
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostFlagRefundAsync(int paymentId, string? notes)
    {
        var auth = await AuthorizeAsync();
        if (auth is null) return Forbid();
        if (!await PaymentBelongsToCandidateAsync(paymentId)) return Forbid();

        var result = await candidateActionService.FlagRefundRequestedAsync(paymentId, auth.Value.User.Id, notes, CancellationToken.None);
        SetStatus(result == CandidateActionResult.Success, "Refund requested flagged.", "Could not flag refund requested.");
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostCreateRetestPaymentAsync()
    {
        var auth = await AuthorizeAsync();
        if (auth is null) return Forbid();

        var result = await candidateActionService.CreateRetestPaymentAsync(Id, auth.Value.User.Id, CancellationToken.None);
        SetStatus(result == CandidateActionResult.Success, "Retest payment created.", "Could not create retest payment — candidate must be marked Failed first.");
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostSendYouthProgramAsync()
    {
        var auth = await AuthorizeAsync();
        if (auth is null) return Forbid();

        var result = await candidateNotificationService.SendYouthProgramInstructionsAsync(Id, CancellationToken.None);
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

        var candidate = await dbContext.Candidates.Include(c => c.Session).FirstOrDefaultAsync(c => c.Id == Id);
        if (candidate is null || !accessScope.CanEdit(user, candidate.Session))
        {
            return null;
        }

        return (user, candidate.Session);
    }

    private Task<bool> PaymentBelongsToCandidateAsync(int paymentId) =>
        dbContext.Payments.AnyAsync(p => p.Id == paymentId && p.CandidateId == Id);

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

        var candidate = await dbContext.Candidates
            .Include(c => c.Session).ThenInclude(s => s.Vec)
            .Include(c => c.ResultMarkedByUser)
            .Include(c => c.Payments)
            .FirstOrDefaultAsync(c => c.Id == Id);

        if (candidate is null || !accessScope.CanView(user, candidate.Session))
        {
            return false;
        }

        CanEdit = accessScope.CanEdit(user, candidate.Session);

        var isWithdrawn = candidate.ApplicationStatus == CandidateApplicationStatus.NotTested;

        var otherAttempts = candidate.Frn is null
            ? []
            : await dbContext.Candidates
                .Include(c => c.Session)
                .Where(c => c.Id != Id && c.Frn == candidate.Frn && c.Session.TeamId == candidate.Session.TeamId)
                .OrderByDescending(c => c.Session.ScheduledStartUtc)
                .Select(c => new OtherAttemptRow(
                    c.Id,
                    c.Session.Id,
                    c.Name ?? "—",
                    c.Session.ScheduledStartUtc.ToString("MMM d, yyyy", CultureInfo.InvariantCulture),
                    c.ApplicationStatus == CandidateApplicationStatus.NotTested ? "Withdrew/no-show" : c.ApplicationStatus.ToString()))
                .ToListAsync();

        Candidate = new CandidateDetailView(
            Id: candidate.Id,
            SessionId: candidate.Session.Id,
            SessionExamToolsId: candidate.Session.ExamToolsSessionId,
            SessionDateLine: candidate.Session.ScheduledStartUtc.ToString("ddd, MMM d, yyyy · h:mm tt", CultureInfo.InvariantCulture),
            IsWithdrawn: isWithdrawn,
            DisplayName: isWithdrawn ? "Withdrew — PII cleared" : candidate.Name ?? "—",
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
            FccLicenseUrl: isWithdrawn || candidate.FccUlsLicenseKey is null
                ? null
                : $"https://wireless2.fcc.gov/UlsApp/UlsSearch/license.jsp?licKey={Uri.EscapeDataString(candidate.FccUlsLicenseKey)}",
            StatusLabel: candidate.ApplicationStatus == CandidateApplicationStatus.NotTested ? "Not tested" : candidate.ApplicationStatus.ToString(),
            RegisteredLine: candidate.DateRegisteredUtc.ToString("M/d/yyyy h:mm tt", CultureInfo.InvariantCulture) + " UTC",
            ApplicationDateLine: candidate.ApplicationDateEnteredUtc?.ToString("M/d/yyyy", CultureInfo.InvariantCulture),
            LicenseGrantDateLine: candidate.LicenseGrantDateUtc?.ToString("M/d/yyyy", CultureInfo.InvariantCulture),
            LicenseNote: candidate.LicenseGrantPredatesSession()
                ? $"Already licensed before this session (held since {candidate.LicenseGrantDateUtc:M/d/yyyy}) — likely a repeat test or class upgrade, not a new grant."
                : null,
            LicenseClassLine: FormatLicenseClassLine(candidate.InitialLicenseClass, candidate.NewLicenseClass),
            ResultMarkedLine: FormatUtcOrNull(candidate.ResultMarkedUtc),
            ResultMarkedByName: candidate.ResultMarkedByUser?.Name,
            Tested: candidate.Tested,
            HasFelonyDisclosure: isWithdrawn ? null : candidate.HasFelonyDisclosure,
            Payments: candidate.Payments.OrderByDescending(p => p.CreatedUtc).Select(ToPaymentRow).ToList(),
            EmailHistory: CandidateEmailHistoryFormatter.Build(candidate),
            OtherAttempts: otherAttempts,
            CanResendConfirmation: !isWithdrawn && candidate.Email is not null,
            CanDelete: !isWithdrawn && !candidate.Tested,
            CanMarkFailed: !isWithdrawn && candidate.ApplicationStatus is CandidateApplicationStatus.Unmatched or CandidateApplicationStatus.Received,
            CanCreateRetestPayment: !isWithdrawn && candidate.ApplicationStatus == CandidateApplicationStatus.Failed,
            CanFlagRefund: !isWithdrawn && candidate.Payments.Count > 0,
            CanSendYouthProgram: !isWithdrawn && candidate.Session.Vec.SupportsYouthProgram);

        return true;
    }

    // Same "no stored timezone" reasoning as CandidateEmailHistoryFormatter.FormatSentUtc — labeled
    // UTC since it isn't converted to any local time. Null-safe: `x?.ToString() + " UTC"` on its own
    // still appends " UTC" to a null value (the `+` runs unconditionally), producing a bare "UTC"
    // string instead of null — caught live rendering this page against a candidate with no result
    // marked yet.
    private static string? FormatUtcOrNull(DateTime? value) =>
        value is { } v ? v.ToString("M/d/yyyy h:mm tt", CultureInfo.InvariantCulture) + " UTC" : null;

    // Derived purely from exam elements graded this sitting — see ExamResultSyncService.ResolveLicenseClasses.
    // Only set for a candidate who passed at least one graded element, so both are always null or both set.
    private static string? FormatLicenseClassLine(LicenseClass? initial, LicenseClass? newClass) =>
        initial is { } i && newClass is { } n ? $"{FormatClass(i)} → {FormatClass(n)}" : null;

    private static string FormatClass(LicenseClass licenseClass) =>
        licenseClass == LicenseClass.None ? "Unlicensed" : licenseClass.ToString();

    private static PaymentRow ToPaymentRow(Payment payment)
    {
        var (chipClass, chipLabel) = payment.Status switch
        {
            PaymentStatus.Paid => ("chip-green", "Paid"),
            PaymentStatus.Unpaid => ("chip-amber", "Unpaid"),
            _ => ("chip-neutral", "Not applicable")
        };

        var amountMismatchLine = payment.AmountMismatchFlaggedUtc is not null
            ? $"Paid ${payment.SquareAmountPaidUsd:F2} against ${payment.Amount:F2} owed"
            : null;

        return new PaymentRow(
            payment.Id,
            payment.Reason == PaymentReason.Retest ? "Retest" : "Initial exam",
            $"${payment.Amount:F2}",
            chipClass,
            chipLabel,
            payment.PaymentLinkUrl,
            FormatUtcOrNull(payment.PaidDateUtc),
            payment.RefundRequested,
            payment.RefundNotes,
            amountMismatchLine,
            payment.ExpiredUnpaid,
            payment.SquareOrderCompletedUtc is not null,
            payment.Status == PaymentStatus.Unpaid);
    }

    public record CandidateDetailView(
        int Id,
        int SessionId,
        string SessionExamToolsId,
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
        bool CanSendYouthProgram);

    public record PaymentRow(
        int Id,
        string ReasonLabel,
        string AmountLine,
        string ChipClass,
        string ChipLabel,
        string? PaymentLinkUrl,
        string? PaidDateLine,
        bool RefundRequested,
        string? RefundNotes,
        string? AmountMismatchLine,
        bool ExpiredUnpaid,
        bool SquareOrderCompleted,
        bool CanMarkPaid);

    public record OtherAttemptRow(int CandidateId, int SessionId, string Name, string SessionDateLine, string StatusLabel);
}
