using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Square;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

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
/// </summary>
public class SquareWebhookHandler(
    AppDbContext dbContext,
    IOptions<SquareOptions> options,
    TimeProvider timeProvider,
    ILogger<SquareWebhookHandler> logger)
{
    private const string PaymentUpdatedType = "payment.updated";
    private const string CompletedStatus = "COMPLETED";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<SquareWebhookOutcome> ProcessAsync(string rawBody, string? signatureHeader, CancellationToken cancellationToken)
    {
        var squareOptions = options.Value;
        if (string.IsNullOrEmpty(squareOptions.WebhookSignatureKey) || string.IsNullOrEmpty(squareOptions.WebhookNotificationUrl))
        {
            // WebhooksHelper.VerifySignature throws ArgumentNullException for either — treat an
            // unconfigured webhook the same as any other unverifiable request rather than 500ing.
            logger.LogWarning("Square webhook received but Square:WebhookSignatureKey/WebhookNotificationUrl are not configured — discarding payload");
            return SquareWebhookOutcome.InvalidSignature;
        }

        if (string.IsNullOrEmpty(signatureHeader)
            || !WebhooksHelper.VerifySignature(rawBody, signatureHeader, squareOptions.WebhookSignatureKey, squareOptions.WebhookNotificationUrl))
        {
            logger.LogWarning("Square webhook signature verification failed — discarding payload");
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
        if (envelope?.Type != PaymentUpdatedType || payment?.OrderId is null)
        {
            return SquareWebhookOutcome.Ignored;
        }

        if (payment.Status != CompletedStatus)
        {
            return SquareWebhookOutcome.Ignored;
        }

        var matched = await dbContext.Payments.FirstOrDefaultAsync(p => p.SquarePaymentReferenceId == payment.OrderId, cancellationToken);
        if (matched is null)
        {
            logger.LogWarning("Square payment.updated for order {SquareOrderId} did not match any Payment row", payment.OrderId);
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

        logger.LogInformation("Payment {PaymentId} marked Paid via Square order {SquareOrderId}", matched.Id, payment.OrderId);
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
        [JsonPropertyName("order_id")]
        public string? OrderId { get; set; }

        public string? Status { get; set; }
    }
}
