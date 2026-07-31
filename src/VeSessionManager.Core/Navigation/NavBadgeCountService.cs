using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Navigation;

/// <summary>
/// The "how much is still outstanding" counts shown as badges on the app nav (see
/// _AppLayout.cshtml) and, for the two team-scoped worklists, next to each team in their own
/// team-picker pills (Applicant Status / Unmatched Payments, added 2026-07-30). Each count mirrors
/// the filter its own page already applies, so a badge and the list it corresponds to can never
/// disagree — the VEC-submission one in particular must stay identical to the Sessions page's
/// "Pending VEC submission" status filter (Index.cshtml.cs), which replaced the standalone VEC
/// Submission page.
///
/// The two predicates that have both an aggregate and a per-team form live once, as the
/// <see cref="PendingGrantPredicate"/>/<see cref="UnresolvedPaymentPredicate"/> expressions below,
/// composed into whichever query needs them rather than retyped — that's the whole reason the
/// per-team counts live here instead of on the pages themselves.
///
/// **teamIds semantics matter:** for the aggregate methods, null means "every team" (SystemAdmin —
/// see SessionAccessScope.GetEffectiveTeamIds, which returns null for that role), NOT "no teams". An
/// empty list genuinely means no teams and correctly yields zeros. Getting this backwards would
/// silently show a SystemAdmin an all-zero nav. The per-team methods take a concrete, non-null list
/// instead — a picker always knows exactly which teams it's about to render.
/// </summary>
public class NavBadgeCountService(AppDbContext dbContext)
{
    /// <summary>Matches ApplicantStatus.cshtml.cs's own "Pending FCC grant" query — passed, but not yet confirmed Granted.</summary>
    private static readonly Expression<Func<Candidate, bool>> PendingGrantPredicate =
        c => c.Tested
            && (c.ApplicationStatus == CandidateApplicationStatus.Unmatched || c.ApplicationStatus == CandidateApplicationStatus.Received);

    /// <summary>Matches UnmatchedPayments.cshtml.cs's own query — arrived from Square, still not attributed to a candidate.</summary>
    private static readonly Expression<Func<UnmatchedSquarePayment, bool>> UnresolvedPaymentPredicate =
        u => u.ResolvedUtc == null;

    public async Task<NavBadgeCounts> GetCountsAsync(IReadOnlyList<int>? teamIds, CancellationToken cancellationToken)
    {
        var applicantsPendingGrant = await dbContext.Candidates
            .Where(PendingGrantPredicate)
            .Where(c => teamIds == null || teamIds.Contains(c.Session.TeamId))
            .CountAsync(cancellationToken);

        var sessionsPendingVecSubmission = await CountSessionsPendingVecSubmissionAsync(teamIds, cancellationToken);

        var unresolvedUnmatchedPayments = await dbContext.UnmatchedSquarePayments
            .Where(UnresolvedPaymentPredicate)
            .Where(u => teamIds == null || teamIds.Contains(u.TeamId))
            .CountAsync(cancellationToken);

        return new NavBadgeCounts(applicantsPendingGrant, sessionsPendingVecSubmission, unresolvedUnmatchedPayments);
    }

    /// <summary>
    /// Backs both the nav badge and the Sessions page's "Pending VEC submission" filter — a session
    /// counts as pending when at least one candidate has reached a terminal state (there's something
    /// concrete to submit) but the session itself is still NotSubmitted. Cancelled sessions never
    /// count. Same teamIds convention as GetCountsAsync.
    /// </summary>
    public Task<int> CountSessionsPendingVecSubmissionAsync(IReadOnlyList<int>? teamIds, CancellationToken cancellationToken) =>
        dbContext.Sessions
            .Where(s => (teamIds == null || teamIds.Contains(s.TeamId))
                && s.Status == SessionStatus.Active
                && s.VecSubmissionStatus == VecSubmissionStatus.NotSubmitted
                && s.Candidates.Any(c => CandidateApplicationStatusExtensions.TerminalStatuses.Contains(c.ApplicationStatus)))
            .CountAsync(cancellationToken);

    /// <summary>
    /// Per-team "Pending FCC grant" counts for Applicant Status's team picker. A team with nothing
    /// pending is absent from the grouped result, so callers should read it through
    /// <see cref="TeamCountExtensions.CountFor"/> rather than indexing.
    /// </summary>
    public async Task<IReadOnlyDictionary<int, int>> GetApplicantsPendingGrantByTeamAsync(
        IReadOnlyList<int> teamIds, CancellationToken cancellationToken) =>
        await CountByTeamAsync(
            dbContext.Candidates.Where(PendingGrantPredicate).Select(c => c.Session.TeamId),
            teamIds,
            cancellationToken);

    /// <summary>Per-team unresolved-payment counts for Unmatched Payments' team picker. Same absent-means-zero convention as <see cref="GetApplicantsPendingGrantByTeamAsync"/>.</summary>
    public async Task<IReadOnlyDictionary<int, int>> GetUnresolvedUnmatchedPaymentsByTeamAsync(
        IReadOnlyList<int> teamIds, CancellationToken cancellationToken) =>
        await CountByTeamAsync(
            dbContext.UnmatchedSquarePayments.Where(UnresolvedPaymentPredicate).Select(u => u.TeamId),
            teamIds,
            cancellationToken);

    /// <summary>
    /// Shared "group a stream of team ids into per-team counts" tail. Grouping is materialized with
    /// ToListAsync before being turned into a dictionary — EF Core InMemory (what the tests run on)
    /// is fragile about composing further operators onto a GroupBy/Select projection, see CLAUDE.md's
    /// Known Constraints entry about VolunteerExaminerReportService.
    /// </summary>
    private static async Task<IReadOnlyDictionary<int, int>> CountByTeamAsync(
        IQueryable<int> teamIdStream, IReadOnlyList<int> teamIds, CancellationToken cancellationToken)
    {
        if (teamIds.Count == 0)
        {
            return new Dictionary<int, int>();
        }

        var grouped = await teamIdStream
            .Where(teamId => teamIds.Contains(teamId))
            .GroupBy(teamId => teamId)
            .Select(g => new { TeamId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        return grouped.ToDictionary(x => x.TeamId, x => x.Count);
    }
}

/// <summary>Zero means "nothing outstanding" — the nav hides the badge entirely rather than rendering a "0".</summary>
public record NavBadgeCounts(int ApplicantsPendingGrant, int SessionsPendingVecSubmission, int UnresolvedUnmatchedPayments);

/// <summary>
/// Reading a per-team count dictionary safely. The grouped queries above omit teams with nothing
/// outstanding entirely, so every read site would otherwise need the same TryGetValue dance — and a
/// team picker specifically *does* want to render an explicit "0" rather than skip the team.
/// </summary>
public static class TeamCountExtensions
{
    public static int CountFor(this IReadOnlyDictionary<int, int> counts, int teamId) =>
        counts.TryGetValue(teamId, out var count) ? count : 0;
}
