using VeSessionManager.Core.Entities;
using VeSessionManager.Web;
using Xunit;

namespace VeSessionManager.Web.Tests;

/// <summary>
/// <see cref="SessionChips"/> is the one definition of the two session chips, and since 2026-08-15
/// also of the list's sort keys.
///
/// <para><b>What these tests used to be, and why that was not enough.</b> A sort key runs inside an
/// EF expression tree and cannot call a method, so both rules were written out a second time in
/// <c>Index.cshtml.cs</c> — and this file held a hand-copied third version to compare against. That
/// catches the chip changing: the copy goes stale and fails. It does not catch the <i>sort key</i>
/// changing — mutating the real one left every test here green, found while fixing #338.</para>
///
/// <para>The sort keys are <c>Expression</c>s now. EF still translates them, and the tests below
/// <c>Compile()</c> and run them — so the comparison is between two artifacts that both ship, in both
/// directions, with no copy anyone is obliged to keep in step.</para>
///
/// <para>Sorting a column by something other than the text in it looks broken to whoever is reading
/// it, and has been a reported bug here before.</para>
/// </summary>
public class SessionChipsTests
{
    private static readonly DateTime Now = new(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc);

    private static readonly Func<Session, string> StatusSortKey = SessionChips.StatusSortKey(Now).Compile();
    private static readonly Func<Session, string> VecSortKey = SessionChips.VecSubmissionSortKey(Now).Compile();

    /// <summary>Every combination that can reach a chip, including contradictory ones.</summary>
    public static TheoryData<SessionStatus, bool, DateTime?, DateTime?, VecSubmissionStatus, bool> Matrix()
    {
        var data = new TheoryData<SessionStatus, bool, DateTime?, DateTime?, VecSubmissionStatus, bool>();
        DateTime? stamp = new DateTime(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc);
        foreach (var status in new[] { SessionStatus.Active, SessionStatus.Cancelled })
        foreach (var flagged in new[] { false, true })
        foreach (var tested in new[] { (DateTime?)null, stamp })
        foreach (var closed in new[] { (DateTime?)null, stamp })
        foreach (var submitted in new[] { VecSubmissionStatus.NotSubmitted, VecSubmissionStatus.Submitted })
        foreach (var hasStarted in new[] { false, true })
        {
            data.Add(status, flagged, tested, closed, submitted, hasStarted);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void ChipLabelsAndSortKeysAgree(
        SessionStatus status, bool flagged, DateTime? tested, DateTime? closed, VecSubmissionStatus submitted, bool hasStarted)
    {
        var session = new Session
        {
            ExamToolsSessionId = "x",
            Title = "x",
            Status = status,
            RescheduleFlaggedForReview = flagged,
            TestingCompletedUtc = tested,
            ExamToolsClosedUtc = closed,
            VecSubmissionStatus = submitted,
            // hasStarted is expressed as a real scheduled start either side of the instant the sort
            // key captured, so both sides decide from the same fact rather than being handed the
            // answer.
            ScheduledStartUtc = hasStarted ? Now.AddHours(-1) : Now.AddHours(1)
        };

        Assert.Equal(
            SessionChips.Status(status, flagged, session.IsCompleted, hasStarted).Label,
            StatusSortKey(session));

        Assert.Equal(
            SessionChips.VecSubmission(status, submitted, hasStarted).Label,
            VecSortKey(session));
    }

    /// <summary>
    /// The priority order is the rule, not an accident — a cancelled session that also carries a
    /// reschedule flag is Cancelled, and a flagged session that has finished is still flagged,
    /// because the flag means "a human needs to look at this" and completion does not clear it.
    /// </summary>
    [Fact]
    public void ContradictoryStatesResolveByPriority()
    {
        Assert.Equal("Cancelled", SessionChips.Status(SessionStatus.Cancelled, rescheduleFlagged: true, isCompleted: true, hasStarted: true).Label);
        Assert.Equal("Reschedule flagged", SessionChips.Status(SessionStatus.Active, rescheduleFlagged: true, isCompleted: true, hasStarted: true).Label);
        Assert.Equal("Completed", SessionChips.Status(SessionStatus.Active, rescheduleFlagged: false, isCompleted: true, hasStarted: true).Label);
        Assert.Equal("Active", SessionChips.Status(SessionStatus.Active, rescheduleFlagged: false, isCompleted: false, hasStarted: true).Label);
    }


    /// <summary>
    /// A session that has not started yet reads <b>Upcoming</b>, not "Active" (Mike, 2026-08-15).
    ///
    /// <para>"Active" is <c>Session.Status</c>'s word and it only ever means "not cancelled" — it is
    /// never set to Completed and has nothing to do with whether testing is happening. That is a trap
    /// this codebase has hit three times in query logic; the chip was the last place it still reached
    /// users, where it read as "testing in progress" on a session two weeks away.</para>
    ///
    /// <para>"Active" now means what a reader assumes: started, and not yet closed out. On this
    /// deployment that is a handful of sessions at a time, all within a day or two of running, since
    /// ingestion closes them once ExamTools does.</para>
    /// </summary>
    [Fact]
    public void ASessionThatHasNotStartedYetIsUpcoming()
    {
        Assert.Equal("Upcoming",
            SessionChips.Status(SessionStatus.Active, rescheduleFlagged: false, isCompleted: false, hasStarted: false).Label);
    }

    [Fact]
    public void ASessionThatHasStartedButIsNotClosedOutIsActive()
    {
        Assert.Equal("Active",
            SessionChips.Status(SessionStatus.Active, rescheduleFlagged: false, isCompleted: false, hasStarted: true).Label);
    }

    /// <summary>
    /// Upcoming sits below the states that mean something happened: a cancelled or flagged session in
    /// the future is still cancelled or flagged. Only the plain not-yet-started case changes.
    /// </summary>
    [Fact]
    public void UpcomingLosesToCancelledAndFlagged()
    {
        Assert.Equal("Cancelled",
            SessionChips.Status(SessionStatus.Cancelled, rescheduleFlagged: false, isCompleted: false, hasStarted: false).Label);
        Assert.Equal("Reschedule flagged",
            SessionChips.Status(SessionStatus.Active, rescheduleFlagged: true, isCompleted: false, hasStarted: false).Label);
    }

    /// <summary>
    /// The drift this consolidation actually fixed: session detail lacked the cancelled branch, so a
    /// cancelled session read "Not submitted" there — an outstanding task — and "—" on the list.
    /// </summary>
    [Fact]
    public void ACancelledSessionHasNothingToSubmit()
    {
        Assert.Equal("—", SessionChips.VecSubmission(SessionStatus.Cancelled, VecSubmissionStatus.NotSubmitted, hasStarted: true).Label);
        Assert.Equal("—", SessionChips.VecSubmission(SessionStatus.Cancelled, VecSubmissionStatus.Submitted, hasStarted: true).Label);
        Assert.Equal("Not submitted", SessionChips.VecSubmission(SessionStatus.Active, VecSubmissionStatus.NotSubmitted, hasStarted: true).Label);
    }

    /// <summary>
    /// The payment chip (#309, DUP-10). The three-arm switch was written out on both the session
    /// roster and the candidate page; only the roster had the fourth case, because only it can be
    /// looking at a candidate with no payment at all.
    /// </summary>
    [Fact]
    public void ThePaymentChipCoversEveryStatusAndTheNoPaymentCase()
    {
        Assert.Equal("Paid", SessionChips.Payment(PaymentStatus.Paid).Label);
        Assert.Equal("Unpaid", SessionChips.Payment(PaymentStatus.Unpaid).Label);
        Assert.Equal("Not applicable", SessionChips.Payment(PaymentStatus.NotApplicable).Label);

        // Null is "this candidate has no payment row", which is not the same as one that exists and
        // is not applicable — the roster shows both and they must not collapse.
        Assert.Equal("No payment", SessionChips.Payment(null).Label);
        Assert.NotEqual(SessionChips.Payment(null), SessionChips.Payment(PaymentStatus.NotApplicable));
    }

    /// <summary>
    /// A session that has not started yet has nothing to submit either (#338, the second half —
    /// "I have a future session that 'hasn't been sent to the FCC', this is also confusing").
    ///
    /// <para>Exactly the same mistake as the status chip beside it, one chip over, and fixed the same
    /// way. "Not submitted" is a true statement about a session next month and a <b>useless</b> one:
    /// it names an outstanding task that cannot be done yet and that nobody has failed to do. There
    /// is nothing to send a VEC until the session has run and produced results.</para>
    ///
    /// <para>Corroborated by the nav badge, which has always been right about this: it counts a
    /// session as pending submission only once some candidate has reached a terminal status. So the
    /// badge read zero while the chip read "Not submitted" — the two disagreed, and the chip was the
    /// one lying.</para>
    /// </summary>
    [Fact]
    public void AFutureSessionHasNothingToSubmitYet()
    {
        Assert.Equal("—",
            SessionChips.VecSubmission(SessionStatus.Active, VecSubmissionStatus.NotSubmitted, hasStarted: false).Label);
    }

    /// <summary>
    /// Submitted outranks not-yet-started. If a Session Manager has marked it submitted, that is a
    /// fact about what happened and the chip reports it — showing "—" there would hide a real action.
    /// </summary>
    [Fact]
    public void ASubmittedSessionSaysSoEvenIfItHasNotStarted()
    {
        Assert.Equal("Submitted",
            SessionChips.VecSubmission(SessionStatus.Active, VecSubmissionStatus.Submitted, hasStarted: false).Label);
    }
}
