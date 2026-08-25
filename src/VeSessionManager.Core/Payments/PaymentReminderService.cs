using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Payments;

/// <summary>
/// Phase 6, and now just one pass: flags applications stuck Unmatched for manual review.
///
/// <para><b>The exam-fee expiration pass is gone (2026-08-25).</b> It used to mark a stale unpaid
/// <c>Payment</c> as <c>ExpiredUnpaid</c> N hours after the FCC application was entered (or, for a
/// retest, after the result was marked) — first as a hardcoded 10-day constant, then briefly as a
/// per-team rule's hours (#401 PR2), then a plain constant again once the notice that used to share
/// its clock was removed. All three versions rested on the same wrong premise. Mike: <i>"the only '10
/// day rule' is the lifetime of the application at the FCC ... any fees related to a test must be
/// collected prior to the test. No fee, no test."</i> This app's own exam fee cannot legitimately be
/// unpaid once an FCC application exists or a result has been marked, because payment is required
/// before testing — enforced by the VE running the session, not by anything in this code. A "retest
/// fee sat unpaid" exception was floated and defended for a round before being checked against real
/// data: zero retest payments have ever existed in this deployment. See CLAUDE.md's Known
/// Constraints and <c>docs/trigger-points.md</c>.</para>
///
/// Multi-team: one Team per RunAsync call. See docs/multi-team.md.
/// </summary>
public class PaymentReminderService(
    AppDbContext dbContext,
    TimeProvider timeProvider,
    IOptions<PaymentReminderOptions> options,
    ILogger<PaymentReminderService> logger)
{
    public async Task<PaymentReminderResult> RunAsync(Team team, CancellationToken cancellationToken)
    {
        var result = new PaymentReminderResult();
        var now = timeProvider.GetUtcNow().UtcDateTime;

        await FlagStaleUnmatchedCandidatesAsync(team, now, result, cancellationToken);

        logger.LogInformation("Payment reminder run finished for team {TeamId} ({TeamName}): {Result}", team.Id, team.Name, result);
        return result;
    }

    private async Task FlagStaleUnmatchedCandidatesAsync(Team team, DateTime now, PaymentReminderResult result, CancellationToken cancellationToken)
    {
        var threshold = now.AddDays(-options.Value.UnmatchedReviewWindowDays);

        var candidates = await dbContext.Candidates
            .Where(c => c.PiiPurgedUtc == null
                        && c.ApplicationStatus == CandidateApplicationStatus.Unmatched
                        && c.UnmatchedReviewFlaggedUtc == null
                        && c.DateRegisteredUtc <= threshold
                        && c.Session.TeamId == team.Id
                        && c.Session.Status == SessionStatus.Active)
            .ToListAsync(cancellationToken);

        foreach (var candidate in candidates)
        {
            candidate.UnmatchedReviewFlaggedUtc = now;
            result.CandidatesFlaggedForReview++;
            logger.LogWarning("Candidate {CandidateId} still Unmatched {WindowDays}+ days after registration — flagged for manual FCC/FRN review", candidate.Id, options.Value.UnmatchedReviewWindowDays);
        }

        if (candidates.Count > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }
}
