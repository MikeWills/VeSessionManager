using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Square;
using Square.Checkout_;

using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Square;

/// <summary>
/// Wraps the official Square .NET SDK's Checkout API to create Order-based payment links (not
/// QuickPay — an Order is what lets us set ReferenceId, per
/// https://developer.squareup.com/reference/square/objects/Order). Registered as a singleton;
/// like ExamToolsClient, that singleton now manages one independent SDK client instance *per
/// team*, keyed by SquareCredentials.TeamId (each team has its own separate Square merchant
/// account — not shared, confirmed with the user). The SDK client has no per-instance login/
/// session constraint the way Discord's does, so this is a plain cache, no locking needed.
/// Construction is still deferred to first use per team (not eager): a constructor throw would be
/// a *host-stopping* failure (.NET's default BackgroundServiceExceptionBehavior is StopHost) — it
/// would take down ExamTools/Zoom/Discord polling too, not just payment generation.
/// </summary>
public sealed class SquareClient : ISquareClient
{
    private readonly ILogger<SquareClient> _logger;
    private readonly ConcurrentDictionary<int, CachedSquareClient> _clientsByTeamId = new();

    public SquareClient(ILogger<SquareClient> logger)
    {
        _logger = logger;
    }

    public async Task<SquarePaymentLink> CreatePaymentLinkAsync(SquareCredentials credentials, SquarePaymentLinkRequest request, CancellationToken cancellationToken)
    {
        var client = GetOrCreateClient(credentials);

        if (string.IsNullOrWhiteSpace(credentials.LocationId))
        {
            // Mirrors Team.IsSquareConfigured deliberately only checking AccessToken, same as the
            // pre-multi-team IsConfigured did — LocationId is validated here instead, at the point
            // it's actually needed, so a team can get as far as "linked their Square account" before
            // also needing to pick a Location.
            throw new InvalidOperationException($"Team {credentials.TeamId}'s Square LocationId is not configured.");
        }

        // Amounts are stored as decimal USD dollars on Payment; Square wants an integer count of
        // the currency's smallest unit (cents for USD).
        var amountCents = (long)Math.Round(request.AmountUsd * 100m, MidpointRounding.AwayFromZero);

        var response = await client.Checkout.PaymentLinks.CreateAsync(
            new CreatePaymentLinkRequest
            {
                IdempotencyKey = request.IdempotencyKey,
                Order = new Order
                {
                    LocationId = credentials.LocationId,
                    ReferenceId = request.ReferenceId,
                    LineItems =
                    [
                        new OrderLineItem
                        {
                            Name = request.ItemName,
                            Quantity = "1",
                            BasePriceMoney = new Money { Amount = amountCents, Currency = Currency.Usd }
                        }
                    ]
                }
            },
            cancellationToken: cancellationToken);

        if (response.Errors is not null && response.Errors.Any())
        {
            var detail = string.Join("; ", response.Errors.Select(e => $"{e.Category} {e.Code}: {e.Detail}"));
            throw new InvalidOperationException($"Square rejected the payment link request: {detail}");
        }

        var paymentLink = response.PaymentLink
            ?? throw new InvalidOperationException("Square create-payment-link response had no payment_link and no errors.");

        _logger.LogInformation("Created Square payment link for team {TeamId}, order {SquareOrderId}", credentials.TeamId, paymentLink.OrderId);
        return new SquarePaymentLink
        {
            Id = paymentLink.Id ?? throw new InvalidOperationException("Square payment link response had no id."),
            OrderId = paymentLink.OrderId ?? throw new InvalidOperationException("Square payment link response had no order_id."),
            Url = paymentLink.Url ?? throw new InvalidOperationException("Square payment link response had no url.")
        };
    }

    public async Task DeletePaymentLinkAsync(SquareCredentials credentials, string paymentLinkId, CancellationToken cancellationToken)
    {
        var client = GetOrCreateClient(credentials);

        var response = await client.Checkout.PaymentLinks.DeleteAsync(
            new DeletePaymentLinksRequest { Id = paymentLinkId },
            cancellationToken: cancellationToken);

        if (response.Errors is not null && response.Errors.Any())
        {
            if (response.Errors.All(e => e.Code == ErrorCode.NotFound))
            {
                // Already deleted — a retried call (e.g. a crash between Square's delete succeeding
                // and the caller persisting that fact) or the link was removed some other way.
                // Either way, nothing left to do; same idempotent-no-op treatment as
                // CompleteOrderAsync's already-Completed check above.
                return;
            }

            var detail = string.Join("; ", response.Errors.Select(e => $"{e.Category} {e.Code}: {e.Detail}"));
            throw new InvalidOperationException($"Square rejected the delete-payment-link request for link {paymentLinkId}: {detail}");
        }

        _logger.LogInformation("Deleted Square payment link {SquarePaymentLinkId} for team {TeamId}", paymentLinkId, credentials.TeamId);
    }

    public async Task CompleteOrderAsync(SquareCredentials credentials, string orderId, CancellationToken cancellationToken)
    {
        var client = GetOrCreateClient(credentials);

        var getResponse = await client.Orders.GetAsync(new GetOrdersRequest { OrderId = orderId }, cancellationToken: cancellationToken);
        if (getResponse.Errors is not null && getResponse.Errors.Any())
        {
            var detail = string.Join("; ", getResponse.Errors.Select(e => $"{e.Category} {e.Code}: {e.Detail}"));
            throw new InvalidOperationException($"Square rejected the get-order request for order {orderId}: {detail}");
        }

        var order = getResponse.Order
            ?? throw new InvalidOperationException($"Square get-order response for order {orderId} had no order and no errors.");

        if (order.State == OrderState.Completed)
        {
            // Already completed — a retried call (e.g. a crash between Square's update succeeding
            // and Payment.SquareOrderCompletedUtc being saved) or completed manually in the Square
            // dashboard already. Either way, nothing to do.
            return;
        }

        var updateResponse = await client.Orders.UpdateAsync(
            new UpdateOrderRequest
            {
                OrderId = orderId,
                Order = new Order { LocationId = order.LocationId, Version = order.Version, State = OrderState.Completed }
            },
            cancellationToken: cancellationToken);

        if (updateResponse.Errors is not null && updateResponse.Errors.Any())
        {
            var detail = string.Join("; ", updateResponse.Errors.Select(e => $"{e.Category} {e.Code}: {e.Detail}"));
            throw new InvalidOperationException($"Square rejected the complete-order request for order {orderId}: {detail}");
        }

        _logger.LogInformation("Marked Square order {SquareOrderId} Completed for team {TeamId}", orderId, credentials.TeamId);
    }

    /// <summary>
    /// Cached per team, and <b>rebuilt when the credentials it was built from change</b> (#252).
    ///
    /// <para>Keyed by TeamId alone, this cache never noticed a credential edit. The Worker is a
    /// long-lived process, so changing a team's Square environment or rotating its access token in
    /// Team Settings had no effect until someone restarted it — and CLAUDE.md's own documented
    /// post-deploy step is "set live teams back to Production in Team Settings", which therefore did
    /// nothing. The failure is silent in the worst direction: the cached client keeps talking to
    /// Sandbox, or keeps presenting a revoked token, and payment links just quietly stop working.</para>
    ///
    /// <para>Same shape as <c>ExamToolsClient.GetOrCreateTeamSession</c>, which already rebuilds when
    /// its BaseUrl changes. The comparison covers both fields the client is constructed from, because
    /// either one changing alone produces a client pointed at the wrong place.</para>
    /// </summary>
    /// <remarks>internal, not private, so a test can observe the identity of the returned client —
    /// "was it rebuilt?" is the entire behavior here and there is nothing else to assert on without
    /// a live Square account (issue #325's convention for the Worker job ticks).</remarks>
    internal global::Square.SquareClient GetOrCreateClient(SquareCredentials credentials)
    {
        // From the credentials, not deployment config: a token is issued for one environment and
        // fails against the other, so a single global switch made a real team on Production and a
        // test team on Sandbox mutually exclusive.
        var environment = credentials.Environment == SquareApiEnvironment.Production
            ? SquareEnvironment.Production
            : SquareEnvironment.Sandbox;

        var existing = _clientsByTeamId.GetOrAdd(
            credentials.TeamId, _ => CreateClient(credentials.AccessToken, environment));

        if (existing.AccessToken == credentials.AccessToken && existing.Environment == environment)
        {
            return existing.Client;
        }

        var replacement = CreateClient(credentials.AccessToken, environment);
        _clientsByTeamId[credentials.TeamId] = replacement;

        // Deliberately not logging which token or environment — the token is a secret and the pair
        // would narrow it. The team id is enough to explain a behavior change in the log.
        _logger.LogInformation(
            "Square credentials changed for team {TeamId} — rebuilt the cached client", credentials.TeamId);

        return replacement.Client;
    }

    private static CachedSquareClient CreateClient(string accessToken, string environment) =>
        new(new global::Square.SquareClient(accessToken, clientOptions: new ClientOptions { BaseUrl = environment }),
            accessToken,
            environment);

    /// <summary>
    /// The client plus what it was built from, so a change can be detected rather than assumed
    /// absent. Environment is the SDK's base-URL string (SquareEnvironment.Production/Sandbox are
    /// string constants), which is exactly what the client was constructed with.
    /// </summary>
    private sealed record CachedSquareClient(
        global::Square.SquareClient Client, string AccessToken, string Environment);
}
