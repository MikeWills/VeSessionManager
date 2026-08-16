using VeSessionManager.Core.Entities;

namespace VeSessionManager.Web;

/// <summary>
/// Which candidate actions are offered for one candidate — the single definition, shared by the
/// session detail roster and the candidate detail page (issues #274, #304).
///
/// <para><b>The bug this closes.</b> These eight flags were computed twice, and one of them had
/// already drifted: <c>CanSendYouthProgram</c> was gated on the session VEC's youth-program flag on
/// <c>CandidateDetail</c> and on nothing but "not withdrawn" on <c>Detail</c>. So the session roster
/// offered the button for every VEC, and clicking it interpolated a raw enum name into the error
/// message. Two copies of one rule, and only one of them knew the rule.</para>
///
/// <para><b>These are not the authorization check.</b> Every one of them is about whether an action
/// is <i>applicable</i>, and the Core service re-decides that for itself on every call — a candidate
/// with no email still cannot be sent one by posting the form directly. What these do is stop the app
/// offering a button whose only possible outcome is an error, which is exactly what #274 was.</para>
/// </summary>
/// <param name="AwaitingFelonyInstructions">
/// Declared a disclosure and has not been sent the instructions — the marker that replaces the
/// automatic send removed in #221. Not a capability but computed from the same state, and kept here
/// so the two do not drift apart either.
/// </param>
/// <param name="CanReceiveEmail">
/// Whether a hand-composed email can reach this candidate at all (#144) — the checkbox state on the
/// Email candidates screen. Identical in form to <see cref="CanResendConfirmation"/> and kept
/// separate anyway: the two answer different questions ("can we re-send the registration email" vs
/// "can this person be written to"), and a future rule about one should not silently change the
/// other.
/// </param>
public readonly record struct CandidateCapabilities(
    bool CanResendConfirmation,
    bool CanDelete,
    bool CanMarkFailed,
    bool CanCreateRetestPayment,
    bool CanFlagRefund,
    bool CanSendYouthProgram,
    bool CanSendFelonyInstructions,
    bool AwaitingFelonyInstructions,
    bool CanReceiveEmail)
{
    /// <param name="vecSupportsYouthProgram">
    /// Passed in rather than read from <c>candidate.Session.Vec</c>, because the two callers load it
    /// differently — <c>Detail</c> has one <c>Session</c> with its <c>Vec</c> included and maps many
    /// candidates against it, <c>CandidateDetail</c> includes it per candidate. Taking the flag makes
    /// the requirement explicit at both call sites instead of depending on an <c>Include</c> thirty
    /// lines away, which is how one of them came to omit the check entirely.
    /// </param>
    /// <param name="hasAnyPayment">
    /// Whether the candidate has a payment at all. Same reasoning: the roster has already picked out a
    /// primary payment for its chip and the detail page has the full list, so neither should have to
    /// re-query and both must mean the same thing by it.
    /// </param>
    public static CandidateCapabilities For(Candidate candidate, bool vecSupportsYouthProgram, bool hasAnyPayment)
    {
        // Withdrawn candidates keep a row for statistics with their PII cleared, so every action that
        // acts on a person is off. This is the single most repeated clause in both originals.
        var active = !candidate.IsWithdrawn;

        return new CandidateCapabilities(
            CanResendConfirmation: active && candidate.Email is not null,
            CanDelete: active && !candidate.Tested,
            CanMarkFailed: active && candidate.ApplicationStatus
                is CandidateApplicationStatus.Unmatched or CandidateApplicationStatus.Received,
            CanCreateRetestPayment: active && candidate.ApplicationStatus == CandidateApplicationStatus.Failed,
            CanFlagRefund: active && hasAnyPayment,
            // The #274 fix. CandidateNotificationService refuses with VecDoesNotSupportYouthProgram
            // otherwise, so without this the button's only outcome is an error.
            CanSendYouthProgram: active && vecSupportsYouthProgram,
            // Not gated on Tested: the useful time to send this is before the session (#221).
            CanSendFelonyInstructions: active && candidate.HasFelonyDisclosure == true && candidate.Email is not null,
            AwaitingFelonyInstructions: active && candidate.HasFelonyDisclosure == true
                && candidate.FelonyDisclosureInstructionsSentUtc is null,
            // A withdrawn candidate's PII is cleared immediately, so there is usually no address left
            // to write to anyway — but the rule is stated rather than relied upon.
            CanReceiveEmail: active && !string.IsNullOrWhiteSpace(candidate.Email));
    }
}
