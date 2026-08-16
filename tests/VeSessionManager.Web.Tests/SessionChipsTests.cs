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
    private static string InlineStatusSortKey(Session s, bool hasStarted) =>
        s.Status == SessionStatus.Cancelled ? "Cancelled"
        : s.RescheduleFlaggedForReview ? "Reschedule flagged"
        : s.TestingCompletedUtc != null || s.ExamToolsClosedUtc != null ? "Completed"
        : hasStarted ? "Active"
        : "Upcoming";

    /// <summary>Copied verbatim from Index.cshtml.cs's "vecsubmission" sort key.</summary>
    private static string InlineVecSortKey(Session s, bool hasStarted) =>
        s.Status == SessionStatus.Cancelled ? "—"
        : s.VecSubmissionStatus == VecSubmissionStatus.Submitted ? "Submitted"
        : hasStarted ? "Not submitted"
        : "—";

    /// <summary>
    /// The two mirrors above are copies, and a copy only guards one direction.
    ///
    /// <para>Change <see cref="SessionChips"/> without updating this file and the comparison below
    /// fails, as intended. Change the <b>real</b> sort key in <c>Index.cshtml.cs</c> and nothing here
    /// notices — the theory compares the chip against the copy, and neither of them moved. Found by
    /// mutating the real sort key while fixing #338: it kept passing.</para>
    ///
    /// <para>So this scans the source. It is deliberately narrow — it asserts only that each sort key
    /// still consults the clock, which is the clause both chips depend on and the one an edit would
    /// drop. A full text comparison would fail on whitespace and teach everyone to ignore it.</para>
    /// </summary>
    [Fact]
    public void TheRealSortKeysStillConsultTheClock()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "VeSessionManager.Web", "Pages", "SessionManager", "Index.cshtml.cs"));

        var statusKey = Between(source, "\"status\" => Order(", "),");
        var vecKey = Between(source, "\"vecsubmission\" => Order(", "),");

        Assert.Contains("ScheduledStartUtc <= now", statusKey);
        Assert.Contains("Upcoming", statusKey);

        // #338: without this clause a future session sorts under "Not submitted" while its chip
        // reads "—", which is the column sorting by text the reader cannot see.
        Assert.Contains("ScheduledStartUtc <= now", vecKey);
    }

    private static string Between(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"Could not find \"{start}\" in Index.cshtml.cs — the sort keys moved or were renamed.");
        var to = source.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        Assert.True(to > from, $"Could not find the end of the block starting at \"{start}\".");
        return source[from..to];
    }

    /// <summary>Walks up from the test binary to the repo root, same approach as the other source-scanning tests here.</summary>
    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }

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
            VecSubmissionStatus = submitted
        };

        Assert.Equal(
            SessionChips.Status(status, flagged, session.IsCompleted, hasStarted).Label,
            InlineStatusSortKey(session, hasStarted));

        Assert.Equal(
            SessionChips.VecSubmission(status, submitted, hasStarted).Label,
            InlineVecSortKey(session, hasStarted));
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
