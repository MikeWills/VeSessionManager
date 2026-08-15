using System.Linq.Expressions;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Sessions;

/// <summary>
/// "Is this session finished?" — the one rule, in the forms the language actually allows (#305).
///
/// <para><b>The rule.</b> A session is finished once a Session Manager has marked testing complete
/// (<c>TestingCompletedUtc</c>) or ExamTools has closed it (<c>ExamToolsClosedUtc</c>). It is
/// deliberately <b>not</b> <c>Status</c>: Status only ever leaves Active on cancellation, so
/// <c>Status == Active</c> means "not cancelled" and selects every session a team has ever run. That
/// misreading has shipped twice — it made VolunteerExaminerSyncService re-poll a team's whole
/// history hourly for months, then came back in the VE Roster's "sessions worked" count, where a VE
/// rostered onto a *future* session already had it in their total. Both looked right on every screen.
/// </para>
///
/// <para><b>Why this is three members and not one.</b> EF Core cannot translate a C# method or
/// property into SQL, and cannot compose one expression into another without a predicate-rewriting
/// dependency (LINQKit), which this project does not take. So the rule needs a form for materialized
/// objects and a form per entity a query starts from. They live here, adjacent, rather than spread
/// across eleven call sites — and <c>SessionCompletionRuleTests</c> asserts they agree, since the
/// language cannot.</para>
///
/// <para><b>Some call sites still spell it out.</b> A predicate nested inside another lambda — an
/// <c>Any(sve =&gt; … &amp;&amp; finished)</c> in the middle of a larger filter — cannot take one of these
/// expressions, for the composition reason above. Those remain inline and are covered by the test
/// rather than by reuse. Reducing eleven copies to a handful is the available win; pretending
/// otherwise would mean contorting readable queries to reach zero.</para>
/// </summary>
public static class SessionCompletion
{
    /// <summary>
    /// The rule over the two raw timestamps. Every in-memory caller routes here — the entity, and the
    /// projected row types that would otherwise re-implement it against their own copies of the same
    /// two fields.
    /// </summary>
    public static bool IsCompleted(DateTime? testingCompletedUtc, DateTime? examToolsClosedUtc) =>
        CompletedUtc(testingCompletedUtc, examToolsClosedUtc) is not null;

    /// <summary>
    /// When it finished, preferring the manual timestamp: a Session Manager marking the session is a
    /// more specific fact than ExamTools observing it closed. Session Detail renders this, so the
    /// preference is user-visible.
    /// </summary>
    public static DateTime? CompletedUtc(DateTime? testingCompletedUtc, DateTime? examToolsClosedUtc) =>
        testingCompletedUtc ?? examToolsClosedUtc;

    /// <summary>
    /// Query-side, for anything starting from <c>Sessions</c>. Usable directly as
    /// <c>query.Where(SessionCompletion.SessionIsCompleted)</c>; not composable into a larger lambda.
    /// </summary>
    public static readonly Expression<Func<Session, bool>> SessionIsCompleted =
        s => s.TestingCompletedUtc != null || s.ExamToolsClosedUtc != null;

    /// <summary>
    /// The same rule for a query starting from the roster link, which is how every "sessions worked"
    /// figure is counted. Chain it — <c>dbContext.SessionVolunteerExaminers.Where(RosterLinkIsCompleted).Where(…)</c>
    /// — rather than inlining, so the rule keeps one home even though it needs a second spelling.
    /// </summary>
    public static readonly Expression<Func<SessionVolunteerExaminer, bool>> RosterLinkIsCompleted =
        sve => sve.Session.TestingCompletedUtc != null || sve.Session.ExamToolsClosedUtc != null;
}
