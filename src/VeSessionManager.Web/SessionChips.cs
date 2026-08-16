using System.Linq.Expressions;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Web;

/// <summary>
/// The one definition of the two status chips a session renders — its lifecycle state, and whether
/// it has been submitted to the VEC.
///
/// <para><b>Why this is worth centralising.</b> The same two switches were written out on the
/// session list and on session detail, and they had already diverged: the list renders a cancelled
/// session's VEC chip as <c>—</c> (there is nothing to submit for a session that never ran), while
/// detail rendered it as "Not submitted", which reads as an outstanding task. The status chip has a
/// third copy in the list's sort key, and its priority order has to match the chip exactly or a
/// column sorts by something the user cannot see — a mismatch there has been a reported bug before.</para>
///
/// <para>Deliberately takes the individual fields rather than a <c>Session</c>: the list renders
/// from a projection (SessionListRow) and never materializes the entity, so anything typed to
/// <c>Session</c> would be unusable on the page that needs it most.</para>
/// </summary>
public static class SessionChips
{
    /// <summary>
    /// Cancelled &gt; Reschedule flagged &gt; Completed &gt; Active &gt; Upcoming. The order is the
    /// rule, not a coincidence — a cancelled session that also carries a reschedule flag is
    /// Cancelled, and a session in the future that is cancelled or flagged is still cancelled or
    /// flagged. Only the plain not-yet-started case reads Upcoming.
    ///
    /// <para><c>isCompleted</c> is <see cref="Session.IsCompleted"/>'s rule: finished by either
    /// route, a Session Manager marking it or ExamTools closing it. It is not <c>Status</c>, which
    /// only ever means "not cancelled" — see SessionCompletionRuleTests.</para>
    ///
    /// <para><b>"Active" used to cover everything that was neither cancelled nor completed, which
    /// included every future session</b> — so a session two weeks away wore a green "Active" chip
    /// that read as "testing in progress" (reported 2026-08-15). That word is
    /// <see cref="SessionStatus.Active"/>'s, and it only ever meant "not cancelled"; the same
    /// misreading has produced three separate query bugs in this codebase, and the chip was the last
    /// place it still reached a user. Splitting on <paramref name="hasStarted"/> makes "Active" mean
    /// what a reader assumes: started, and not yet closed out. That is a handful of sessions at a
    /// time here, all within a day or two of running, because ingestion stamps
    /// <c>ExamToolsClosedUtc</c> once ExamTools closes them.</para>
    /// </summary>
    /// <param name="hasStarted">Scheduled start is at or before now. Compared by the caller, which holds the clock.</param>
    public static (string Class, string Label) Status(SessionStatus status, bool rescheduleFlagged, bool isCompleted, bool hasStarted) =>
        status == SessionStatus.Cancelled ? ("chip-brick", "Cancelled")
        : rescheduleFlagged ? ("chip-amber", "Reschedule flagged")
        : isCompleted ? ("chip-neutral", "Completed")
        : hasStarted ? ("chip-green", "Active")
        : ("chip-blue", "Upcoming");

    /// <summary>
    /// <c>—</c> whenever there is nothing to submit yet, "Not submitted" only when there genuinely
    /// is. Two cases produce the dash, and they are the same case:
    ///
    /// <list type="bullet">
    ///   <item>A <b>cancelled</b> session never ran. Session detail used to omit this branch, so a
    ///   cancelled session read "Not submitted" there and "—" on the list.</item>
    ///   <item>A session that <b>has not started</b> has produced no results to send (#338, reported
    ///   alongside the "Active" chip in the same breath: <i>"I have a future session that 'hasn't
    ///   been sent to the FCC', this is also confusing"</i>). It was the identical mistake one chip
    ///   over — a true statement that names an outstanding task nobody has failed to do and nobody
    ///   can do yet.</item>
    /// </list>
    ///
    /// <para><b>The nav badge was already right about this</b>, which is the strongest evidence the
    /// chip was wrong: <c>NavBadgeCountService.CountSessionsPendingVecSubmissionAsync</c> counts a
    /// session only once some candidate has reached a terminal status, so it read zero for a future
    /// session while this chip read "Not submitted". The two disagreed and the chip was the one
    /// lying.</para>
    ///
    /// <para>The badge's rule is the stricter and more precise one — "results exist" rather than
    /// "the session has started" — and this deliberately does <b>not</b> copy it. It would need a
    /// per-row candidate-status aggregate the session list does not project, to move the boundary by
    /// a few hours for sessions on the day they run. <paramref name="hasStarted"/> costs nothing,
    /// mirrors <see cref="Status"/> exactly, and fixes the case anyone actually saw.</para>
    ///
    /// <para>A session marked <b>Submitted</b> says so regardless: that is a fact about what somebody
    /// did, and hiding it behind a dash would erase a real action.</para>
    /// </summary>
    /// <param name="hasStarted">Scheduled start is at or before now — the same value <see cref="Status"/> takes, from the same caller.</param>
    public static (string Class, string Label) VecSubmission(SessionStatus status, VecSubmissionStatus submissionStatus, bool hasStarted) =>
        status == SessionStatus.Cancelled ? ("chip-neutral", "—")
        : submissionStatus == VecSubmissionStatus.Submitted ? ("chip-green", "Submitted")
        : hasStarted ? ("chip-neutral", "Not submitted")
        : ("chip-neutral", "—");

    /// <summary>
    /// A candidate's payment state (#309, DUP-10) — written out on both the session roster and the
    /// candidate detail page until 2026-08-16.
    /// </summary>
    /// <param name="status">
    /// Null means <b>no payment row at all</b>, which only the roster can encounter: the candidate
    /// page renders one chip per payment it already has. Deliberately distinct from
    /// <see cref="PaymentStatus.NotApplicable"/>, which is a payment that exists and is not owed —
    /// collapsing the two would report "no payment" for a session that collects no fees.
    /// </param>
    public static (string Class, string Label) Payment(PaymentStatus? status) => status switch
    {
        null => ("chip-neutral", "No payment"),
        PaymentStatus.Paid => ("chip-green", "Paid"),
        PaymentStatus.Unpaid => ("chip-amber", "Unpaid"),
        _ => ("chip-neutral", "Not applicable")
    };

    // ---- Sort keys ---------------------------------------------------------------------------

    /// <summary>
    /// The list's sort keys, as expressions EF can translate — so the column sorts by the same rule
    /// the chip renders, rather than by a restatement of it.
    ///
    /// <para><b>Why these live here and not at the call site.</b> They used to be written out inline
    /// in <c>Index.cshtml.cs</c>, because a sort key runs inside an expression tree and EF cannot
    /// invoke a method — <see cref="Status"/> is unreachable from there. So the same five-state
    /// switch existed twice, and the test that was supposed to hold them together compared the chip
    /// against a <i>copy</i> of the sort key kept in the test file. That catches the chip changing.
    /// It does not catch the sort key changing: mutating the real one left the suite green (2026-08-15,
    /// while fixing #338).</para>
    ///
    /// <para>An <c>Expression</c> is the way out. EF composes it into the query, and a test can
    /// <c>Compile()</c> it and run it against a real <see cref="Session"/> — so the guard compares two
    /// artifacts that both actually ship, in both directions, instead of a copy nobody is obliged to
    /// update. Same pattern as <c>SessionListRow.Projection</c> a few hundred lines away.</para>
    ///
    /// <para>Sorting a column by something other than the text in it looks broken to whoever is
    /// reading it, and has been a reported bug here before.</para>
    /// </summary>
    /// <param name="nowUtc">
    /// Captured into the expression rather than read from a clock inside it: <c>DateTime.UtcNow</c>
    /// in an expression tree is evaluated by SQLite, not by this process, and the caller already
    /// holds the same instant it passes to <see cref="Status"/>.
    /// </param>
    public static Expression<Func<Session, string>> StatusSortKey(DateTime nowUtc) =>
        s => s.Status == SessionStatus.Cancelled ? "Cancelled"
            : s.RescheduleFlaggedForReview ? "Reschedule flagged"
            // Session.IsCompleted's rule, spelled out: it is a property, so EF cannot call it either.
            : s.TestingCompletedUtc != null || s.ExamToolsClosedUtc != null ? "Completed"
            : s.ScheduledStartUtc <= nowUtc ? "Active"
            : "Upcoming";

    /// <inheritdoc cref="StatusSortKey"/>
    public static Expression<Func<Session, string>> VecSubmissionSortKey(DateTime nowUtc) =>
        s => s.Status == SessionStatus.Cancelled ? "—"
            : s.VecSubmissionStatus == VecSubmissionStatus.Submitted ? "Submitted"
            : s.ScheduledStartUtc <= nowUtc ? "Not submitted"
            : "—";
}
