using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Square;

namespace VeSessionManager.Core.Payments;

/// <summary>
/// Square payment links never auto-expire — an Unpaid Payment's link stays live forever unless
/// something explicitly deletes it. This scan-based, per-team job deletes the Square link for any
/// Unpaid Payment whose CreatedUtc is older than Team.PurgeUnpaidLinkDays (default 30, admin-
/// configurable), then nulls out our own PaymentLinkUrl/SquarePaymentReferenceId/SquarePaymentLinkId
/// so the app never shows or resends a link that 404s on Square. SquareLinkPurgedUtc is both the
/// idempotency guard here and the flag PaymentGenerationService's own "no link -> generate one" scan
/// checks, so a purged Payment is never silently regenerated on the very next poll. See
/// docs/payment-link-purge.md.
/// </summary>
public class SquarePaymentLinkPurgeService(
    AppDbContext dbContext,
    ISquareClient squareClient,
    TimeProvider timeProvider,
    ILogger<SquarePaymentLinkPurgeService> logger)
{
    public async Task<SquarePaymentLinkPurgeResult> RunAsync(Team team, CancellationToken cancellationToken)
    {
        var result = new SquarePaymentLinkPurgeResult();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var threshold = now.AddDays(-team.PurgeUnpaidLinkDays);

        var payments = await dbContext.Payments
            .Where(p => p.Status == PaymentStatus.Unpaid
                        && p.PaymentLinkUrl != null
                        && p.SquareLinkPurgedUtc == null
                        && p.Candidate.Session.TeamId == team.Id
                        && p.CreatedUtc <= threshold)
            .ToListAsync(cancellationToken);

        if (payments.Count == 0)
        {
            return result;
        }

        if (!team.IsSquareConfigured)
        {
            // Optional-integration posture, same as PaymentGenerationService — skip quietly rather
            // than fail-log every poll; SquareLinkPurgedUtc stays null so the next poll purges
            // everything backlogged once Square is configured.
            logger.LogInformation("Square is not configured for team {TeamId} — {PendingCount} stale unpaid link(s) waiting to be purged; will purge automatically once configured",
                team.Id, payments.Count);
            return result;
        }

        var credentials = team.ToSquareCredentials();

        foreach (var payment in payments)
        {
            try
            {
                if (payment.SquarePaymentLinkId is not null)
                {
                    await squareClient.DeletePaymentLinkAsync(credentials, payment.SquarePaymentLinkId, cancellationToken);
                }
                else
                {
                    // Only possible for a Payment whose link predates SquarePaymentLinkId being
                    // tracked (before the youth-payment-confirmation migration) — there's nothing to
                    // call Square's delete API with, so the link stays live on Square's side. Known,
                    // accepted gap for pre-existing rows; still clear our own reference below so the
                    // app stops offering/resending it.
                    logger.LogWarning("Payment {PaymentId} has a stale Square link but no SquarePaymentLinkId on record — clearing our own reference, but the link itself could not be deleted on Square's side", payment.Id);
                }

                payment.PaymentLinkUrl = null;
                payment.SquarePaymentReferenceId = null;
                payment.SquarePaymentLinkId = null;
                payment.SquareLinkPurgedUtc = now;
                result.Purged++;
            }
            catch (Exception ex)
            {
                result.Failed++;
                logger.LogError(ex, "Failed to purge stale Square payment link for Payment {PaymentId} — will retry on next poll", payment.Id);
            }

            // Save after every item so a crash mid-run, or one failure, never loses progress
            // already made on others.
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        logger.LogInformation("Stale Square payment link purge finished for team {TeamId} ({TeamName}): {Result}", team.Id, team.Name, result);
        return result;
    }
}
