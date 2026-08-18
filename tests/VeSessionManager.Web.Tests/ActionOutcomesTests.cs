using VeSessionManager.Core.CandidateActions;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Notifications;
using VeSessionManager.Core.Payments;
using VeSessionManager.Core.Sessions;
using VeSessionManager.Core.VecSubmissions;
using VeSessionManager.Web;
using Xunit;

namespace VeSessionManager.Web.Tests;

/// <summary>
/// The outcome table (issues #244, #304).
///
/// <para><b>What #244 actually was.</b> Not a wrong string — a <i>missing branch</i>. Three enum
/// values, two branches, so the third fell into the second's message and told the user the opposite
/// of what happened. Every test here is aimed at that shape: an outcome that says something meant for
/// a different outcome.</para>
/// </summary>
public class ActionOutcomesTests
{
    // ---- #244 ------------------------------------------------------------------------------

    /// <summary>
    /// The regression itself. <c>SessionNotFound</c> used to report "Session is already marked
    /// submitted" on the session list, which is not merely unhelpful — it asserts the action was
    /// unnecessary when in fact it did not happen.
    /// </summary>
    [Fact]
    public void MarkSubmittedToVec_TellsTheThreeOutcomesApart()
    {
        var marked = ActionOutcomes.MarkSubmittedToVec(VecSubmissionMarkResult.Marked);
        var already = ActionOutcomes.MarkSubmittedToVec(VecSubmissionMarkResult.AlreadySubmitted);
        var missing = ActionOutcomes.MarkSubmittedToVec(VecSubmissionMarkResult.SessionNotFound);

        Assert.True(marked.Success);
        Assert.False(already.Success);
        Assert.False(missing.Success);

        Assert.Equal(3, new[] { marked.Message, already.Message, missing.Message }.Distinct().Count());
        Assert.DoesNotContain("already", missing.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The same collapse the audit flagged alongside it: both page models reported <c>AlreadyDone</c>
    /// and <c>NotFound</c> as the single sentence "Could not mark session completed."
    /// </summary>
    [Fact]
    public void MarkCompleted_TellsAlreadyDoneApartFromNotFound()
    {
        var already = ActionOutcomes.MarkCompleted(new SessionCompletionResult(SessionActionResult.AlreadyDone, 0));
        var missing = ActionOutcomes.MarkCompleted(new SessionCompletionResult(SessionActionResult.NotFound, 0));

        Assert.False(already.Success);
        Assert.False(missing.Success);
        Assert.NotEqual(already.Message, missing.Message);
    }

    /// <summary>
    /// Candidates awaiting felony instructions are named, not counted silently — the send became
    /// manual in #221, so this is the moment a Session Manager would otherwise assume it was handled.
    /// </summary>
    [Fact]
    public void MarkCompleted_SaysSoWhenCandidatesAreStillAwaitingFelonyInstructions()
    {
        var quiet = ActionOutcomes.MarkCompleted(new SessionCompletionResult(SessionActionResult.Success, 0));
        var waiting = ActionOutcomes.MarkCompleted(new SessionCompletionResult(SessionActionResult.Success, 2));

        Assert.True(quiet.Success);
        Assert.True(waiting.Success);
        Assert.DoesNotContain("felony", quiet.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2 candidate(s) declared a felony disclosure", waiting.Message);
    }

    // ---- The general property ---------------------------------------------------------------

    /// <summary>
    /// No action may report a failure using its success sentence, or vice versa. This is the weakest
    /// form of #244 and the one that generalizes: every mapping below is exercised across every value
    /// of its enum, so a branch added to an enum without a branch added here shows up as a failure
    /// wearing a success's words.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryMapping))]
    public void NoFailureBorrowsASuccessSentence(string action, Func<object, ActionOutcome> map, object[] values)
    {
        var outcomes = values.Select(map).ToList();
        var successes = outcomes.Where(o => o.Success).Select(o => o.Message).ToHashSet(StringComparer.Ordinal);
        var failures = outcomes.Where(o => !o.Success).Select(o => o.Message).ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(successes);
        Assert.NotEmpty(failures);

        var shared = successes.Intersect(failures).ToList();
        Assert.True(shared.Count == 0, $"{action}: same sentence for success and failure — {string.Join("; ", shared)}");

        Assert.DoesNotContain(outcomes, o => string.IsNullOrWhiteSpace(o.Message));
    }

    /// <summary>
    /// A raw enum name reaching the user is the tell that a branch is missing — the email actions used
    /// to interpolate the result directly, so a VEC with no youth program was told
    /// "Could not send youth program instructions: VecDoesNotSupportYouthProgram."
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryMapping))]
    public void NoOutcomeShowsTheUserARawEnumName(string action, Func<object, ActionOutcome> map, object[] values)
    {
        var names = values.Select(v => v switch
        {
            SessionCompletionResult r => r.Result.ToString(),
            SessionDeleteResult r => r.Result.ToString(),
            RefundOutcome r => r.Result.ToString(),
            _ => v.ToString()!
        }).Distinct().ToList();

        foreach (var outcome in values.Select(map))
        {
            foreach (var name in names.Where(n => n.Length > 6))
            {
                Assert.False(outcome.Message.Contains(name, StringComparison.Ordinal),
                    $"{action}: \"{outcome.Message}\" shows the user the raw enum name {name}");
            }
        }
    }

    public static TheoryData<string, Func<object, ActionOutcome>, object[]> EveryMapping()
    {
        object[] session = [.. Enum.GetValues<SessionActionResult>().Cast<object>()];
        object[] candidate = [.. Enum.GetValues<CandidateActionResult>().Cast<object>()];
        object[] email = [.. Enum.GetValues<CandidateEmailSendResult>().Cast<object>()];
        object[] vec = [.. Enum.GetValues<VecSubmissionMarkResult>().Cast<object>()];
        object[] completion = [.. Enum.GetValues<SessionActionResult>()
            .Select(r => (object)new SessionCompletionResult(r, 0))];
        object[] deletion = [.. Enum.GetValues<SessionActionResult>()
            .Select(r => (object)new SessionDeleteResult(r, 3, 2, 1))];
        // Success is expanded across every RefundStatus, not just the default: "submitted" and
        // "completed" are different claims about whether the buyer has their money, and the whole
        // point of the mapping is that only one of them says refunded (#375).
        object[] refunds = [.. Enum.GetValues<RefundResult>()
            .Select(r => (object)new RefundOutcome(r, "Square said no.", RefundStatus.Pending, 5m)),
            .. Enum.GetValues<RefundStatus>()
            .Select(s => (object)new RefundOutcome(RefundResult.Success, null, s, null))];

        return new TheoryData<string, Func<object, ActionOutcome>, object[]>
        {
            { "MarkSubmittedToVec", o => ActionOutcomes.MarkSubmittedToVec((VecSubmissionMarkResult)o), vec },
            { "MarkCompleted", o => ActionOutcomes.MarkCompleted((SessionCompletionResult)o), completion },
            { "ClearRescheduleFlag", o => ActionOutcomes.ClearRescheduleFlag((SessionActionResult)o), session },
            { "DeleteSession", o => ActionOutcomes.DeleteSession((SessionDeleteResult)o), deletion },
            { "SetRetainedAmountOverride", o => ActionOutcomes.SetRetainedAmountOverride((SessionActionResult)o, 12.50m), session },
            { "MarkFailed", o => ActionOutcomes.MarkFailed((CandidateActionResult)o), candidate },
            { "DeleteCandidate", o => ActionOutcomes.DeleteCandidate((CandidateActionResult)o), candidate },
            { "SetFrn", o => ActionOutcomes.SetFrn((CandidateActionResult)o), candidate },
            { "MarkPaid", o => ActionOutcomes.MarkPaid((CandidateActionResult)o), candidate },
            { "FlagRefund", o => ActionOutcomes.FlagRefund((CandidateActionResult)o), candidate },
            { "IssueRefund", o => ActionOutcomes.IssueRefund((RefundOutcome)o, 15m), refunds },
            { "CreateRetestPayment", o => ActionOutcomes.CreateRetestPayment((CandidateActionResult)o), candidate },
            { "ResendConfirmation", o => ActionOutcomes.ResendConfirmation((CandidateEmailSendResult)o), email },
            { "SendFelonyInstructions", o => ActionOutcomes.SendFelonyInstructions((CandidateEmailSendResult)o), email },
            { "SendYouthProgram", o => ActionOutcomes.SendYouthProgram((CandidateEmailSendResult)o), email }
        };
    }

    /// <summary>
    /// A refund Square has only accepted must not be reported as done (#375). Square answers
    /// immediately and takes up to 14 days to settle a card refund, so "refunded" at submit time is
    /// a claim about the buyer's money that nothing has established — and the Session Manager would
    /// close the loop with the candidate on the strength of it.
    /// </summary>
    [Fact]
    public void IssueRefund_OnlyClaimsTheMoneyWentBackWhenSquareSaysCompleted()
    {
        var pending = ActionOutcomes.IssueRefund(new RefundOutcome(RefundResult.Success, Status: RefundStatus.Pending), 15m);
        var completed = ActionOutcomes.IssueRefund(new RefundOutcome(RefundResult.Success, Status: RefundStatus.Completed), 15m);

        Assert.True(pending.Success);
        Assert.True(completed.Success);

        // Asserted on the claim rather than the word "completed", which the pending message may
        // legitimately use about the future ("will show it as completed once Square confirms").
        // What must not appear is the statement that the buyer has the money.
        Assert.Contains("submitted", pending.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("returned to the buyer", pending.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("returned to the buyer", completed.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("$15.00", pending.Message);
    }

    /// <summary>
    /// The instinct after an error is to click again, and here that would be an attempt to refund
    /// twice. The refund is already persisted with its idempotency key and gets re-sent by the
    /// status job, so the message has to say don't.
    /// </summary>
    [Fact]
    public void IssueRefund_TellsTheUserNotToRetryACallThatFailedInFlight()
    {
        var failed = ActionOutcomes.IssueRefund(new RefundOutcome(RefundResult.CallFailed, "timeout"), 15m);

        Assert.False(failed.Success);
        Assert.Contains("do not issue it a second time", failed.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An invalid amount says what the ceiling is — "invalid" alone leaves the user guessing at a number the app already knows.</summary>
    [Fact]
    public void IssueRefund_NamesTheRemainingAmountWhenTheOneEnteredIsTooLarge()
    {
        var invalid = ActionOutcomes.IssueRefund(
            new RefundOutcome(RefundResult.AmountInvalid, RemainingRefundableUsd: 4.50m), 15m);

        Assert.False(invalid.Success);
        Assert.Contains("$4.50", invalid.Message);
    }

    /// <summary>Money in a message goes through Usd, never a bare :F2 — see CLAUDE.md's Usd entry.</summary>
    [Fact]
    public void SetRetainedAmountOverride_FormatsTheAmountAsDollars()
    {
        var set = ActionOutcomes.SetRetainedAmountOverride(SessionActionResult.Success, 12.50m);
        var cleared = ActionOutcomes.SetRetainedAmountOverride(SessionActionResult.Success, null);

        Assert.Contains("$12.50", set.Message);
        Assert.True(cleared.Success);
        Assert.DoesNotContain("$", cleared.Message);
    }
}
