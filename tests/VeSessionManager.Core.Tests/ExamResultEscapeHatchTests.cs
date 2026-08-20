using VeSessionManager.Core.Entities;
using VeSessionManager.Core.ExamResults;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// The escape-hatch promise, made checkable.
///
/// <para><c>ExamResultSyncService.SyncSessionAsync</c> is documented as the unbounded re-read for a
/// session the routine sweep no longer reaches. Both entry points share one filter, so every bound
/// added for the scheduled scan silently applied to the escape hatch too — and the promise, living
/// only in a doc comment, quietly became false <b>twice</b>:</para>
/// <list type="number">
///   <item><b>2026-08-03</b> — the manual refresh ran <c>RunAsync</c>, so <c>ResultSyncWindow</c>
///   applied regardless and the escape hatch did not exist at all.</item>
///   <item><b>2026-08-20</b> — the settled gate from #437 was inherited, so a license class frozen
///   too low could not be repaired by pressing Refresh: the bound excluded precisely the candidates
///   needing repair, since a frozen class is normally noticed after the session is finalized.</item>
/// </list>
///
/// <para>Both times the prose and the code disagreed and nothing failed. These tests are the third
/// thing that has to change — alongside the two named buckets in
/// <see cref="ExamResultCandidateFilter"/> — before the promise can silently break again.</para>
/// </summary>
public class ExamResultEscapeHatchTests
{
    private static Session ClosedSession() => NewSession(new DateTime(2026, 8, 20));
    private static Session OpenSession() => NewSession(null);

    private static Session NewSession(DateTime? closedUtc) => new()
    {
        ExamToolsSessionId = "session-1",
        Title = "Test Session",
        ExamToolsClosedUtc = closedUtc
    };

    private static Candidate Settled() => new()
    {
        ExamToolsApplicantId = "a1",
        Tested = true,
        NewLicenseClass = LicenseClass.General
    };

    /// <summary>
    /// The promise itself. Whatever the scheduled scan skips to save a call, the human-triggered path
    /// still reads — that is the entire meaning of "escape hatch", and it is what was false twice.
    /// </summary>
    [Fact]
    public void EverythingTheScheduledScanSkipsForCost_TheManualPathStillReads()
    {
        var session = ClosedSession();
        var candidate = Settled();

        Assert.True(ExamResultCandidateFilter.IsSettledForNow(candidate, session));   // the scheduled scan's economy...
        Assert.False(ExamResultCandidateFilter.ShouldRead(candidate, session, includeSettled: false));
        Assert.True(ExamResultCandidateFilter.ShouldRead(candidate, session, includeSettled: true)); // ...is not the escape hatch's
    }

    /// <summary>
    /// The general form, stated so a future bound is covered by intent rather than by luck: for
    /// <b>any</b> candidate the feed is allowed to speak for, being "settled" must never be what stops
    /// the manual path. If a new cost bound is added to <see cref="ExamResultCandidateFilter"/>'s
    /// scheduled-only bucket, this keeps holding; if it is added to the wrong bucket, this fails.
    /// </summary>
    [Theory]
    [InlineData(true, true)]    // settled, closed session — the #437 repair case
    [InlineData(true, false)]   // tested with a class, session still open
    [InlineData(false, true)]   // not yet tested, session closed
    [InlineData(false, false)]  // nothing settled at all
    public void TheManualPath_ReadsEveryCandidateTheFeedMaySpeakFor(bool settled, bool sessionClosed)
    {
        var session = sessionClosed ? ClosedSession() : OpenSession();
        var candidate = settled ? Settled() : new Candidate { ExamToolsApplicantId = "a1" };

        Assert.True(ExamResultCandidateFilter.ShouldRead(candidate, session, includeSettled: true));
    }

    /// <summary>
    /// The other half, and the reason the buckets are named rather than merged. These exclusions are
    /// about what the feed is <b>allowed</b> to say, not about cost — so the escape hatch must not
    /// lift them. A Session Manager's Failed verdict being undone by somebody pressing Refresh would
    /// be a much easier accident than the scheduled job doing it.
    /// </summary>
    [Fact]
    public void AHumanFailedVerdict_SurvivesEvenTheEscapeHatch()
    {
        var candidate = new Candidate
        {
            ExamToolsApplicantId = "a1",
            ApplicationStatus = CandidateApplicationStatus.Failed,
            ResultMarkedByUserId = 7
        };

        Assert.False(ExamResultCandidateFilter.ShouldRead(candidate, OpenSession(), includeSettled: true));
        Assert.False(ExamResultCandidateFilter.ShouldRead(candidate, OpenSession(), includeSettled: false));
    }

    /// <summary>An auto-failed row is re-examined on both paths — that is what made the old pass-one-fail-one bug self-correcting.</summary>
    [Fact]
    public void AnAutoFailedRow_IsStillReExaminedOnBothPaths()
    {
        var candidate = new Candidate
        {
            ExamToolsApplicantId = "a1",
            ApplicationStatus = CandidateApplicationStatus.Failed,
            ResultMarkedByUserId = null
        };

        Assert.True(ExamResultCandidateFilter.ShouldRead(candidate, OpenSession(), includeSettled: false));
        Assert.True(ExamResultCandidateFilter.ShouldRead(candidate, OpenSession(), includeSettled: true));
    }

    [Fact]
    public void AWithdrawnCandidate_IsReadOnNeitherPath()
    {
        var candidate = new Candidate
        {
            ExamToolsApplicantId = "a1",
            ApplicationStatus = CandidateApplicationStatus.NotTested
        };

        Assert.False(ExamResultCandidateFilter.ShouldRead(candidate, OpenSession(), includeSettled: true));
    }

    /// <summary>No ExamTools id, nothing to fetch — on any path.</summary>
    [Fact]
    public void ACandidateWithNoExamToolsId_IsReadOnNeitherPath()
    {
        var candidate = new Candidate { ExamToolsApplicantId = null };

        Assert.False(ExamResultCandidateFilter.ShouldRead(candidate, OpenSession(), includeSettled: true));
        Assert.False(ExamResultCandidateFilter.ShouldRead(candidate, OpenSession(), includeSettled: false));
    }

    /// <summary>
    /// ⚠️ An open session is never settled, however complete the record looks. Grading is entered
    /// element by element, so "Tested with a class" on an open session means "as of the last poll" —
    /// which is the whole of #437.
    /// </summary>
    [Fact]
    public void AnOpenSession_IsNeverSettled_HoweverCompleteTheRecordLooks()
        => Assert.False(ExamResultCandidateFilter.IsSettledForNow(Settled(), OpenSession()));
}
