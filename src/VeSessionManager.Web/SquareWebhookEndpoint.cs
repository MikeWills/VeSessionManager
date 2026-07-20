using VeSessionManager.Core.Square;

namespace VeSessionManager.Web;

/// <summary>Phase 3: receives Square's payment.updated webhook. Multi-team: routed per-team (see docs/multi-team.md) since signature verification needs that team's own WebhookSignatureKey before the payload can even be parsed.</summary>
public static class SquareWebhookEndpoint
{
    public static IEndpointRouteBuilder MapSquareWebhook(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/webhooks/square/{teamId:int}", async (
            int teamId,
            HttpRequest request,
            SquareWebhookHandler handler,
            ILogger<Program> logger,
            CancellationToken cancellationToken) =>
        {
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
        });

        return endpoints;
    }
}
