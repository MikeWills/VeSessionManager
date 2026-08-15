using VeSessionManager.Core;
using VeSessionManager.Core.CandidateActions;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Notifications;
using VeSessionManager.Core.Payments;
using VeSessionManager.Core.Sessions;
using VeSessionManager.Core.VecSubmissions;

namespace VeSessionManager.Web;

/// <summary>Whether an action succeeded, and the one sentence the user is shown about it.</summary>
public readonly record struct ActionOutcome(bool Success, string Message);

/// <summary>
/// The single definition of what each Session Manager action says to the user (issue #304).
///
/// <para><b>What this replaces.</b> Nine candidate actions and four session actions were written out
/// once per page model — <c>Detail</c>, <c>CandidateDetail</c> and the session list — identical down
/// to the punctuation. Identical is the state these start in; it is not the state they stay in. Two
/// live bugs came from exactly this pair of files:</para>
///
/// <list type="bullet">
///   <item><b>#244</b> — <see cref="VecSubmissionService.MarkSubmittedAsync"/> returns three values.
///   <c>Detail</c> was fixed to handle all three and the list copy was not, so a session that could
///   not be found reported that it was <i>already submitted</i>.</item>
///   <item><b>#274</b> — one copy of <c>CanSendYouthProgram</c> checked the VEC's youth-program flag
///   and the other checked nothing, so the button rendered where the service would refuse it. See
///   <see cref="CandidateCapabilities"/>, which is the same fix for the display side.</item>
/// </list>
///
/// <para><b>Why the messages and not the handlers.</b> The handlers themselves are 4 lines of
/// authorization, ownership re-check and redirect, and those three things genuinely differ per page —
/// the list re-resolves a posted session id, <c>Detail</c> trusts its route id and re-checks the
/// candidate, <c>CandidateDetail</c> is the candidate. Collapsing them would trade a real
/// duplication for a fake abstraction. What actually drifted, both times, was the mapping from a
/// result to a sentence, and that is what lives here.</para>
///
/// <para>Every mapping is exhaustive over its enum, and
/// <c>ActionOutcomesTests.EveryOutcomeOfAnActionSaysSomethingDifferent</c> holds it that way — #244
/// was two enum values sharing one sentence.</para>
/// </summary>
public static class ActionOutcomes
{
    // ---- Session actions -------------------------------------------------------------------

    public static ActionOutcome MarkSubmittedToVec(VecSubmissionMarkResult result) => result switch
    {
        VecSubmissionMarkResult.Marked => new(true, "Session marked submitted to VEC."),
        VecSubmissionMarkResult.AlreadySubmitted => new(false, "Session is already marked submitted."),
        VecSubmissionMarkResult.SessionNotFound => new(false, "Session not found."),
        _ => new(false, "Could not mark the session submitted to VEC.")
    };

    /// <summary>
    /// The success message names the candidates who declared a felony disclosure rather than counting
    /// them silently: the send is manual since #221, and marking a session complete is the moment
    /// someone would otherwise assume it had been handled for them.
    /// </summary>
    public static ActionOutcome MarkCompleted(SessionCompletionResult result) => result.Result switch
    {
        SessionActionResult.Success when result.CandidatesAwaitingFelonyInstructions > 0 => new(true,
            $"Session marked completed — {result.CandidatesTested} candidate(s) tested. "
            + $"{result.CandidatesAwaitingFelonyInstructions} candidate(s) declared a felony disclosure "
            + "and have not been sent the FCC instructions — send them from the candidate's row."),
        SessionActionResult.Success => new(true,
            $"Session marked completed — {result.CandidatesTested} candidate(s) tested."),
        SessionActionResult.AlreadyDone => new(false, "Session is already marked completed."),
        SessionActionResult.NotFound => new(false, "Session not found."),
        _ => new(false, "Could not mark session completed.")
    };

    public static ActionOutcome ClearRescheduleFlag(SessionActionResult result) => result switch
    {
        SessionActionResult.Success => new(true, "Reschedule flag cleared."),
        SessionActionResult.AlreadyDone => new(false, "Session is not flagged for reschedule."),
        SessionActionResult.NotFound => new(false, "Session not found."),
        _ => new(false, "Could not clear reschedule flag.")
    };

    public static ActionOutcome DeleteSession(SessionDeleteResult result) => result.Result switch
    {
        SessionActionResult.Success => new(true,
            $"Session deleted — {result.CandidatesRemoved} candidate(s), {result.PaymentsRemoved} payment(s), "
            + $"and {result.VeAssignmentsRemoved} VE roster assignment(s) removed with it."),
        SessionActionResult.Blocked => new(false,
            "Could not delete session — one of its payments is still referenced by an unmatched Square "
            + "payment record. Resolve that first."),
        SessionActionResult.NotFound => new(false, "Session not found."),
        _ => new(false, "Could not delete session.")
    };

    public static ActionOutcome SetRetainedAmountOverride(SessionActionResult result, decimal? amount) => result switch
    {
        SessionActionResult.Success when amount is null => new(true, "Retained amount override cleared."),
        SessionActionResult.Success => new(true,
            $"Retained amount overridden to {Usd.Format(amount!.Value)} for this session."),
        SessionActionResult.NotFound => new(false, "Session not found."),
        _ => new(false, "Could not update retained amount override.")
    };

    // ---- Candidate actions -----------------------------------------------------------------

    public static ActionOutcome MarkFailed(CandidateActionResult result) =>
        Candidate(result, "Candidate marked failed.", "Could not mark candidate failed.");

    public static ActionOutcome DeleteCandidate(CandidateActionResult result) => result switch
    {
        // The only action that can hit AlreadyTested, and the one message here worth being specific
        // about: it is the difference between "try again" and "this is no longer possible".
        CandidateActionResult.AlreadyTested => new(false,
            "Could not delete candidate — testing already completed for this session."),
        _ => Candidate(result, "Candidate marked as withdrew/no-show; PII cleared.",
            "Could not delete candidate.")
    };

    public static ActionOutcome SetFrn(CandidateActionResult result) =>
        Candidate(result, "FRN updated.", "Could not update FRN.");

    /// <summary>
    /// Rejected before the service is called, so it has no <see cref="CandidateActionResult"/> of its
    /// own — the service trusts the caller to have checked, same division as
    /// <c>SetRetainedAmountOverride</c>'s parse.
    /// </summary>
    public static ActionOutcome BlankFrn() => new(false, "FRN cannot be blank.");

    public static ActionOutcome MarkPaid(CandidateActionResult result) =>
        Candidate(result, "Payment marked paid.", "Could not mark payment paid.");

    public static ActionOutcome FlagRefund(CandidateActionResult result) =>
        Candidate(result, "Refund requested flagged.", "Could not flag refund requested.");

    /// <summary>
    /// Issuing a real refund through Square (#375) — used by both entry points, the candidate's
    /// payment and an unmatched payment.
    ///
    /// <para><b>Success does not say "refunded".</b> Square accepts a refund immediately and then
    /// takes up to 14 days to settle a card or bank transfer, and it can still end rejected. The
    /// pending message says submitted and says why it is not instant; only a Completed status claims
    /// the money went back. Telling a Session Manager a refund is done when Square has not finished
    /// is the specific failure this wording exists to avoid — they would close the loop with the
    /// candidate and never look again.</para>
    ///
    /// <para><see cref="RefundResult.CallFailed"/> is the other one worth reading carefully: it tells
    /// the user <i>not</i> to retry by hand. The refund row is persisted with its idempotency key and
    /// the status job re-sends it, so clicking again is at best redundant — and the instinct after an
    /// error message is to click again.</para>
    /// </summary>
    public static ActionOutcome IssueRefund(RefundOutcome outcome, decimal amountUsd) => outcome.Result switch
    {
        RefundResult.Success when outcome.Status == RefundStatus.Completed => new(true,
            $"Refund completed — {Usd.Format(amountUsd)} returned to the buyer."),
        RefundResult.Success => new(true,
            $"Refund of {Usd.Format(amountUsd)} submitted to Square. Card refunds usually clear within "
            + "a few hours but can take up to 14 days — this page will show it as completed once Square confirms."),
        RefundResult.NotFound => new(false, "Payment not found."),
        RefundResult.NoSquarePaymentId => new(false,
            "Could not refund — this payment was recorded before the app began storing Square's payment id, "
            + "so it has to be refunded from the Square dashboard."),
        RefundResult.NotPaid => new(false, "Could not refund — this payment is not marked paid."),
        RefundResult.SquareNotConfigured => new(false,
            "Could not refund — this team has no Square credentials configured."),
        RefundResult.SquareSwitchedOff => new(false,
            "Could not refund — Square is switched off for this team."),
        RefundResult.TooOld => new(false,
            "Could not refund — Square will not refund a payment taken more than a year ago."),
        RefundResult.RefundLimitReached => new(false,
            "Could not refund — Square allows at most 20 refunds against one payment."),
        RefundResult.AmountInvalid => new(false,
            $"Could not refund — enter an amount between {Usd.Format(0.01m)} and "
            + $"{Usd.Format(outcome.RemainingRefundableUsd ?? 0m)}, which is what is left to refund."),
        RefundResult.SquareRefused => new(false,
            $"Square refused the refund. {outcome.Detail}"),
        _ => new(false,
            "Could not reach Square. The refund has been recorded and will be sent again automatically — "
            + "do not issue it a second time.")
    };

    public static ActionOutcome CreateRetestPayment(CandidateActionResult result) =>
        Candidate(result, "Retest payment created.",
            "Could not create retest payment — candidate must be marked Failed first.");

    // ---- Candidate emails ------------------------------------------------------------------

    public static ActionOutcome ResendConfirmation(CandidateEmailSendResult result) =>
        Email(result, "Confirmation email resent.", "resend confirmation email");

    public static ActionOutcome SendFelonyInstructions(CandidateEmailSendResult result) =>
        Email(result, "Felony disclosure instructions sent.", "send felony disclosure instructions");

    public static ActionOutcome SendYouthProgram(CandidateEmailSendResult result) =>
        Email(result, "Youth program instructions sent.", "send youth program instructions");

    // ---- Shared shapes ---------------------------------------------------------------------

    /// <summary>
    /// The failure branches every candidate action shares. <c>NotFound</c> and <c>InvalidState</c>
    /// stay behind the caller's own wording because what "invalid state" means is specific to the
    /// action — for a retest payment it is "not marked failed yet", which the caller says.
    /// </summary>
    private static ActionOutcome Candidate(CandidateActionResult result, string success, string failure) =>
        result switch
        {
            CandidateActionResult.Success => new(true, success),
            CandidateActionResult.NotFound => new(false, "Candidate not found."),
            CandidateActionResult.AlreadyDone => new(false, "That has already been done."),
            _ => new(false, failure)
        };

    /// <summary>
    /// Email failures were reported by interpolating the raw enum name into the sentence — so a VEC
    /// with no youth program told the Session Manager
    /// "Could not send youth program instructions: VecDoesNotSupportYouthProgram." Each value gets a
    /// sentence instead; <paramref name="verbPhrase"/> is the action, lower case, for the fallback.
    /// </summary>
    private static ActionOutcome Email(CandidateEmailSendResult result, string success, string verbPhrase) =>
        result switch
        {
            CandidateEmailSendResult.Sent => new(true, success),
            CandidateEmailSendResult.CandidateNotFound => new(false, "Candidate not found."),
            CandidateEmailSendResult.NoEmailAddress => new(false,
                $"Could not {verbPhrase} — no email address on file for this candidate."),
            CandidateEmailSendResult.EmailNotConfigured => new(false,
                $"Could not {verbPhrase} — this team has no email settings configured."),
            CandidateEmailSendResult.TemplateMissing => new(false,
                $"Could not {verbPhrase} — the email template is missing."),
            CandidateEmailSendResult.VecDoesNotSupportYouthProgram => new(false,
                $"Could not {verbPhrase} — this session's VEC does not run a youth program."),
            CandidateEmailSendResult.NoFelonyDisclosure => new(false,
                $"Could not {verbPhrase} — this candidate has not declared a felony disclosure."),
            _ => new(false, $"Could not {verbPhrase}.")
        };
}
