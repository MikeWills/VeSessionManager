using VeSessionManager.Core.Entities;
using VeSessionManager.Web;
using Xunit;

namespace VeSessionManager.Web.Tests;

/// <summary>
/// The shared capability rules (issues #274, #304).
///
/// <para><b>#274 in one line:</b> the session roster offered "Send youth program instructions" for
/// every VEC, because that copy of the rule did not consult the VEC's youth-program flag while the
/// other copy did. The service refuses, so the button's only possible outcome was an error.</para>
/// </summary>
public class CandidateCapabilitiesTests
{
    private static Candidate Active(Action<Candidate>? tweak = null)
    {
        var candidate = new Candidate
        {
            Id = 1,
            SessionId = 1,
            Name = "Pat Example",
            Email = "pat@example.com",
            ApplicationStatus = CandidateApplicationStatus.Received
        };

        tweak?.Invoke(candidate);
        return candidate;
    }

    // ---- #274 ------------------------------------------------------------------------------

    [Fact]
    public void CanSendYouthProgram_IsFalseWhenTheVecDoesNotRunOne()
    {
        var can = CandidateCapabilities.For(Active(), vecSupportsYouthProgram: false, hasAnyPayment: true);
        Assert.False(can.CanSendYouthProgram);
    }

    [Fact]
    public void CanSendYouthProgram_IsTrueWhenTheVecRunsOne()
    {
        var can = CandidateCapabilities.For(Active(), vecSupportsYouthProgram: true, hasAnyPayment: true);
        Assert.True(can.CanSendYouthProgram);
    }

    // ---- The clause every capability shares --------------------------------------------------

    /// <summary>
    /// A withdrawn candidate keeps a row for statistics with their PII cleared. Every action acts on a
    /// person, so every one of these is off — the single most repeated clause in both originals, and
    /// the one most likely to be forgotten when an eighth flag is added.
    /// </summary>
    [Fact]
    public void AWithdrawnCandidateCanDoNothingAtAll()
    {
        var withdrawn = Active(c =>
        {
            // NotTested is what IsWithdrawn actually reads — see Candidate.IsWithdrawn.
            c.ApplicationStatus = CandidateApplicationStatus.NotTested;
            c.HasFelonyDisclosure = true;
        });

        var can = CandidateCapabilities.For(withdrawn, vecSupportsYouthProgram: true, hasAnyPayment: true);

        Assert.True(withdrawn.IsWithdrawn, "fixture no longer produces a withdrawn candidate");
        Assert.False(can.CanResendConfirmation);
        Assert.False(can.CanDelete);
        Assert.False(can.CanMarkFailed);
        Assert.False(can.CanCreateRetestPayment);
        Assert.False(can.CanFlagRefund);
        Assert.False(can.CanSendYouthProgram);
        Assert.False(can.CanSendFelonyInstructions);
        Assert.False(can.AwaitingFelonyInstructions);
    }

    // ---- The rest ---------------------------------------------------------------------------

    [Fact]
    public void CanResendConfirmation_NeedsAnEmailAddress()
    {
        Assert.True(CandidateCapabilities.For(Active(), false, false).CanResendConfirmation);
        Assert.False(CandidateCapabilities.For(Active(c => c.Email = null), false, false).CanResendConfirmation);
    }

    [Fact]
    public void CanDelete_StopsOnceTheCandidateHasTested()
    {
        Assert.True(CandidateCapabilities.For(Active(), false, false).CanDelete);
        Assert.False(CandidateCapabilities.For(Active(c => c.Tested = true), false, false).CanDelete);
    }

    [Theory]
    [InlineData(CandidateApplicationStatus.Unmatched, true)]
    [InlineData(CandidateApplicationStatus.Received, true)]
    [InlineData(CandidateApplicationStatus.Granted, false)]
    [InlineData(CandidateApplicationStatus.Failed, false)]
    public void CanMarkFailed_OnlyBeforeAnOutcomeIsKnown(CandidateApplicationStatus status, bool expected)
    {
        Assert.Equal(expected,
            CandidateCapabilities.For(Active(c => c.ApplicationStatus = status), false, false).CanMarkFailed);
    }

    [Fact]
    public void CanCreateRetestPayment_OnlyAfterFailing()
    {
        Assert.True(CandidateCapabilities.For(
            Active(c => c.ApplicationStatus = CandidateApplicationStatus.Failed), false, false).CanCreateRetestPayment);
        Assert.False(CandidateCapabilities.For(Active(), false, false).CanCreateRetestPayment);
    }

    [Fact]
    public void CanFlagRefund_NeedsAPaymentToRefund()
    {
        Assert.True(CandidateCapabilities.For(Active(), false, hasAnyPayment: true).CanFlagRefund);
        Assert.False(CandidateCapabilities.For(Active(), false, hasAnyPayment: false).CanFlagRefund);
    }

    /// <summary>
    /// Deliberately not gated on <c>Tested</c>: the useful time to send this is <i>before</i> the
    /// session, which is the whole point of #221 making it manual.
    /// </summary>
    [Fact]
    public void CanSendFelonyInstructions_DoesNotWaitForTheCandidateToHaveTested()
    {
        var declared = Active(c => c.HasFelonyDisclosure = true);

        Assert.True(CandidateCapabilities.For(declared, false, false).CanSendFelonyInstructions);
        Assert.False(CandidateCapabilities.For(Active(), false, false).CanSendFelonyInstructions);
        Assert.False(CandidateCapabilities.For(
            Active(c => { c.HasFelonyDisclosure = true; c.Email = null; }), false, false).CanSendFelonyInstructions);
    }

    /// <summary>
    /// The marker that replaced the automatic send. It has to clear once the instructions go out, or
    /// the session row nags forever about work that is done.
    /// </summary>
    [Fact]
    public void AwaitingFelonyInstructions_ClearsOnceTheyHaveBeenSent()
    {
        Assert.True(CandidateCapabilities.For(
            Active(c => c.HasFelonyDisclosure = true), false, false).AwaitingFelonyInstructions);

        Assert.False(CandidateCapabilities.For(Active(c =>
        {
            c.HasFelonyDisclosure = true;
            c.FelonyDisclosureInstructionsSentUtc = new DateTime(2026, 8, 11, 12, 0, 0, DateTimeKind.Utc);
        }), false, false).AwaitingFelonyInstructions);
    }
}
