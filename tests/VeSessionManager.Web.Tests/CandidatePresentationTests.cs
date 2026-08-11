using VeSessionManager.Core.Entities;
using VeSessionManager.Web;
using Xunit;

namespace VeSessionManager.Web.Tests;

/// <summary>
/// The point of <see cref="CandidatePresentation"/> is that one status produces one label. Before
/// it, <see cref="CandidateApplicationStatus.NotTested"/> rendered as "Not tested" on a candidate's
/// own record and as "Withdrew/no-show" in the "other attempts" list on that same page — so someone
/// comparing two of their own attempts saw two different words for one thing.
/// </summary>
public class CandidatePresentationTests
{
    [Fact]
    public void NotTestedHasExactlyOneLabelEverywhere()
    {
        Assert.Equal("Not tested", CandidatePresentation.StatusLabel(CandidateApplicationStatus.NotTested));
    }

    /// <summary>
    /// The other four read correctly as their own names, which is why only one arm translates. If a
    /// status is ever added that does not, it belongs in the helper rather than at a call site.
    /// </summary>
    [Theory]
    [InlineData(CandidateApplicationStatus.Unmatched, "Unmatched")]
    [InlineData(CandidateApplicationStatus.Received, "Received")]
    [InlineData(CandidateApplicationStatus.Granted, "Granted")]
    [InlineData(CandidateApplicationStatus.Failed, "Failed")]
    public void OtherStatusesUseTheirOwnNames(CandidateApplicationStatus status, string expected)
    {
        Assert.Equal(expected, CandidatePresentation.StatusLabel(status));
    }

    /// <summary>
    /// A withdrawn candidate's personal details are purged immediately, so there is genuinely
    /// nothing to show — and the stand-in must not be mistaken for a missing-data bug.
    /// </summary>
    [Fact]
    public void AWithdrawnCandidateShowsTheClearedStandInRatherThanAStaleName()
    {
        var candidate = new Candidate
        {
            Name = "should not be shown",
            ApplicationStatus = CandidateApplicationStatus.NotTested
        };

        Assert.True(candidate.IsWithdrawn);
        Assert.Equal("Withdrew — PII cleared", CandidatePresentation.DisplayName(candidate));
    }

    [Fact]
    public void ANormalCandidateShowsTheirName()
    {
        var candidate = new Candidate { Name = "Terrance A Harris", ApplicationStatus = CandidateApplicationStatus.Received };

        Assert.False(candidate.IsWithdrawn);
        Assert.Equal("Terrance A Harris", CandidatePresentation.DisplayName(candidate));
    }

    /// <summary>
    /// A candidate purged on the retention schedule rather than by withdrawing: not withdrawn, but
    /// the name is gone. An em dash rather than an empty cell, so the row still reads as a row.
    /// </summary>
    [Fact]
    public void APurgedButNotWithdrawnCandidateFallsBackToADash()
    {
        var candidate = new Candidate { Name = null, ApplicationStatus = CandidateApplicationStatus.Granted };

        Assert.Equal("—", CandidatePresentation.DisplayName(candidate));
    }
}
