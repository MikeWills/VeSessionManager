using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Square;
using Square.Checkout_;

namespace VeSessionManager.Core.Square;

/// <summary>
/// Wraps the official Square .NET SDK's Checkout API to create Order-based payment links (not
/// QuickPay — an Order is what lets us set ReferenceId, per
/// https://developer.squareup.com/reference/square/objects/Order). Registered as a singleton.
/// Credential validation and construction of the inner SDK client are both deferred to first
/// use (not the constructor): this type is resolved eagerly from inside a BackgroundService, and
/// a constructor throw there is a *host-stopping* failure (.NET's default
/// BackgroundServiceExceptionBehavior is StopHost) — it would take down ExamTools/Zoom/Discord
/// polling too, not just payment generation. A throw from CreatePaymentLinkAsync instead, is
/// just one more per-item failure PaymentGenerationService already catches and retries next poll.
/// </summary>
public sealed class SquareClient : ISquareClient
{
    private readonly SquareOptions _options;
    private readonly ILogger<SquareClient> _logger;
    private global::Square.SquareClient? _client;

    public SquareClient(IOptions<SquareOptions> options, ILogger<SquareClient> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.AccessToken);

    public async Task<SquarePaymentLink> CreatePaymentLinkAsync(SquarePaymentLinkRequest request, CancellationToken cancellationToken)
    {
        var client = GetOrCreateClient();

        if (string.IsNullOrWhiteSpace(_options.LocationId))
        {
            throw new InvalidOperationException("Square:LocationId is not configured.");
        }

        // Amounts are stored as decimal USD dollars on Payment; Square wants an integer count of
        // the currency's smallest unit (cents for USD).
        var amountCents = (long)Math.Round(request.AmountUsd * 100m, MidpointRounding.AwayFromZero);

        var response = await client.Checkout.PaymentLinks.CreateAsync(
            new CreatePaymentLinkRequest
            {
                IdempotencyKey = Guid.NewGuid().ToString(),
                Order = new Order
                {
                    LocationId = _options.LocationId,
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

        _logger.LogInformation("Created Square payment link for order {SquareOrderId}", paymentLink.OrderId);
        return new SquarePaymentLink
        {
            OrderId = paymentLink.OrderId ?? throw new InvalidOperationException("Square payment link response had no order_id."),
            Url = paymentLink.Url ?? throw new InvalidOperationException("Square payment link response had no url.")
        };
    }

    private global::Square.SquareClient GetOrCreateClient()
    {
        if (_client is not null)
        {
            return _client;
        }

        if (string.IsNullOrWhiteSpace(_options.AccessToken))
        {
            throw new InvalidOperationException(
                "Square access token is not configured. Set Square:AccessToken via user-secrets or environment variables.");
        }

        var environment = _options.Environment.Equals("Production", StringComparison.OrdinalIgnoreCase)
            ? SquareEnvironment.Production
            : SquareEnvironment.Sandbox;

        return _client = new global::Square.SquareClient(
            _options.AccessToken,
            clientOptions: new ClientOptions { BaseUrl = environment });
    }
}
