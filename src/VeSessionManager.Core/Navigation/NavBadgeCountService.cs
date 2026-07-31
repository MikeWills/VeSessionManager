using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Navigation;

/// <summary>
/// The "how much is still outstanding" counts shown as badges on the app nav (see
/// _AppLayout.cshtml). Each count mirrors the filter its own page already applies, so a badge and
/// the page it links to can never disagree — the VEC-submission one is in fact the single source
/// both this and VecSubmissionReportService.GetPendingSubmissionCountAsync use.
///
/// **teamIds semantics matter:** null means "every team" (SystemAdmin — see
/// SessionAccessScope.GetEffectiveTeamIds, which returns null for that role), NOT "no teams". An
/// empty list genuinely means no teams and correctly yields zeros. Getting this backwards would
/// silently show a SystemAdmin an all-zero nav.
/// </summary>
public class NavBadgeCountService(AppDbContext dbContext)
{
    public async Task<NavBadgeCounts> GetCountsAsync(IReadOnlyList<int>? teamIds, CancellationToken cancellationToken)
    {
        var applicantsPendingGrant = await dbContext.Candidates
            .Where(c => (teamIds == null || teamIds.Contains(c.Session.TeamId))
                && c.Tested
                && (c.ApplicationStatus == CandidateApplicationStatus.Unmatched || c.ApplicationStatus == CandidateApplicationStatus.Received))
            .CountAsync(cancellationToken);

        var sessionsPendingVecSubmission = await CountSessionsPendingVecSubmissionAsync(teamIds, cancellationToken);

        var unresolvedUnmatchedPayments = await dbContext.UnmatchedSquarePayments
            .Where(u => (teamIds == null || teamIds.Contains(u.TeamId)) && u.ResolvedUtc == null)
            .CountAsync(cancellationToken);

        return new NavBadgeCounts(applicantsPendingGrant, sessionsPendingVecSubmission, unresolvedUnmatchedPayments);
    }

    /// <summary>
    /// Shared by the nav badge and VecSubmissionReportService's own per-team count — a session counts
    /// as pending when at least one candidate has reached a terminal state (there's something
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
}

/// <summary>Zero means "nothing outstanding" — the nav hides the badge entirely rather than rendering a "0".</summary>
public record NavBadgeCounts(int ApplicantsPendingGrant, int SessionsPendingVecSubmission, int UnresolvedUnmatchedPayments);
