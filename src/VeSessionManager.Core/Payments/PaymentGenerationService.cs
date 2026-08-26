using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Integrations;
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
///      failure so the very next poll retries just the link generation, not row creation too. A
///      Payment whose link SquarePaymentLinkPurgeService already deleted (SquareLinkPurgedUtc set)
///      is deliberately excluded here too, or this pass would immediately regenerate the link that
///      was just purged for being stale — see docs/payment-link-purge.md.
///
/// Multi-team: this service now operates on one Team's candidates/payments per RunAsync call —
/// each team has its own separate Square merchant account (Team.IsSquareConfigured). See
/// docs/multi-team.md.
///
/// GenerateLinkAsync persists a Payment.SquareIdempotencyKey *before* calling Square and reuses it
/// on every retry, so a crash between Square's call succeeding and PaymentLinkUrl being saved
/// replays the same request (Square returns the original link) instead of creating a duplicate —
/// same bug class as the Discord/Zoom duplicate-event issue fixed in SessionEventSchedulingService,
/// see docs/square-payments.md.
/// </summary>
public class PaymentGenerationService(
    AppDbContext dbContext,
    ISquareClient squareClient,
    TimeProvider timeProvider,
    TeamIntegrationState integrationState,
    ILogger<PaymentGenerationService> logger)
{
    /// <param name="onlySessionId">Restrict the run to one session's candidates (the Detail page's
    /// session-scoped refresh); null (every scheduled/team-wide run) scans the whole team.</param>
    public async Task<PaymentGenerationResult> RunAsync(Team team, CancellationToken cancellationToken, int? onlySessionId = null)
    {
        var result = new PaymentGenerationResult();
        var now = timeProvider.GetUtcNow().UtcDateTime;

        // Status == Active means "not cancelled", NOT "not finished" (CLAUDE.md) — on its own it
        // matched every session the team has ever run, so the historical import's year of backfilled
        // candidates all queued up for payment. See PaymentEligibilityWindow.
        var paymentCutoff = PaymentEligibilityWindow.CutoffUtc(now);

        var candidatesNeedingPayment = await dbContext.Candidates
            .Include(c => c.Session).ThenInclude(s => s.FeeConfiguration)
            .Include(c => c.Session).ThenInclude(s => s.Vec)
            .Where(c => c.PiiPurgedUtc == null
                        && c.Session.TeamId == team.Id
                        && c.Session.Status == SessionStatus.Active
                        && c.Session.ScheduledStartUtc >= paymentCutoff
                        && (onlySessionId == null || c.SessionId == onlySessionId)
                        && !c.Payments.Any(p => p.Reason == PaymentReason.InitialExam))
            .ToListAsync(cancellationToken);

        foreach (var candidate in candidatesNeedingPayment)
        {
            var feeConfiguration = candidate.Session.FeeConfiguration;
            var payment = new Payment
            {
                CandidateId = candidate.Id,
                CandidateNameSnapshot = candidate.Name,
                Reason = PaymentReason.InitialExam,
                Amount = feeConfiguration.FeeCollectionEnabled ? feeConfiguration.ExamFeeAmount!.Value : 0m,
                Status = feeConfiguration.FeeCollectionEnabled ? PaymentStatus.Unpaid : PaymentStatus.NotApplicable,
                YouthConfirmationToken = feeConfiguration.FeeCollectionEnabled && candidate.Session.Vec.SupportsYouthProgram
                    ? Guid.NewGuid()
                    : null,
                CreatedUtc = now
            };
            dbContext.Payments.Add(payment);

            // Saved per candidate rather than as one batch at the end (2026-08-03). The unique index
            // on (CandidateId, Reason) means a concurrent run in the other process — Web's manual
            // refresh racing the Worker's tick — can make exactly one of these inserts fail. With a
            // single batch save that one collision would roll back every other candidate's payment
            // in the same pass; per-candidate, it costs only the row the other process already
            // created. Same crash-safety reasoning as the link-generation loop below.
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                result.PaymentsCreated++;
                logger.LogInformation("Created InitialExam Payment for candidate {CandidateId} (session {ExamToolsSessionId}), FeeCollectionEnabled={FeeCollectionEnabled}",
                    candidate.Id, candidate.Session.ExamToolsSessionId, feeConfiguration.FeeCollectionEnabled);
            }
            catch (DbUpdateException ex)
            {
                // Detaching first is required — a failed entity stays in the change tracker and
                // would be retried (and fail again) by every later save in this pass, including
                // JobRunHistoryLogger's.
                dbContext.Entry(payment).State = EntityState.Detached;

                // Then ask the database what actually happened, rather than assuming. Catching
                // DbUpdateException alone would lump two very different causes together: the
                // expected uniqueness collision, and a transient "database is locked" — which is
                // entirely realistic here, since Web and Worker share one SQLite file. Both are
                // survivable (the next tick retries either way), but reporting a lock contention
                // as "the other process already created it" would send someone hunting a race that
                // isn't happening. One extra query, only on the failure path.
                //
                // Provider-agnostic on purpose: no SQLite error codes, so this keeps working if the
                // database is ever swapped.
                var alreadyExists = await dbContext.Payments
                    .AnyAsync(p => p.CandidateId == candidate.Id && p.Reason == PaymentReason.InitialExam, cancellationToken);

                if (alreadyExists)
                {
                    result.PaymentsSkippedAlreadyExisted++;
                    logger.LogInformation("Skipped creating an InitialExam Payment for candidate {CandidateId} — one already exists, which means the other process (manual refresh vs. scheduled job) created it first",
                        candidate.Id);
                }
                else
                {
                    result.PaymentsFailed++;
                    logger.LogError(ex, "Failed to create an InitialExam Payment for candidate {CandidateId}, and no payment exists afterwards — this is NOT the known Web-vs-Worker collision; the next run will retry",
                        candidate.Id);
                }
            }
        }

        // Bounded by the same window as creation above, and this is the half that matters most: the
        // ~1710 Unpaid payments the unbounded version already created for backfilled historical
        // candidates are still in the table, and without this bound the first poll after Square
        // credentials are set would mint a real payment link for every one of them.
        var paymentsNeedingLink = await dbContext.Payments
            .Include(p => p.Candidate).ThenInclude(c => c.Session)
            .Where(p => p.Status == PaymentStatus.Unpaid
                        && p.PaymentLinkUrl == null
                        && p.SquareLinkPurgedUtc == null
                        && p.Candidate.Session.TeamId == team.Id
                        // Same filter the creation pass above applies (#281). Without it a session
                        // cancelled after its Payment rows existed but before Square was configured
                        // gets live checkout links on the next poll — a real ordering, since Square
                        // is optional and often set up later. SessionEventSchedulingService already
                        // tears down Zoom and Discord for a cancelled session; nothing tears these
                        // down, so the only defence is never minting them.
                        && p.Candidate.Session.Status == SessionStatus.Active
                        && p.Candidate.Session.ScheduledStartUtc >= paymentCutoff
                        && (onlySessionId == null || p.Candidate.SessionId == onlySessionId))
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

        // Switched off (#64). Unlike the unconfigured branch above, nothing is queued and nothing
        // says "will generate automatically once...": re-enabling starts fresh from that moment, so
        // a week of muted links cannot suddenly appear against a real merchant account.
        if (!integrationState.ShouldCall(team, TeamIntegration.Square, "creating Square payment links"))
        {
            paymentsNeedingLink.Clear();
        }

        var credentials = team.IsSquareConfigured
            ? team.ToSquareCredentials()
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
            .Include(c => c.Session).ThenInclude(s => s.Vec)
            .FirstOrDefaultAsync(c => c.Id == candidateId, cancellationToken)
            ?? throw new InvalidOperationException($"Candidate {candidateId} not found.");

        var feeConfiguration = candidate.Session.FeeConfiguration;
        var payment = new Payment
        {
            CandidateId = candidate.Id,
            CandidateNameSnapshot = candidate.Name,
            Reason = PaymentReason.Retest,
            Amount = feeConfiguration.FeeCollectionEnabled ? feeConfiguration.ExamFeeAmount!.Value : 0m,
            Status = feeConfiguration.FeeCollectionEnabled ? PaymentStatus.Unpaid : PaymentStatus.NotApplicable,
            YouthConfirmationToken = feeConfiguration.FeeCollectionEnabled && candidate.Session.Vec.SupportsYouthProgram
                ? Guid.NewGuid()
                : null,
            CreatedUtc = timeProvider.GetUtcNow().UtcDateTime
        };
        dbContext.Payments.Add(payment);
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Created Retest Payment for candidate {CandidateId}", candidate.Id);

        var team = candidate.Session.Team;
        if (feeConfiguration.FeeCollectionEnabled
            && integrationState.ShouldCall(team, TeamIntegration.Square, "creating a Square payment link")
            && team.IsSquareConfigured)
        {
            try
            {
                // Generate the link inline for responsive admin UX; if it fails, RunAsync's scan
                // picks this Payment up on the next poll (PaymentLinkUrl is still null) — same
                // retry-safety as the rest of this service.
                var credentials = team.ToSquareCredentials();
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

        // Persisted *before* calling Square, and reused as-is if already set from a previous
        // attempt — a crash between Square's API call succeeding and PaymentLinkUrl being saved
        // would otherwise generate a second, different link for the same fee on the next poll
        // (same class of bug as the Discord/Zoom duplicate-event issue, see docs/square-payments.md). Square's own
        // idempotency guarantee means replaying the same key returns the original link instead of
        // creating a new one.
        if (payment.SquareIdempotencyKey is null)
        {
            payment.SquareIdempotencyKey = Guid.NewGuid().ToString();
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        var link = await squareClient.CreatePaymentLinkAsync(
            credentials,
            new SquarePaymentLinkRequest(payment.Id.ToString(), itemName, payment.Amount, payment.SquareIdempotencyKey),
            cancellationToken);

        payment.PaymentLinkUrl = link.Url;
        payment.SquarePaymentReferenceId = link.OrderId;
        payment.SquarePaymentLinkId = link.Id;
        logger.LogInformation("Generated Square payment link for Payment {PaymentId}", payment.Id);
    }
}
