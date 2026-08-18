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
public class NavBadgeCountService(AppDbContext dbContext, TimeProvider timeProvider)
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

        // The whole reason the reconciliation sweep writes findings to a table rather than only into
        // its run summary: a count on the chassis is seen without going looking, and "ExamTools has
        // sessions we don't" is not something anyone thinks to check for.
        var openReconciliationFindings = await dbContext.ReconciliationFindings
            .Where(f => f.ResolvedUtc == null)
            .Where(f => teamIds == null || teamIds.Contains(f.TeamId))
            .CountAsync(cancellationToken);

        var renewalsNeedingAttention = await CountRenewalsNeedingAttentionAsync(teamIds, cancellationToken);

        return new NavBadgeCounts(applicantsPendingGrant, sessionsPendingVecSubmission, unresolvedUnmatchedPayments, openReconciliationFindings, renewalsNeedingAttention);
    }

    /// <summary>
    /// The Renewal Monitor's share of the Applicants menu: watched licenses in a status a human
    /// should act on — <c>WatchedLicenseStatusExtensions.NeedsAttention</c>, whose own comment always
    /// said it was "what a future digest would count".
    ///
    /// <para>Materialized and derived in memory rather than translated: <c>DeriveStatus</c> is date
    /// arithmetic against now with deliberate edge rules (valid <i>through</i> the expiry date, grace
    /// through its final day), and re-expressing that in SQL is a second copy that drifts. The watch
    /// list is hand-curated and small, and this sits behind <c>NavBadgeCountCache</c> anyway.</para>
    /// </summary>
    private async Task<int> CountRenewalsNeedingAttentionAsync(IReadOnlyList<int>? teamIds, CancellationToken cancellationToken)
    {
        if (teamIds is { Count: 0 })
        {
            return 0;
        }

        var licenses = await dbContext.WatchedLicenses
            .AsNoTracking()
            .Where(w => teamIds == null || teamIds.Contains(w.TeamId))
            .ToListAsync(cancellationToken);

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        return licenses.Count(w => w.DeriveStatus(nowUtc).NeedsAttention());
    }

    /// <summary>
    /// Backs both the nav badge and the Sessions page's "Pending VEC submission" filter — a session
    /// counts as pending when at least one candidate has a result worth submitting -- Granted or
    /// Failed, see SubmittableStatuses -- but the session itself is still NotSubmitted. A withdrawal
    /// does not count: it is settled, and there is nothing to file (#423). Cancelled sessions never
    /// count. Same teamIds convention as GetCountsAsync.
    /// </summary>
    public Task<int> CountSessionsPendingVecSubmissionAsync(IReadOnlyList<int>? teamIds, CancellationToken cancellationToken) =>
        dbContext.Sessions
            .Where(s => (teamIds == null || teamIds.Contains(s.TeamId))
                && s.Status == SessionStatus.Active
                && s.VecSubmissionStatus == VecSubmissionStatus.NotSubmitted
                // Submittable, not merely terminal (#423): a withdrawal is settled but produces no
                // paperwork, so a session whose only settled candidate withdrew has nothing to send.
                && s.Candidates.Any(c => CandidateApplicationStatusExtensions.SubmittableStatuses.Contains(c.ApplicationStatus)))
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
/// <param name="RenewalsNeedingAttention">Watched licenses a human should act on — the Renewal Monitor's share of the Applicants menu.</param>
public record NavBadgeCounts(int ApplicantsPendingGrant, int SessionsPendingVecSubmission, int UnresolvedUnmatchedPayments, int OpenReconciliationFindings, int RenewalsNeedingAttention)
{
    /// <summary>The Applicants trigger chip: the sum of its menu items, so the number on the closed menu equals what opening it accounts for.</summary>
    public int ApplicantsTotal => ApplicantsPendingGrant + RenewalsNeedingAttention;
}

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
