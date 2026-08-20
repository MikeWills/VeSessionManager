using VeSessionManager.Core.Entities;
using VeSessionManager.Web;

namespace VeSessionManager.Web.Tests;

/// <summary>
/// Issue #195 — how the stored FCC application timeline is described to a Session Manager.
/// </summary>
public class UlsTimelinePresentationTests
{
    private static CandidateUlsHistoryEntry Entry(DateTime? logged, string code, string? text = null)
        => new() { LogDateUtc = logged, Code = code, CodeText = text };

    [Fact]
    public void EachEntry_ShowsFccsOwnWords()
    {
        var lines = CandidatePresentation.ApplicationTimeline([Entry(new DateTime(2026, 8, 4), "RDLCOM", "Redlight Review Completed")]);

        var line = Assert.Single(lines);
        Assert.Equal("Redlight Review Completed", line.Description);
        Assert.Equal("RDLCOM", line.Code);
    }

    /// <summary>
    /// ⚠️ The trap this page already documents for the two FCC date fields beside it, and the reason
    /// the timeline must not be "helpfully" run through <c>EasternTimeFormatter</c>: FCC dates are
    /// date-only, stored at UTC midnight. Converting one to Eastern shifts it back a calendar day
    /// (UTC midnight is 8pm the previous day in ET), which misreports the date FCC actually gave
    /// rather than merely relabelling its timezone.
    /// </summary>
    [Fact]
    public void TheDate_IsFccsCalendarDate_NotShiftedIntoEastern()
        => Assert.Equal("8/4/2026", Assert.Single(CandidatePresentation.ApplicationTimeline([Entry(new DateTime(2026, 8, 4), "RDLCOM")])).DateLine);

    /// <summary>Missing text degrades to the code, never to a blank row — jargon beats nothing.</summary>
    [Fact]
    public void AnEntryWithNoText_FallsBackToItsCode()
        => Assert.Equal("BQCOM", Assert.Single(CandidatePresentation.ApplicationTimeline([Entry(new DateTime(2026, 8, 4), "BQCOM")])).Description);

    /// <summary>An undated entry is still shown — a missing date is not a reason to hide that the action happened.</summary>
    [Fact]
    public void AnUndatedEntry_IsStillShown_WithADash()
    {
        var line = Assert.Single(CandidatePresentation.ApplicationTimeline([Entry(null, "RDLOFF", "Redlight Review Initiated")]));

        Assert.Equal("—", line.DateLine);
        Assert.Equal("Redlight Review Initiated", line.Description);
    }

    /// <summary>
    /// The code is shown beside the words rather than instead of them. It is what
    /// <c>ResolveHoldReason</c> matches on, so when somebody asks why a candidate shows a hold, the
    /// answer is on screen rather than in the source.
    /// </summary>
    [Fact]
    public void TheRawCode_IsKeptAlongsideTheDescription()
    {
        var line = Assert.Single(CandidatePresentation.ApplicationTimeline([Entry(new DateTime(2026, 8, 1), "RDLOFF", "Redlight Review Initiated")]));

        Assert.Equal("RDLOFF", line.Code);
        Assert.NotEqual(line.Code, line.Description);
    }

    [Fact]
    public void NoEntries_IsAnEmptyTimeline_NotAnError()
        => Assert.Empty(CandidatePresentation.ApplicationTimeline([]));
}
