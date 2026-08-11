using Microsoft.AspNetCore.Http.Features;
using VeSessionManager.Core.Square;

namespace VeSessionManager.Web;

/// <summary>Phase 3: receives Square's payment.updated webhook. Multi-team: routed per-team (see docs/multi-team.md) since signature verification needs that team's own WebhookSignatureKey before the payload can even be parsed.</summary>
public static class SquareWebhookEndpoint
{
    /// <summary>
    /// Hard cap on the request body this endpoint will read (2026-08-03 hardening). This is the
    /// only unauthenticated endpoint in the app that buffers its whole body into a string, and it
    /// must do so *before* the signature can be checked — so without a cap, anyone on the internet
    /// could force repeated 30MB (Kestrel's default) large-object-heap allocations at whatever rate
    /// they can open connections. Square's payment.updated payloads are a few KB; 64KB is orders of
    /// magnitude of headroom.
    /// </summary>
    private const long MaxBodyBytes = 64 * 1024;

    public static IEndpointRouteBuilder MapSquareWebhook(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/webhooks/square/{teamId:int}", async (
            int teamId,
            HttpRequest request,
            SquareWebhookHandler handler,
            ILogger<Program> logger,
            CancellationToken cancellationToken) =>
        {
            // Both halves are needed: Content-Length rejects a declared oversize body without
            // reading it, and lowering MaxRequestBodySize makes the read itself throw for a chunked
            // body that lies about (or omits) its length.
            if (request.ContentLength > MaxBodyBytes)
            {
                logger.LogWarning("Rejected an oversized Square webhook body ({ContentLength} bytes)", request.ContentLength);
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            var sizeFeature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (sizeFeature is { IsReadOnly: false })
            {
                sizeFeature.MaxRequestBodySize = MaxBodyBytes;
            }

            // Signature verification needs the exact raw bytes Square signed — read as a plain
            // string before anything else touches the body (no JSON model binding upstream of this).
            using var reader = new StreamReader(request.Body);
            var rawBody = await reader.ReadToEndAsync(cancellationToken);
            var signatureHeader = request.Headers["x-square-hmacsha256-signature"].ToString();

            var outcome = await handler.ProcessAsync(teamId, rawBody, signatureHeader, cancellationToken);

            if (outcome == SquareWebhookOutcome.InvalidSignature)
            {
                logger.LogWarning("Rejected a Square webhook with an invalid signature");
                return Results.Unauthorized();
            }

            // Processed or Ignored both acknowledge with 2xx — per Square's retry behavior,
            // "we understood this and chose not to act on it" must look the same as "handled" to
            // avoid Square retrying an event we'll never match (e.g. an unrecognized order id).
            return Results.Ok();
        })
        // Square is not a signed-in user and never will be. Required since the fallback policy
        // landed (#158) — a minimal-API endpoint with no authorization metadata inherits it, and a
        // 401 here would be invisible from inside the app: Square would retry, give up, and payments
        // would simply stop being recorded. The endpoint's real gate is HMAC signature verification
        // against the team's own webhook signature key, which is stronger than any cookie.
        .AllowAnonymous();

        return endpoints;
    }
}
