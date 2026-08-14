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
    /// Cancelled &gt; Reschedule flagged &gt; Completed &gt; Active. The order is the rule, not a
    /// coincidence — a cancelled session that also carries a reschedule flag is Cancelled.
    ///
    /// <para><c>isCompleted</c> is <see cref="Session.IsCompleted"/>'s rule: finished by either
    /// route, a Session Manager marking it or ExamTools closing it. It is not <c>Status</c>, which
    /// only ever means "not cancelled" — see SessionCompletionRuleTests.</para>
    /// </summary>
    public static (string Class, string Label) Status(SessionStatus status, bool rescheduleFlagged, bool isCompleted) =>
        status == SessionStatus.Cancelled ? ("chip-brick", "Cancelled")
        : rescheduleFlagged ? ("chip-amber", "Reschedule flagged")
        : isCompleted ? ("chip-neutral", "Completed")
        : ("chip-green", "Active");

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
