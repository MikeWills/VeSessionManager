using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Payments;
using VeSessionManager.Core.Square;
using VeSessionManager.Core.Integrations;
using Xunit;

namespace VeSessionManager.Core.Tests;

public class YouthPaymentConfirmationServiceTests
{
    private static readonly DateTime Now = new(2026, 7, 27, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private sealed record CapturedCall(string ReferenceId, string ItemName, decimal AmountUsd, string IdempotencyKey);

    private sealed class FakeSquareClient : ISquareClient
    {
        public List<CapturedCall> CreateCalls { get; } = [];
        public List<string> DeletedPaymentLinkIds { get; } = [];
        public Exception? ThrowOnDelete { get; set; }

        /// <summary>Simulates the crash window this service's persist-once idempotency key exists for: Square has been called (the request is recorded) but the caller never gets to persist the result.</summary>
        public Exception? ThrowOnCreate { get; set; }
        private int _nextOrderId = 8000;

        public Task<SquarePaymentLink> CreatePaymentLinkAsync(SquareCredentials credentials, SquarePaymentLinkRequest request, CancellationToken cancellationToken)
        {
            CreateCalls.Add(new CapturedCall(request.ReferenceId, request.ItemName, request.AmountUsd, request.IdempotencyKey));
            if (ThrowOnCreate is not null)
            {
                throw ThrowOnCreate;
            }

            var orderId = $"order-{_nextOrderId++}";
            return Task.FromResult(new SquarePaymentLink { Id = $"link-{orderId}", OrderId = orderId, Url = $"https://square.link/u/{orderId}" });
        }

        public Task CompleteOrderAsync(SquareCredentials credentials, string orderId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeletePaymentLinkAsync(SquareCredentials credentials, string paymentLinkId, CancellationToken cancellationToken)
        {
            if (ThrowOnDelete is not null)
            {
                throw ThrowOnDelete;
            }

            DeletedPaymentLinkIds.Add(paymentLinkId);
            return Task.CompletedTask;
        }
    }

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static YouthPaymentConfirmationService CreateService(AppDbContext dbContext, ISquareClient square) =>
        new(dbContext, square, new FixedTimeProvider(Now), new TeamIntegrationState(NullLogger<TeamIntegrationState>.Instance), NullLogger<YouthPaymentConfirmationService>.Instance);

    /// <summary>Seeds a Team with Square configured, a youth-program Vec, a FeeConfiguration with a
    /// $5 youth rate (unless overridden), and an Unpaid InitialExam Payment with a standard $15
    /// link already generated and a YouthConfirmationToken set.</summary>
    private static async Task<(Team Team, Payment Payment, Guid Token)> SeedAsync(
        AppDbContext dbContext, bool squareConfigured = true, decimal? youthExamFeeAmount = 5m,
        PaymentStatus status = PaymentStatus.Unpaid, bool withExistingSquareLink = true,
        string? squareIdempotencyKey = null)
    {
        var team = new Team
        {
            Name = "TESTTEAM",
            SquareAccessToken = squareConfigured ? "square-token" : null,
            SquareLocationId = squareConfigured ? "square-location" : null,
            CreatedUtc = Now
        };
        var vec = new Vec { Name = "ARRL", SupportsYouthProgram = true };
        var user = new User { Name = "System", Email = "system@localhost", Role = UserRole.SystemAdmin };
        var feeConfiguration = new FeeConfiguration
        {
            Vec = vec,
            EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            FeeCollectionEnabled = true,
            ExamFeeAmount = 15m,
            YouthExamFeeAmount = youthExamFeeAmount,
            CreatedByUser = user,
            CreatedUtc = Now
        };
        var session = new Session
        {
            ExamToolsSessionId = "session-1",
            Title = "July Session",
            ScheduledStartUtc = Now.AddDays(4),
            DurationMinutes = 60,
            Vec = vec,
            Team = team,
            FeeConfiguration = feeConfiguration,
            Status = SessionStatus.Active,
            CreatedUtc = Now
        };
        var candidate = new Candidate
        {
            ExamToolsApplicantId = "applicant-1",
            Session = session,
            Name = "Roana Glory",
            Email = "roana@example.com",
            DateRegisteredUtc = Now
        };
        var token = Guid.NewGuid();
        var payment = new Payment
        {
            Candidate = candidate,
            Reason = PaymentReason.InitialExam,
            Amount = 15m,
            Status = status,
            PaymentLinkUrl = withExistingSquareLink ? "https://square.link/u/order-old" : null,
            SquarePaymentReferenceId = withExistingSquareLink ? "order-old" : null,
            SquarePaymentLinkId = withExistingSquareLink ? "link-old" : null,
            SquareIdempotencyKey = squareIdempotencyKey,
            YouthConfirmationToken = token,
            CreatedUtc = Now
        };
        dbContext.Payments.Add(payment);
        await dbContext.SaveChangesAsync();
        return (team, payment, token);
    }

    [Fact]
    public async Task ConfirmAsync_HappyPath_DeletesOldLink_CreatesNewYouthLink_UpdatesPayment_WritesAudit()
    {
        await using var dbContext = CreateContext();
        var (team, payment, token) = await SeedAsync(dbContext);
        var square = new FakeSquareClient();

        var result = await CreateService(dbContext, square).ConfirmAsync(token, CancellationToken.None);

        Assert.Equal(YouthConfirmationOutcome.Success, result.Outcome);
        Assert.NotNull(result.RedirectUrl);

        Assert.Equal(new[] { "link-old" }, square.DeletedPaymentLinkIds);
        var call = Assert.Single(square.CreateCalls);
        Assert.Equal(5m, call.AmountUsd);
        Assert.Equal(payment.Id.ToString(), call.ReferenceId);
        Assert.Contains("Youth Rate", call.ItemName);

        var updated = await dbContext.Payments.SingleAsync();
        Assert.Equal(5m, updated.Amount);
        Assert.NotEqual("https://square.link/u/order-old", updated.PaymentLinkUrl);
        Assert.NotEqual("order-old", updated.SquarePaymentReferenceId);
        Assert.NotEqual("link-old", updated.SquarePaymentLinkId);
        Assert.Equal(result.RedirectUrl, updated.PaymentLinkUrl);

        var audit = await dbContext.AuditLogs.SingleAsync();
        Assert.Equal("YouthRateConfirmed", audit.Action);
        Assert.Null(audit.UserId);
        Assert.Equal(nameof(Payment), audit.EntityType);
        Assert.Equal(payment.Id, audit.EntityId);
    }

    [Fact]
    public async Task ConfirmAsync_UnknownToken_ReturnsNotFound_NoSquareCalls()
    {
        await using var dbContext = CreateContext();
        await SeedAsync(dbContext);
        var square = new FakeSquareClient();

        var result = await CreateService(dbContext, square).ConfirmAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(YouthConfirmationOutcome.NotFound, result.Outcome);
        Assert.Empty(square.CreateCalls);
        Assert.Empty(square.DeletedPaymentLinkIds);
    }

    [Fact]
    public async Task ConfirmAsync_AlreadyPaid_ReturnsAlreadyResolved_NoSquareCalls_NoAmountChange()
    {
        await using var dbContext = CreateContext();
        var (_, payment, token) = await SeedAsync(dbContext, status: PaymentStatus.Paid);
        var square = new FakeSquareClient();

        var result = await CreateService(dbContext, square).ConfirmAsync(token, CancellationToken.None);

        Assert.Equal(YouthConfirmationOutcome.AlreadyResolved, result.Outcome);
        Assert.Empty(square.CreateCalls);
        Assert.Empty(square.DeletedPaymentLinkIds);
        var unchanged = await dbContext.Payments.SingleAsync();
        Assert.Equal(15m, unchanged.Amount);
    }

    [Fact]
    public async Task ConfirmAsync_FeeNotConfigured_ReturnsFeeNotConfigured_NoSquareCalls()
    {
        await using var dbContext = CreateContext();
        var (_, _, token) = await SeedAsync(dbContext, youthExamFeeAmount: null);
        var square = new FakeSquareClient();

        var result = await CreateService(dbContext, square).ConfirmAsync(token, CancellationToken.None);

        Assert.Equal(YouthConfirmationOutcome.FeeNotConfigured, result.Outcome);
        Assert.Empty(square.CreateCalls);
    }

    [Fact]
    public async Task ConfirmAsync_SquareNotConfigured_ReturnsSquareNotConfigured()
    {
        await using var dbContext = CreateContext();
        var (_, _, token) = await SeedAsync(dbContext, squareConfigured: false);
        var square = new FakeSquareClient();

        var result = await CreateService(dbContext, square).ConfirmAsync(token, CancellationToken.None);

        Assert.Equal(YouthConfirmationOutcome.SquareNotConfigured, result.Outcome);
        Assert.Empty(square.CreateCalls);
    }

    [Fact]
    public async Task ConfirmAsync_DeleteOldLinkFails_StillGeneratesNewLink()
    {
        await using var dbContext = CreateContext();
        var (_, payment, token) = await SeedAsync(dbContext);
        var square = new FakeSquareClient { ThrowOnDelete = new InvalidOperationException("Square delete failed") };

        var result = await CreateService(dbContext, square).ConfirmAsync(token, CancellationToken.None);

        Assert.Equal(YouthConfirmationOutcome.Success, result.Outcome);
        Assert.Single(square.CreateCalls);
        var updated = await dbContext.Payments.SingleAsync();
        Assert.Equal(5m, updated.Amount);
    }

    [Fact]
    public async Task ConfirmAsync_NoExistingSquareLink_SkipsDelete_StillGeneratesNewLink()
    {
        await using var dbContext = CreateContext();
        var (_, _, token) = await SeedAsync(dbContext, withExistingSquareLink: false);
        var square = new FakeSquareClient();

        var result = await CreateService(dbContext, square).ConfirmAsync(token, CancellationToken.None);

        Assert.Equal(YouthConfirmationOutcome.Success, result.Outcome);
        Assert.Empty(square.DeletedPaymentLinkIds);
        Assert.Single(square.CreateCalls);
    }

    [Fact]
    public async Task CheckEligibilityAsync_MirrorsConfirmAsyncGuards_WithoutMutatingOrCallingSquare()
    {
        await using var dbContext = CreateContext();
        var (_, _, token) = await SeedAsync(dbContext);
        var square = new FakeSquareClient();

        var outcome = await CreateService(dbContext, square).CheckEligibilityAsync(token, CancellationToken.None);

        Assert.Equal(YouthConfirmationOutcome.Success, outcome.Outcome);
        Assert.Empty(square.CreateCalls);
        Assert.Empty(square.DeletedPaymentLinkIds);
        var unchanged = await dbContext.Payments.SingleAsync();
        Assert.Equal(15m, unchanged.Amount);
    }

    // ---- idempotency key: persist-once, reused on retry (T07, 2026-08-03) ----

    [Fact]
    public async Task ConfirmAsync_WithAnExistingStandardRateLink_SendsAKeySquareHasNotSeen()
    {
        // The standard-rate link is deleted here, so its key must go with it — replaying that key
        // would make Square hand back the standard-rate link at the standard-rate price.
        await using var dbContext = CreateContext();
        var (_, _, token) = await SeedAsync(dbContext, squareIdempotencyKey: "standard-rate-key");
        var square = new FakeSquareClient();

        await CreateService(dbContext, square).ConfirmAsync(token, CancellationToken.None);

        Assert.Equal(["link-old"], square.DeletedPaymentLinkIds);
        var call = Assert.Single(square.CreateCalls);
        Assert.NotEqual("standard-rate-key", call.IdempotencyKey);
        Assert.False(string.IsNullOrWhiteSpace(call.IdempotencyKey));
    }

    [Fact]
    public async Task ConfirmAsync_PersistsTheIdempotencyKeyItSendsToSquare()
    {
        await using var dbContext = CreateContext();
        var (_, _, token) = await SeedAsync(dbContext);
        var square = new FakeSquareClient();

        await CreateService(dbContext, square).ConfirmAsync(token, CancellationToken.None);

        var call = Assert.Single(square.CreateCalls);
        Assert.Equal(call.IdempotencyKey, (await dbContext.Payments.SingleAsync()).SquareIdempotencyKey);
    }

    [Fact]
    public async Task ConfirmAsync_AfterAnInterruptedAttempt_ReusesThePersistedKeyRatherThanMintingANewOne()
    {
        // The regression that matters. Until 2026-08-03 this method assigned a fresh Guid on every
        // call, so a crash between Square accepting CreatePaymentLink and the save at the end left
        // the Payment Unpaid — the candidate could confirm again, send a *different* key, and get a
        // second live Square order with the first orphaned and still payable.
        await using var dbContext = CreateContext();
        var (_, _, token) = await SeedAsync(dbContext);
        var crashingSquare = new FakeSquareClient { ThrowOnCreate = new HttpRequestException("connection reset after Square accepted the request") };
        var service = CreateService(dbContext, crashingSquare);

        await Assert.ThrowsAsync<HttpRequestException>(() => service.ConfirmAsync(token, CancellationToken.None));

        var keySentBeforeTheCrash = Assert.Single(crashingSquare.CreateCalls).IdempotencyKey;
        var persistedKey = (await dbContext.Payments.SingleAsync()).SquareIdempotencyKey;
        Assert.Equal(keySentBeforeTheCrash, persistedKey);

        // Act — the candidate clicks confirm again after the crash.
        var retrySquare = new FakeSquareClient();
        var result = await CreateService(dbContext, retrySquare).ConfirmAsync(token, CancellationToken.None);

        // Assert — Square sees the same key, so it replays the one order instead of creating a second.
        Assert.Equal(YouthConfirmationOutcome.Success, result.Outcome);
        Assert.Equal(keySentBeforeTheCrash, Assert.Single(retrySquare.CreateCalls).IdempotencyKey);
    }

    [Fact]
    public async Task ConfirmAsync_WithAKeyPersistedButNoLiveLink_ReusesThatKey()
    {
        // The stored state an interrupted attempt leaves behind, seeded directly: no
        // SquarePaymentLinkId (so nothing to delete and nothing to clear the key), key already set.
        await using var dbContext = CreateContext();
        var (_, _, token) = await SeedAsync(dbContext, withExistingSquareLink: false, squareIdempotencyKey: "interrupted-attempt-key");
        var square = new FakeSquareClient();

        await CreateService(dbContext, square).ConfirmAsync(token, CancellationToken.None);

        Assert.Empty(square.DeletedPaymentLinkIds);
        Assert.Equal("interrupted-attempt-key", Assert.Single(square.CreateCalls).IdempotencyKey);
    }

    [Fact]
    public async Task ConfirmAsync_InterruptedAfterTheOldLinkWasDeleted_DoesNotDeleteTwice()
    {
        // Clearing SquarePaymentLinkId alongside the key is what makes the retry skip the delete —
        // the standard-rate link is already gone.
        await using var dbContext = CreateContext();
        var (_, _, token) = await SeedAsync(dbContext);
        var crashingSquare = new FakeSquareClient { ThrowOnCreate = new HttpRequestException("boom") };

        await Assert.ThrowsAsync<HttpRequestException>(
            () => CreateService(dbContext, crashingSquare).ConfirmAsync(token, CancellationToken.None));

        var retrySquare = new FakeSquareClient();
        await CreateService(dbContext, retrySquare).ConfirmAsync(token, CancellationToken.None);

        Assert.Equal(["link-old"], crashingSquare.DeletedPaymentLinkIds);
        Assert.Empty(retrySquare.DeletedPaymentLinkIds);
    }
}
