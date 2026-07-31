using VeSessionManager.Core.Navigation;

namespace VeSessionManager.Core.VecSubmissions;

/// <summary>
/// Phase 8's "dashboard indicator: count of sessions pending [VEC] submission." A session counts as
/// pending when it has at least one candidate whose ApplicationStatus has reached a terminal/
/// complete state (Granted, Failed, or NotTested — the same terminal set SessionIngestionService
/// already treats as "done," meaning there's something concrete to submit) but the session's own
/// VecSubmissionStatus is still NotSubmitted. Cancelled sessions never count — nothing to submit
/// for a session that never happened.
///
/// The predicate itself now lives in NavBadgeCountService, since the app nav shows this same number
/// as a badge (just across every team the user can see rather than one) — keeping one definition
/// means the badge and this page's own count can never drift apart. This stays as the per-team
/// entry point the VEC Submission page already calls.
/// </summary>
public class VecSubmissionReportService(NavBadgeCountService navBadgeCountService)
{
    public Task<int> GetPendingSubmissionCountAsync(int teamId, CancellationToken cancellationToken) =>
        navBadgeCountService.CountSessionsPendingVecSubmissionAsync([teamId], cancellationToken);
}
