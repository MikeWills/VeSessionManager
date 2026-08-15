using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Integrations;
using VeSessionManager.Core.Square;

namespace VeSessionManager.Core.Payments;

/// <summary>
/// Follows submitted refunds to a conclusion (#375).
///
/// <para><b>Why this job has to exist.</b> Every other outbound Square call in this app finishes
/// when it returns. A refund does not: Square accepts it, answers PENDING, and takes anything up to
/// 14 days for a card or bank transfer — and it can still end REJECTED or FAILED. Without something
/// watching, the app would show every refund as issued and never learn that one bounced. There is no
/// webhook subscription for this, so it is a poll.</para>
///
/// <para>Scan-based and idempotent like every other job here: <see cref="Refund.SettledUtc"/> is both
/// the query filter and the guard, so an extra tick is a no-op and a missed one catches up.</para>
///
/// <para>It also does a second job that looks unrelated and is not: a refund left in
/// <see cref="RefundStatus.Submitting"/> with no Square refund id is one whose call never came back.
/// Re-sending it with its <b>persisted</b> idempotency key either returns the refund Square already
/// made or makes it now — and either way exactly once. That is the crash path RefundService's
/// save-before-call ordering exists for, and nothing else would ever complete it: the user got an
/// error and has no reason to click again.</para>
/// </summary>
public class RefundStatusService(
    AppDbContext dbContext,
    ISquareClient squareClient,
    TimeProvider timeProvider,
    TeamIntegrationState integrationState,
    ILogger<RefundStatusService> logger)
{
    public async Task<RefundStatusResult> RunAsync(Team team, CancellationToken cancellationToken)
    {
        var result = new RefundStatusResult();

        var open = await dbContext.Refunds
            .Where(r => r.TeamId == team.Id && r.SettledUtc == null)
            .OrderBy(r => r.RequestedUtc)
            .ToListAsync(cancellationToken);

        if (open.Count == 0)
        {
            return result;
        }

        if (!integrationState.ShouldCall(team, TeamIntegration.Square, "checking Square refund status"))
        {
            return result;
        }

        if (!team.IsSquareConfigured)
        {
            // Optional-integration posture. SettledUtc stays null, so everything backlogged is picked
            // up on the first tick after credentials are restored.
            logger.LogInformation("Square is not configured for team {TeamId} — {OpenCount} refund(s) awaiting a status check", team.Id, open.Count);
            return result;
        }

        var credentials = team.ToSquareCredentials();

        foreach (var refund in open)
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            try
            {
                var squareRefund = refund.SquareRefundId is null
                    // Never got an answer the first time. Same key, so this cannot double-refund.
                    ? await squareClient.RefundPaymentAsync(
                        credentials,
                        new SquareRefundRequest(refund.SquarePaymentId, refund.AmountUsd, refund.Reason, refund.SquareIdempotencyKey),
                        cancellationToken)
                    : await squareClient.GetRefundAsync(credentials, refund.SquareRefundId, cancellationToken);

                if (refund.SquareRefundId is null)
                {
                    refund.SquareRefundId = squareRefund.Id;
                    refund.SubmittedUtc = now;
                    result.Resubmitted++;
                    logger.LogInformation("Recovered in-flight refund {RefundId} — Square refund {SquareRefundId}", refund.Id, squareRefund.Id);
                }

                refund.Status = RefundService.MapStatus(squareRefund.Status);
                refund.LastCheckedUtc = now;

                if (refund.IsSettled)
                {
                    refund.SettledUtc = now;
                    refund.FailureDetail = refund.Status == RefundStatus.Completed ? null : $"Square reported the refund as {squareRefund.Status}.";
                    result.Settled++;

                    // A rejected or failed refund is the one outcome here somebody has to act on, and
                    // no screen is being watched for it. Logged as an error so it reaches the same
                    // place every other "this needs a human" signal in the Worker does.
                    if (refund.Status != RefundStatus.Completed)
                    {
                        logger.LogError("Square refund {SquareRefundId} for team {TeamId} ended {SquareRefundStatus} — {AmountUsd} was not returned to the buyer",
                            squareRefund.Id, team.Id, squareRefund.Status, refund.AmountUsd);
                    }
                }
                else
                {
                    // Written even though nothing changed, so a refund stuck pending for a fortnight
                    // reads as "checked, still pending" rather than "nothing is watching this".
                    result.StillPending++;
                }
            }
            catch (Exception ex)
            {
                result.Failed++;
                refund.LastCheckedUtc = now;
                refund.FailureDetail = ex.Message;
                logger.LogError(ex, "Failed to check Square refund status for refund {RefundId} — will retry on the next tick", refund.Id);
            }

            // Saved per row, so one failure never discards progress on the others. Note this is a
            // plain save of a tracked entity, not a ChangeTracker.Clear() on error — see CLAUDE.md.
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        logger.LogInformation("Refund status check finished for team {TeamId} ({TeamName}): {Result}", team.Id, team.Name, result);
        return result;
    }
}

public class RefundStatusResult
{
    /// <summary>Reached a terminal state on this tick — including the ones Square rejected.</summary>
    public int Settled { get; set; }

    public int StillPending { get; set; }

    /// <summary>Refunds whose original call never came back, re-sent with their persisted idempotency key.</summary>
    public int Resubmitted { get; set; }

    /// <summary>The status check itself failed. The refund is untouched and gets asked again next tick.</summary>
    public int Failed { get; set; }

    public override string ToString() =>
        $"settled {Settled}, still pending {StillPending}, resubmitted {Resubmitted}, check failed {Failed}";
}
