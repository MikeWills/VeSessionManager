using VeSessionManager.Core.Entities;
using VeSessionManager.Web;
using Xunit;

namespace VeSessionManager.Web.Tests;

/// <summary>
/// <see cref="SessionChips"/> is the one definition of the two session chips — but the session
/// list's *sort keys* cannot call it, because they run inside an EF expression tree and EF cannot
/// invoke a method. So the sort keys restate the same rules by hand, and these tests are what stop
/// the two drifting: they assert the inline SQL-bound spellings produce exactly the labels the chips
/// render.
///
/// <para>Sorting a column by something other than the text in it looks broken to the person reading
/// it, and has been reported as a bug here before.</para>
/// </summary>
public class SessionChipsTests
{
    /// <summary>
    /// Copied verbatim from Index.cshtml.cs's "status" sort key. If that changes and this does not,
    /// the comparison below fails.
    /// </summary>
    private static string InlineStatusSortKey(Session s) =>
        s.Status == SessionStatus.Cancelled ? "Cancelled"
        : s.RescheduleFlaggedForReview ? "Reschedule flagged"
        : s.TestingCompletedUtc != null || s.ExamToolsClosedUtc != null ? "Completed"
        : "Active";

    /// <summary>Copied verbatim from Index.cshtml.cs's "vecsubmission" sort key.</summary>
    private static string InlineVecSortKey(Session s) =>
        s.Status == SessionStatus.Cancelled ? "—"
        : s.VecSubmissionStatus == VecSubmissionStatus.Submitted ? "Submitted"
        : "Not submitted";

    /// <summary>Every combination that can reach a chip, including contradictory ones.</summary>
    public static TheoryData<SessionStatus, bool, DateTime?, DateTime?, VecSubmissionStatus> Matrix()
    {
        var data = new TheoryData<SessionStatus, bool, DateTime?, DateTime?, VecSubmissionStatus>();
        DateTime? stamp = new DateTime(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc);
        foreach (var status in new[] { SessionStatus.Active, SessionStatus.Cancelled })
        foreach (var flagged in new[] { false, true })
        foreach (var tested in new[] { (DateTime?)null, stamp })
        foreach (var closed in new[] { (DateTime?)null, stamp })
        foreach (var submitted in new[] { VecSubmissionStatus.NotSubmitted, VecSubmissionStatus.Submitted })
        {
            data.Add(status, flagged, tested, closed, submitted);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void ChipLabelsAndSortKeysAgree(
        SessionStatus status, bool flagged, DateTime? tested, DateTime? closed, VecSubmissionStatus submitted)
    {
        var session = new Session
        {
            ExamToolsSessionId = "x",
            Title = "x",
            Status = status,
            RescheduleFlaggedForReview = flagged,
            TestingCompletedUtc = tested,
            ExamToolsClosedUtc = closed,
            VecSubmissionStatus = submitted
        };

        Assert.Equal(
            SessionChips.Status(status, flagged, session.IsCompleted).Label,
            InlineStatusSortKey(session));

        Assert.Equal(
            SessionChips.VecSubmission(status, submitted).Label,
            InlineVecSortKey(session));
    }

    /// <summary>
    /// The priority order is the rule, not an accident — a cancelled session that also carries a
    /// reschedule flag is Cancelled, and a flagged session that has finished is still flagged,
    /// because the flag means "a human needs to look at this" and completion does not clear it.
    /// </summary>
    [Fact]
    public void ContradictoryStatesResolveByPriority()
    {
        Assert.Equal("Cancelled", SessionChips.Status(SessionStatus.Cancelled, rescheduleFlagged: true, isCompleted: true).Label);
        Assert.Equal("Reschedule flagged", SessionChips.Status(SessionStatus.Active, rescheduleFlagged: true, isCompleted: true).Label);
        Assert.Equal("Completed", SessionChips.Status(SessionStatus.Active, rescheduleFlagged: false, isCompleted: true).Label);
        Assert.Equal("Active", SessionChips.Status(SessionStatus.Active, rescheduleFlagged: false, isCompleted: false).Label);
    }

    /// <summary>
    /// The drift this consolidation actually fixed: session detail lacked the cancelled branch, so a
    /// cancelled session read "Not submitted" there — an outstanding task — and "—" on the list.
    /// </summary>
    [Fact]
    public void ACancelledSessionHasNothingToSubmit()
    {
        Assert.Equal("—", SessionChips.VecSubmission(SessionStatus.Cancelled, VecSubmissionStatus.NotSubmitted).Label);
        Assert.Equal("—", SessionChips.VecSubmission(SessionStatus.Cancelled, VecSubmissionStatus.Submitted).Label);
        Assert.Equal("Not submitted", SessionChips.VecSubmission(SessionStatus.Active, VecSubmissionStatus.NotSubmitted).Label);
    }
}
