using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Square;

namespace VeSessionManager.Core.Payments;

public enum YouthConfirmationOutcome
{
    Success,

    /// <summary>No Payment matches this token — an invalid/stale/tampered-with link.</summary>
    NotFound,

    /// <summary>The Payment is no longer Unpaid (already paid, or a Session Manager marked it
    /// NotApplicable/expired) — nothing left to switch. Covers a candidate clicking both the
    /// standard and youth links (deliberately not guarded any more tightly than this — see
    /// docs/youth-payment-confirmation.md).</summary>
    AlreadyResolved,

    /// <summary>The session's FeeConfiguration has no YouthExamFeeAmount set — the youth flow isn't
    /// set up for this VEC's fee schedule yet, even though SupportsYouthProgram is true.</summary>
    FeeNotConfigured,

    /// <summary>The team's Square credentials aren't set — same optional-integration posture as
    /// PaymentGenerationService.</summary>
    SquareNotConfigured
}

public record YouthConfirmationResult(YouthConfirmationOutcome Outcome, string? RedirectUrl = null);

/// <summary>
/// Backs the public, unauthenticated youth-rate confirmation page (Pages/Public/YouthConfirm in
/// the Web project). A candidate who self-identifies as a youth is switched from the session's
/// standard exam fee to its youth rate: the existing Square payment link is deleted and a new one
/// generated at the youth amount, honor-system only — no age data exists anywhere in this app to
/// verify against. See docs/youth-payment-confirmation.md.
/// </summary>
public class YouthPaymentConfirmationService(
    AppDbContext dbContext,
    ISquareClient squareClient,
    TimeProvider timeProvider,
    ILogger<YouthPaymentConfirmationService> logger)
{
    /// <summary>Read-only eligibility check for the page's GET (decide whether to render the form or
    /// an explanatory message) — same checks as ConfirmAsync's early guards, no mutation, no Square
    /// calls.</summary>
    public async Task<YouthConfirmationOutcome> CheckEligibilityAsync(Guid token, CancellationToken cancellationToken)
    {
        var payment = await dbContext.Payments
            .Include(p => p.Candidate).ThenInclude(c => c.Session).ThenInclude(s => s.FeeConfiguration)
            .FirstOrDefaultAsync(p => p.YouthConfirmationToken == token, cancellationToken);
        if (payment is null)
        {
            return YouthConfirmationOutcome.NotFound;
        }

        if (payment.Status != PaymentStatus.Unpaid)
        {
            return YouthConfirmationOutcome.AlreadyResolved;
        }

        if (payment.Candidate.Session.FeeConfiguration.YouthExamFeeAmount is null)
        {
            return YouthConfirmationOutcome.FeeNotConfigured;
        }

        return YouthConfirmationOutcome.Success;
    }

    public async Task<YouthConfirmationResult> ConfirmAsync(Guid token, CancellationToken cancellationToken)
    {
        var payment = await dbContext.Payments
            .Include(p => p.Candidate).ThenInclude(c => c.Session).ThenInclude(s => s.Team)
            .Include(p => p.Candidate).ThenInclude(c => c.Session).ThenInclude(s => s.FeeConfiguration)
            .FirstOrDefaultAsync(p => p.YouthConfirmationToken == token, cancellationToken);
        if (payment is null)
        {
            return new YouthConfirmationResult(YouthConfirmationOutcome.NotFound);
        }

        if (payment.Status != PaymentStatus.Unpaid)
        {
            return new YouthConfirmationResult(YouthConfirmationOutcome.AlreadyResolved);
        }

        var feeConfiguration = payment.Candidate.Session.FeeConfiguration;
        if (feeConfiguration.YouthExamFeeAmount is null)
        {
            logger.LogWarning("Youth confirmation attempted for Payment {PaymentId} but its FeeConfiguration has no YouthExamFeeAmount set", payment.Id);
            return new YouthConfirmationResult(YouthConfirmationOutcome.FeeNotConfigured);
        }

        var team = payment.Candidate.Session.Team;
        if (!team.IsSquareConfigured)
        {
            return new YouthConfirmationResult(YouthConfirmationOutcome.SquareNotConfigured);
        }

        var credentials = team.ToSquareCredentials();

        if (payment.SquarePaymentLinkId is not null)
        {
            try
            {
                await squareClient.DeletePaymentLinkAsync(credentials, payment.SquarePaymentLinkId, cancellationToken);
            }
            catch (Exception ex)
            {
                // Best-effort: an orphaned standard-rate link left live in Square is a manually
                // cleanable inconvenience, not a reason to block the candidate's youth checkout.
                logger.LogWarning(ex, "Failed to delete old Square payment link {SquarePaymentLinkId} for Payment {PaymentId} — continuing to generate the youth-rate link anyway",
                    payment.SquarePaymentLinkId, payment.Id);
            }

            // The standard-rate link is gone, so its key must go with it — the youth call needs a
            // key Square has never seen, or it would replay the standard-rate link straight back.
            // Clearing both here (rather than assigning a fresh key unconditionally below) is what
            // makes the ??= on the next line safe.
            payment.SquarePaymentLinkId = null;
            payment.SquareIdempotencyKey = null;
        }

        // Persisted *before* calling Square and reused as-is on a retry — the crash-safety pattern
        // PaymentGenerationService.GenerateLinkAsync follows. Until 2026-08-03 this assigned a fresh
        // Guid unconditionally while claiming in a comment to do exactly what it now does: a crash
        // between Square accepting CreatePaymentLink and the save at the end of this method left the
        // Payment Unpaid, so the candidate could confirm again, mint a *different* key, and get a
        // second live Square order with the first orphaned and still payable. With the key persisted
        // first and reused, the retried call is an idempotent replay that returns the same link.
        payment.SquareIdempotencyKey ??= Guid.NewGuid().ToString();
        await dbContext.SaveChangesAsync(cancellationToken);

        var link = await squareClient.CreatePaymentLinkAsync(
            credentials,
            new SquarePaymentLinkRequest(payment.Id.ToString(), $"VE Exam Fee (Youth Rate) - {payment.Candidate.Session.Title}", feeConfiguration.YouthExamFeeAmount.Value, payment.SquareIdempotencyKey),
            cancellationToken);

        payment.Amount = feeConfiguration.YouthExamFeeAmount.Value;
        payment.PaymentLinkUrl = link.Url;
        payment.SquarePaymentReferenceId = link.OrderId;
        payment.SquarePaymentLinkId = link.Id;

        var now = timeProvider.GetUtcNow().UtcDateTime;
        dbContext.AddAuditLog(null, "YouthRateConfirmed", nameof(Payment), payment.Id,
            $"Candidate self-confirmed youth rate via public link; Payment switched from standard rate to {Usd.Format(feeConfiguration.YouthExamFeeAmount.Value)}.", now);

        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Confirmed youth rate for Payment {PaymentId}, candidate {CandidateId}", payment.Id, payment.CandidateId);

        return new YouthConfirmationResult(YouthConfirmationOutcome.Success, link.Url);
    }
}
