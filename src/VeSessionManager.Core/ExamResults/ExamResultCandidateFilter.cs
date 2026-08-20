using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.ExamResults;

/// <summary>
/// Which candidates an exam-result read looks at, split into the two buckets that must never be
/// confused — and the reason the split exists at all.
///
/// <para><b>The failure this prevents, which has now happened twice.</b>
/// <c>ExamResultSyncService.SyncSessionAsync</c> is documented as the unbounded escape hatch for a
/// session graded later than the routine sweep reaches. Both entry points share one filter, so
/// <i>every bound added for the scheduled scan silently applied to the escape hatch too</i>, and the
/// doc comment quietly became false:</para>
/// <list type="number">
///   <item><b>2026-08-03</b> — the manual refresh ran <c>RunAsync</c>, so <c>ResultSyncWindow</c>
///   applied regardless and the promised escape hatch did not exist.</item>
///   <item><b>2026-08-20</b> — the settled gate added with #437 was inherited by the manual path, so
///   a license class frozen too low could not be repaired by pressing Refresh. That bound was
///   exactly wrong for the population needing repair: a frozen class is almost always noticed
///   <i>after</i> the session is finalized.</item>
/// </list>
///
/// <para>Both times the promise was in prose and the bound was in a shared <c>Where</c>, where
/// nothing connected them. This type is the structural answer: <b>a new rule has to be put in one of
/// two named buckets, and the names say which path it binds.</b> There is no third place to put it
/// and no way to add one by accident, because <see cref="ShouldRead"/> is the only caller.</para>
/// </summary>
public static class ExamResultCandidateFilter
{
    /// <summary>
    /// Rules that hold on <b>every</b> path, human-triggered included. These are not about cost —
    /// they are about what the feed is allowed to speak for.
    ///
    /// <para>Put a rule here when re-reading the candidate would be <i>wrong</i>. Not when it would
    /// merely be wasteful — that is the other bucket.</para>
    /// </summary>
    public static bool CanBeReadFromTheFeed(Candidate candidate) =>
        candidate.ExamToolsApplicantId is not null

        // Withdrew or no-showed: there is no result to read, now or ever.
        && candidate.ApplicationStatus != CandidateApplicationStatus.NotTested

        // A HUMAN Failed verdict is final. Auto-failed rows (ResultMarkedByUserId null) are
        // re-examined on purpose — that is what made the old pass-one-fail-one bug self-correcting —
        // but a Session Manager who marked somebody failed must never be overruled by a feed, and
        // least of all by somebody pressing a button.
        && (candidate.ApplicationStatus != CandidateApplicationStatus.Failed || candidate.ResultMarkedByUserId is null);

    /// <summary>
    /// Bounds that exist <b>only to keep the scheduled scan cheap</b>, and which a human explicitly
    /// asking for a re-read is entitled to ignore.
    ///
    /// <para>Put a rule here when re-reading would be correct but wasteful. ⚠️ If you are about to
    /// add a clause to the scheduled scan, it almost certainly belongs here — and if you put it in
    /// the other bucket instead, you have just made the escape hatch's promise false for the third
    /// time.</para>
    ///
    /// <para>"Settled" means: this candidate has a result, has a license class derived from it, and
    /// their session is closed in ExamTools so no further grading can arrive. Grading is entered
    /// element by element, which is why an <i>open</i> session is never settled however complete the
    /// record looks (#437).</para>
    /// </summary>
    public static bool IsSettledForNow(Candidate candidate, Session session) =>
        candidate.Tested
        && candidate.NewLicenseClass is not null
        && session.ExamToolsClosedUtc is not null;

    /// <summary>
    /// Whether this read should fetch the candidate's detail.
    /// </summary>
    /// <param name="includeSettled">
    /// True only on the human-triggered path (<c>SyncSessionAsync</c>), where it lifts
    /// <see cref="IsSettledForNow"/> and nothing else. This is the escape hatch, and it is the whole
    /// promise: a person pressing Refresh pays one applicant-detail call per candidate, once, and
    /// gets a genuine re-read rather than the scheduled scan's economies.
    /// </param>
    public static bool ShouldRead(Candidate candidate, Session session, bool includeSettled) =>
        CanBeReadFromTheFeed(candidate)
        && (includeSettled || !IsSettledForNow(candidate, session));
}
