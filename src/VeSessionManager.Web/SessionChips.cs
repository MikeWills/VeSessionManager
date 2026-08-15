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
    /// A cancelled session shows <c>—</c> rather than "Not submitted": there is nothing to submit
    /// for a session that never ran, and "Not submitted" reads as an outstanding task. Session
    /// detail used to omit this branch.
    /// </summary>
    public static (string Class, string Label) VecSubmission(SessionStatus status, VecSubmissionStatus submissionStatus) =>
        status == SessionStatus.Cancelled ? ("chip-neutral", "—")
        : submissionStatus == VecSubmissionStatus.Submitted ? ("chip-green", "Submitted")
        : ("chip-neutral", "Not submitted");
}
