using VeSessionManager.Core.Ingestion;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// A manual refresh must not report success after the pipeline failed (#242).
///
/// <para><b>The shape of the original bug.</b> <c>JobRunHistoryLogger</c> catches and does not
/// rethrow — deliberately, because that is what stops one team's bad row taking down the Worker. So
/// a pipeline whose every step threw returned <c>(0, 0, 0)</c>, which is byte-identical to a run
/// that simply had nothing to do. Both page handlers then rendered the same green sentence:</para>
///
/// <para><i>"Refreshed HRCC — 0 new candidate(s), 0 updated, 0 confirmation email(s) sent."</i></para>
///
/// <para>An admin clicking "Refresh now" on a team with wrong ExamTools credentials was told it
/// worked. This is the documented <c>sent 0, failed 1</c> shape — the counts are true and the
/// conclusion drawn from them is false.</para>
///
/// <para>Tested at the message level rather than through the pipeline: what was wrong was never the
/// counting, it was the sentence built from it, and that sentence now has one home.</para>
/// </summary>
public class ManualRefreshFailureReportingTests
{
    [Fact]
    public void ACleanRunReportsTheCounts()
    {
        var result = new ManualRefreshResult(CandidatesAdded: 3, CandidatesUpdated: 1, ConfirmationEmailsSent: 2, FailedSteps: 0);

        var (success, message) = result.Describe("HRCC");

        Assert.True(success);
        Assert.Contains("3 new candidate(s)", message);
        Assert.Contains("HRCC", message);
    }

    /// <summary>
    /// The exact case from the finding: every count zero because every step threw. This must not be
    /// success, and it must not recite the zeros — printing "0 new candidate(s)" beside a failure is
    /// what made the original message so convincing.
    /// </summary>
    [Fact]
    public void ATotalFailureIsNotReportedAsSuccess()
    {
        var result = new ManualRefreshResult(CandidatesAdded: 0, CandidatesUpdated: 0, ConfirmationEmailsSent: 0, FailedSteps: 6);

        var (success, message) = result.Describe("HRCC");

        Assert.False(success);
        Assert.Contains("6 step(s) failed", message);
        Assert.DoesNotContain("0 new candidate(s)", message);
        Assert.Contains("Job History", message);
    }

    /// <summary>
    /// A run that genuinely had nothing to do looks identical in its counts and must still read as
    /// success — otherwise the fix trades a false success for a false alarm, and an alarm that fires
    /// on every quiet refresh is one nobody reads.
    /// </summary>
    [Fact]
    public void AQuietRunWithNothingToDoIsStillSuccess()
    {
        var result = new ManualRefreshResult(CandidatesAdded: 0, CandidatesUpdated: 0, ConfirmationEmailsSent: 0, FailedSteps: 0);

        var (success, message) = result.Describe("HRCC");

        Assert.True(success);
        Assert.Contains("0 new candidate(s)", message);
    }

    /// <summary>
    /// Partial failure is the case most likely to mislead: some steps worked, so there are real
    /// counts to show, and showing them alone would imply the whole run succeeded.
    /// </summary>
    [Fact]
    public void APartialFailureIsReportedAsFailureEvenWithRealCounts()
    {
        var result = new ManualRefreshResult(CandidatesAdded: 5, CandidatesUpdated: 0, ConfirmationEmailsSent: 0, FailedSteps: 2);

        var (success, message) = result.Describe("HRCC");

        Assert.False(success);
        Assert.Contains("2 step(s) failed", message);
    }

    /// <summary>The session-scoped button names no team; the two call sites differ only in that.</summary>
    [Fact]
    public void TheSessionScopedMessageNamesNoTeam()
    {
        var clean = new ManualRefreshResult(1, 0, 0, 0).Describe(teamName: null);
        var failed = new ManualRefreshResult(0, 0, 0, 3).Describe(teamName: null);

        Assert.StartsWith("Refreshed —", clean.Message);
        Assert.StartsWith("Refreshed —", failed.Message);
        Assert.False(failed.Success);
    }
}
