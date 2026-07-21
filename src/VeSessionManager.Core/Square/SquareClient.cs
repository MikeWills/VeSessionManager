using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Square;
using Square.Checkout_;

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
    private readonly SquareOptions _options;
    private readonly ILogger<SquareClient> _logger;
    private readonly ConcurrentDictionary<int, global::Square.SquareClient> _clientsByTeamId = new();

    public SquareClient(IOptions<SquareOptions> options, ILogger<SquareClient> logger)
    {
        _options = options.Value;
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
            OrderId = paymentLink.OrderId ?? throw new InvalidOperationException("Square payment link response had no order_id."),
            Url = paymentLink.Url ?? throw new InvalidOperationException("Square payment link response had no url.")
        };
    }

    private global::Square.SquareClient GetOrCreateClient(SquareCredentials credentials) =>
        _clientsByTeamId.GetOrAdd(credentials.TeamId, _ =>
        {
            var environment = _options.Environment.Equals("Production", StringComparison.OrdinalIgnoreCase)
                ? SquareEnvironment.Production
                : SquareEnvironment.Sandbox;

            return new global::Square.SquareClient(
                credentials.AccessToken,
                clientOptions: new ClientOptions { BaseUrl = environment });
        });
}
