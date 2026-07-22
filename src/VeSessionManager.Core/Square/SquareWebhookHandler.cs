using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Square;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Payments;

namespace VeSessionManager.Core.Square;

public enum SquareWebhookOutcome
{
    /// <summary>Signature didn't verify — caller should respond 401 and must not trust the payload.</summary>
    InvalidSignature,

    /// <summary>A payment.updated/COMPLETED event was matched to a Payment row and applied.</summary>
    Processed,

    /// <summary>Valid signature, but nothing to do — wrong event type, non-COMPLETED status, unmatched reference id, or already-applied (duplicate delivery). Still acknowledge with 2xx per Square's retry behavior.</summary>
    Ignored
}

/// <summary>
/// Handles incoming Square webhooks. Verifies the signature via the SDK's own crypto helper
/// (real HMAC-SHA256, not something worth re-implementing), then applies payment.updated/
/// COMPLETED events to the matching Payment row. Matching is by Square's Order id
/// (Payment.SquarePaymentReferenceId), not by the reference_id we set when creating the payment
/// link — Square's payment.updated payload does not echo reference_id back, only order_id (see
/// docs/square-payments.md).
///
/// Multi-team: each team has its own Square account/WebhookSignatureKey, so the route
/// (/webhooks/square/{teamId}, see SquareWebhookEndpoint) identifies which team's key to verify
/// against *before* the payload can even be parsed — the route param, not anything in the body,
/// is what determines this. See docs/multi-team.md.
///
/// A COMPLETED event whose order_id doesn't match any Payment row (e.g. a payment taken through a
/// separate online payment page, not one this app generated a link for) is handed off to
/// SquarePaymentMatchingService rather than just logged and discarded — see its own doc comment.
/// </summary>
public class SquareWebhookHandler(
    AppDbContext dbContext,
    SquarePaymentMatchingService paymentMatchingService,
    TimeProvider timeProvider,
    ILogger<SquareWebhookHandler> logger)
{
    private const string PaymentUpdatedType = "payment.updated";
    private const string CompletedStatus = "COMPLETED";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<SquareWebhookOutcome> ProcessAsync(int teamId, string rawBody, string? signatureHeader, CancellationToken cancellationToken)
    {
        var team = await dbContext.Teams.FindAsync([teamId], cancellationToken);
        if (team is null || !team.IsSquareWebhookConfigured)
        {
            // Never leak whether teamId is a real row vs. just unconfigured — both look identical
            // to the caller. WebhooksHelper.VerifySignature throws ArgumentNullException for a
            // blank key/url anyway — treat an unconfigured webhook the same as any other
            // unverifiable request rather than 500ing.
            logger.LogWarning("Square webhook received for team {TeamId}, but that team doesn't exist or its Square webhook isn't configured — discarding payload", teamId);
            return SquareWebhookOutcome.InvalidSignature;
        }

        if (string.IsNullOrEmpty(signatureHeader)
            || !WebhooksHelper.VerifySignature(rawBody, signatureHeader, team.SquareWebhookSignatureKey!, team.SquareWebhookNotificationUrl!))
        {
            logger.LogWarning("Square webhook signature verification failed for team {TeamId} — discarding payload", teamId);
            return SquareWebhookOutcome.InvalidSignature;
        }

        SquareWebhookEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<SquareWebhookEnvelope>(rawBody, JsonOptions);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Square webhook had a valid signature but an unparseable body");
            return SquareWebhookOutcome.Ignored;
        }

        var payment = envelope?.Data?.Object?.Payment;
        if (envelope?.Type != PaymentUpdatedType || payment?.OrderId is null || payment.Id is null)
        {
            return SquareWebhookOutcome.Ignored;
        }

        if (payment.Status != CompletedStatus)
        {
            return SquareWebhookOutcome.Ignored;
        }

        var matched = await dbContext.Payments
            .Include(p => p.Candidate).ThenInclude(c => c.Session).ThenInclude(s => s.Team)
            .FirstOrDefaultAsync(p => p.SquarePaymentReferenceId == payment.OrderId, cancellationToken);
        if (matched is null)
        {
            // Not one of this app's own generated orders — try email-matching against a candidate,
            // or record it for manual review, rather than silently dropping it.
            var amountUsd = (payment.AmountMoney?.Amount ?? 0) / 100m;
            var outcome = await paymentMatchingService.HandleUnmatchedOrderAsync(
                teamId, payment.OrderId, payment.Id, amountUsd, payment.BuyerEmailAddress, cancellationToken);
            return outcome == SquareUnmatchedPaymentOutcome.AlreadyRecorded ? SquareWebhookOutcome.Ignored : SquareWebhookOutcome.Processed;
        }

        if (matched.Candidate.Session.TeamId != teamId)
        {
            // A real signature verified, but for a Payment that belongs to a different team than
            // the route claims — almost certainly a misconfigured WebhookNotificationUrl pointing
            // at the wrong team's route, not an attack (the signature is genuinely valid for the
            // team it belongs to). Treat as a config error to investigate, not silently apply it
            // to the wrong team's payment.
            logger.LogWarning("Square payment.updated for order {SquareOrderId} matched a Payment belonging to team {ActualTeamId}, not the route's team {RouteTeamId} — check that team's SquareWebhookNotificationUrl",
                payment.OrderId, matched.Candidate.Session.TeamId, teamId);
            return SquareWebhookOutcome.Ignored;
        }

        if (matched.Status == PaymentStatus.Paid)
        {
            // Square retries webhook delivery — a second COMPLETED event for the same order is expected, not an error.
            return SquareWebhookOutcome.Ignored;
        }

        matched.Status = PaymentStatus.Paid;
        matched.PaidDateUtc = timeProvider.GetUtcNow().UtcDateTime;
        await dbContext.SaveChangesAsync(cancellationToken);

        // Covers "the session was already marked completed before this payment's webhook arrived"
        // — the other direction of SquarePaymentMatchingService.CompleteOrderIfEligibleAsync's
        // "either side can happen second" pairing (SessionActionService.MarkCompletedAsync covers
        // the reverse: payment already Paid, session marked completed after).
        await paymentMatchingService.TryCompleteOrderAsync(matched, cancellationToken);

        logger.LogInformation("Payment {PaymentId} marked Paid via Square order {SquareOrderId} (team {TeamId})", matched.Id, payment.OrderId, teamId);
        return SquareWebhookOutcome.Processed;
    }

    // Wire shapes for the small slice of Square's webhook envelope Phase 3 reads — see
    // https://developer.squareup.com/reference/square/payments-api/webhooks/payment.updated.
    private class SquareWebhookEnvelope
    {
        public string? Type { get; set; }
        public SquareWebhookData? Data { get; set; }
    }

    private class SquareWebhookData
    {
        [JsonPropertyName("object")]
        public SquareWebhookObject? Object { get; set; }
    }

    private class SquareWebhookObject
    {
        public SquareWebhookPayment? Payment { get; set; }
    }

    private class SquareWebhookPayment
    {
        public string? Id { get; set; }

        [JsonPropertyName("order_id")]
        public string? OrderId { get; set; }

        public string? Status { get; set; }

        [JsonPropertyName("amount_money")]
        public SquareWebhookMoney? AmountMoney { get; set; }

        /// <summary>Only present when Square's checkout collected a buyer email — the fallback matching signal for a payment.updated event whose order_id isn't one of this app's own. See https://developer.squareup.com/reference/square/objects/Payment.</summary>
        [JsonPropertyName("buyer_email_address")]
        public string? BuyerEmailAddress { get; set; }
    }

    private class SquareWebhookMoney
    {
        public long Amount { get; set; }
    }
}
