using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Integrations;
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

/// <summary>What the public youth page needs to render before the candidate confirms anything (#192).</summary>
/// <param name="TeamContactEmail">The team's reply-to address, or null when it has no EmailSettings row.</param>
/// <param name="IntroHtml">
/// The team's own <see cref="Entities.EmailSettings.YouthConfirmIntroHtml"/>, or
/// <see cref="YouthConfirmDefaults.IntroHtml"/> when the team has none set (no row at all, or the
/// field is null/blank) — resolved here so the page never has to know the fallback exists.
/// </param>
public record YouthEligibility(YouthConfirmationOutcome Outcome, string? TeamContactEmail, string IntroHtml);

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
    TeamIntegrationState integrationState,
    ILogger<YouthPaymentConfirmationService> logger)
{
    /// <summary>Read-only eligibility check for the page's GET (decide whether to render the form or
    /// an explanatory message) — same checks as ConfirmAsync's early guards, no mutation, no Square
    /// calls.</summary>
    /// <param name="cancellationToken"></param>
    /// <returns>
    /// The outcome, plus the team's own reply-to address when there is one (#192).
    ///
    /// <para>The page needs it to tell a candidate under 13 where to send their COPPA consent form.
    /// Read from the team's <c>EmailSettings</c> rather than written into the markup, because the
    /// page is shared by every team on the deployment and a hardcoded address would send one team's
    /// paperwork to another. Null when the team has no settings row yet, and the copy degrades to
    /// naming no address rather than naming a wrong one.</para>
    /// </returns>
    public async Task<YouthEligibility> CheckEligibilityAsync(Guid token, CancellationToken cancellationToken)
    {
        var payment = await dbContext.Payments
            .Include(p => p.Candidate).ThenInclude(c => c.Session).ThenInclude(s => s.FeeConfiguration)
            .FirstOrDefaultAsync(p => p.YouthConfirmationToken == token, cancellationToken);
        if (payment is null)
        {
            return new YouthEligibility(YouthConfirmationOutcome.NotFound, null, YouthConfirmDefaults.IntroHtml);
        }

        if (payment.Status != PaymentStatus.Unpaid)
        {
            return new YouthEligibility(YouthConfirmationOutcome.AlreadyResolved, null, YouthConfirmDefaults.IntroHtml);
        }

        if (payment.Candidate.Session.FeeConfiguration.YouthExamFeeAmount is null)
        {
            return new YouthEligibility(YouthConfirmationOutcome.FeeNotConfigured, null, YouthConfirmDefaults.IntroHtml);
        }

        // Projected, not the whole row: this is an anonymous page and EmailSettings carries more
        // than it needs to know.
        var settings = await dbContext.EmailSettings
            .Where(e => e.TeamId == payment.Candidate.Session.TeamId)
            .Select(e => new { e.ReplyToAddress, e.YouthConfirmIntroHtml })
            .FirstOrDefaultAsync(cancellationToken);
        var introHtml = string.IsNullOrWhiteSpace(settings?.YouthConfirmIntroHtml) ? YouthConfirmDefaults.IntroHtml : settings.YouthConfirmIntroHtml;

        return new YouthEligibility(YouthConfirmationOutcome.Success, settings?.ReplyToAddress, introHtml);
    }

    /// <param name="declaredUnder13">
    /// The candidate's answer to "is the candidate under 13?" (2026-08-26) — always supplied, since
    /// the page requires an answer before this is called. Recorded on the candidate regardless of
    /// the youth-rate outcome below; the COPPA question and the fee-switch are independent facts.
    /// </param>
    /// <param name="coppaFormSent">
    /// Whether the candidate checked "I have sent this form to ExamTools." Only meaningful — and
    /// only ever true — when <paramref name="declaredUnder13"/> is true; the page's own validation
    /// refuses to reach here otherwise.
    /// </param>
    public async Task<YouthConfirmationResult> ConfirmAsync(Guid token, bool declaredUnder13, bool coppaFormSent, CancellationToken cancellationToken)
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

        // Recorded before any of the fee/Square outcome checks below, and saved immediately: the
        // COPPA declaration is a fact about the candidate independent of whether the youth-rate
        // switch itself succeeds, so a FeeNotConfigured/SquareNotConfigured return further down must
        // not lose it.
        //
        // The latest submission wins in FULL — a "No" clears any COPPA timestamp a previous "Yes"
        // stamped (live-caught 2026-08-27: a candidate who resubmitted the form as over-13 kept a
        // stale "COPPA form sent" record, and the roster then showed a compliance fact that was no
        // longer claimed). An unchanged repeat "Yes" keeps its original confirmation time.
        payment.Candidate.DeclaredUnder13 = declaredUnder13;
        payment.Candidate.CoppaFormSentConfirmedUtc = declaredUnder13 && coppaFormSent
            ? payment.Candidate.CoppaFormSentConfirmedUtc ?? timeProvider.GetUtcNow().UtcDateTime
            : null;
        await dbContext.SaveChangesAsync(cancellationToken);

        var feeConfiguration = payment.Candidate.Session.FeeConfiguration;
        if (feeConfiguration.YouthExamFeeAmount is null)
        {
            logger.LogWarning("Youth confirmation attempted for Payment {PaymentId} but its FeeConfiguration has no YouthExamFeeAmount set", payment.Id);
            return new YouthConfirmationResult(YouthConfirmationOutcome.FeeNotConfigured);
        }

        var team = payment.Candidate.Session.Team;

        // Switched off suppresses the outbound Square calls below (#64). Reported as
        // SquareNotConfigured because that is what the caller can already render, and the log line
        // from ShouldCall says which of the two it actually was — a muted team is a deliberate,
        // temporary state that the person who muted it knows about.
        if (!integrationState.ShouldCall(team, TeamIntegration.Square, "replacing a payment link with the youth rate")
            || !team.IsSquareConfigured)
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
        var coppaNote = declaredUnder13 ? $"; declared under 13, COPPA form sent to ExamTools confirmed: {coppaFormSent}" : "";
        dbContext.AddAuditLog(null, "YouthRateConfirmed", nameof(Payment), payment.Id,
            $"Candidate self-confirmed youth rate via public link; Payment switched from standard rate to {Usd.Format(feeConfiguration.YouthExamFeeAmount.Value)}{coppaNote}.", now,
            teamId: team.Id);

        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Confirmed youth rate for Payment {PaymentId}, candidate {CandidateId}", payment.Id, payment.CandidateId);

        return new YouthConfirmationResult(YouthConfirmationOutcome.Success, link.Url);
    }
}
