using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Square;

namespace VeSessionManager.Core.Payments;

/// <summary>
/// Phase 3: creates the InitialExam Payment row for every registered candidate and generates its
/// Square payment link, plus exposes CreateRetestPaymentAsync for the (not-yet-built, Phase 9)
/// admin "create retest payment" action. Scan-based like Phase 2's scheduling service — two
/// separate passes, each driven by comparing stored state, not an event queue:
///   1. Candidate has no InitialExam Payment row yet -> create one (Status = NotApplicable and no
///      link at all if the session's FeeConfiguration doesn't collect a fee; Unpaid otherwise).
///   2. Payment is Unpaid with no PaymentLinkUrl yet -> call Square for a link. Left null on
///      failure so the very next poll retries just the link generation, not row creation too.
///
/// Multi-team: this service now operates on one Team's candidates/payments per RunAsync call —
/// each team has its own separate Square merchant account (Team.IsSquareConfigured). See
/// docs/multi-team.md.
/// </summary>
public class PaymentGenerationService(
    AppDbContext dbContext,
    ISquareClient squareClient,
    TimeProvider timeProvider,
    ILogger<PaymentGenerationService> logger)
{
    public async Task<PaymentGenerationResult> RunAsync(Team team, CancellationToken cancellationToken)
    {
        var result = new PaymentGenerationResult();
        var now = timeProvider.GetUtcNow().UtcDateTime;

        var candidatesNeedingPayment = await dbContext.Candidates
            .Include(c => c.Session).ThenInclude(s => s.FeeConfiguration)
            .Where(c => c.PiiPurgedUtc == null
                        && c.Session.TeamId == team.Id
                        && c.Session.Status == SessionStatus.Active
                        && !c.Payments.Any(p => p.Reason == PaymentReason.InitialExam))
            .ToListAsync(cancellationToken);

        foreach (var candidate in candidatesNeedingPayment)
        {
            var feeConfiguration = candidate.Session.FeeConfiguration;
            var payment = new Payment
            {
                CandidateId = candidate.Id,
                Reason = PaymentReason.InitialExam,
                Amount = feeConfiguration.FeeCollectionEnabled ? feeConfiguration.ExamFeeAmount!.Value : 0m,
                Status = feeConfiguration.FeeCollectionEnabled ? PaymentStatus.Unpaid : PaymentStatus.NotApplicable,
                CreatedUtc = now
            };
            dbContext.Payments.Add(payment);
            result.PaymentsCreated++;
            logger.LogInformation("Created InitialExam Payment for candidate {CandidateId} (session {ExamToolsSessionId}), FeeCollectionEnabled={FeeCollectionEnabled}",
                candidate.Id, candidate.Session.ExamToolsSessionId, feeConfiguration.FeeCollectionEnabled);
        }

        if (candidatesNeedingPayment.Count > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        var paymentsNeedingLink = await dbContext.Payments
            .Include(p => p.Candidate).ThenInclude(c => c.Session)
            .Where(p => p.Status == PaymentStatus.Unpaid && p.PaymentLinkUrl == null && p.Candidate.Session.TeamId == team.Id)
            .ToListAsync(cancellationToken);

        if (paymentsNeedingLink.Count > 0 && !team.IsSquareConfigured)
        {
            // Square is an optional integration — a team that doesn't use it yet (or ever) would
            // otherwise see this fail and log an error every single poll, forever. Payment rows
            // still exist and wait correctly; the moment this team's SquareAccessToken is set, the
            // very next poll generates every backlogged link with no other config change needed.
            logger.LogInformation("Square is not configured for team {TeamId} — {PendingCount} Unpaid payment(s) waiting for a link; links will generate automatically once credentials are set",
                team.Id, paymentsNeedingLink.Count);
            paymentsNeedingLink.Clear();
        }

        var credentials = team.IsSquareConfigured
            ? new SquareCredentials(team.Id, team.SquareAccessToken!, team.SquareLocationId ?? "")
            : null;

        foreach (var payment in paymentsNeedingLink)
        {
            try
            {
                await GenerateLinkAsync(credentials!, payment, payment.Candidate.Session.Title, cancellationToken);
                result.LinksGenerated++;
            }
            catch (Exception ex)
            {
                result.LinksFailed++;
                logger.LogError(ex, "Failed to generate Square payment link for Payment {PaymentId}", payment.Id);
            }

            // Save after every item so a crash mid-run, or one payment's failure, never loses
            // progress already made on others.
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        logger.LogInformation("Payment generation finished for team {TeamId} ({TeamName}): {Result}", team.Id, team.Name, result);
        return result;
    }

    /// <summary>Manual entry point for the Session Manager's "create retest payment" action (surfaced in Phase 9's admin UI, which doesn't exist yet — this is the service method it will call). Keyed by candidateId, not Team, since the caller works from a specific candidate — the owning Team is resolved internally via the candidate's Session.</summary>
    public async Task<Payment> CreateRetestPaymentAsync(int candidateId, CancellationToken cancellationToken)
    {
        var candidate = await dbContext.Candidates
            .Include(c => c.Session).ThenInclude(s => s.FeeConfiguration)
            .Include(c => c.Session).ThenInclude(s => s.Team)
            .FirstOrDefaultAsync(c => c.Id == candidateId, cancellationToken)
            ?? throw new InvalidOperationException($"Candidate {candidateId} not found.");

        var feeConfiguration = candidate.Session.FeeConfiguration;
        var payment = new Payment
        {
            CandidateId = candidate.Id,
            Reason = PaymentReason.Retest,
            Amount = feeConfiguration.FeeCollectionEnabled ? feeConfiguration.ExamFeeAmount!.Value : 0m,
            Status = feeConfiguration.FeeCollectionEnabled ? PaymentStatus.Unpaid : PaymentStatus.NotApplicable,
            CreatedUtc = timeProvider.GetUtcNow().UtcDateTime
        };
        dbContext.Payments.Add(payment);
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Created Retest Payment for candidate {CandidateId}", candidate.Id);

        var team = candidate.Session.Team;
        if (feeConfiguration.FeeCollectionEnabled && team.IsSquareConfigured)
        {
            try
            {
                // Generate the link inline for responsive admin UX; if it fails, RunAsync's scan
                // picks this Payment up on the next poll (PaymentLinkUrl is still null) — same
                // retry-safety as the rest of this service.
                var credentials = new SquareCredentials(team.Id, team.SquareAccessToken!, team.SquareLocationId ?? "");
                await GenerateLinkAsync(credentials, payment, candidate.Session.Title, cancellationToken);
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to generate Square payment link for retest Payment {PaymentId} — will retry on next poll", payment.Id);
            }
        }

        return payment;
    }

    private async Task GenerateLinkAsync(SquareCredentials credentials, Payment payment, string sessionTitle, CancellationToken cancellationToken)
    {
        var itemName = payment.Reason == PaymentReason.Retest
            ? $"VE Exam Retest Fee - {sessionTitle}"
            : $"VE Exam Fee - {sessionTitle}";

        var link = await squareClient.CreatePaymentLinkAsync(
            credentials,
            new SquarePaymentLinkRequest(payment.Id.ToString(), itemName, payment.Amount),
            cancellationToken);

        payment.PaymentLinkUrl = link.Url;
        payment.SquarePaymentReferenceId = link.OrderId;
        logger.LogInformation("Generated Square payment link for Payment {PaymentId}", payment.Id);
    }
}
