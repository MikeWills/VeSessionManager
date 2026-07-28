using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Payments;
using VeSessionManager.Core.Square;
using Xunit;

namespace VeSessionManager.Core.Tests;

public class SquareWebhookHandlerTests
{
    private static readonly DateTime Now = new(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);
    private const string SignatureKey = "test-signature-key";
    private const string NotificationUrl = "https://vesessionmanager.example/webhooks/square/1";

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private sealed class FakeSquareClient : ISquareClient
    {
        public Task<SquarePaymentLink> CreatePaymentLinkAsync(SquareCredentials credentials, SquarePaymentLinkRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by SquareWebhookHandlerTests.");

        public Task CompleteOrderAsync(SquareCredentials credentials, string orderId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeletePaymentLinkAsync(SquareCredentials credentials, string paymentLinkId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by SquareWebhookHandlerTests.");
    }

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static SquareWebhookHandler CreateHandler(AppDbContext dbContext) => new(
        dbContext,
        new SquarePaymentMatchingService(dbContext, new FakeSquareClient(), new FixedTimeProvider(Now), NullLogger<SquarePaymentMatchingService>.Instance),
        new FixedTimeProvider(Now),
        NullLogger<SquareWebhookHandler>.Instance);

    /// <summary>Seeds a Team with the given webhook signature key/url (both null if signatureConfigured is false).</summary>
    private static async Task<Team> SeedTeamAsync(AppDbContext dbContext, bool signatureConfigured = true, string? notificationUrl = NotificationUrl)
    {
        var team = new Team
        {
            Name = "TESTTEAM",
            SquareWebhookSignatureKey = signatureConfigured ? SignatureKey : null,
            SquareWebhookNotificationUrl = signatureConfigured ? notificationUrl : null,
            CreatedUtc = Now
        };
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        return team;
    }

    /// <summary>Square's own documented algorithm: HMAC-SHA256(signatureKey, notificationUrl + requestBody), base64-encoded.</summary>
    private static string ComputeValidSignature(string body, string notificationUrl = NotificationUrl) =>
        Convert.ToBase64String(new HMACSHA256(Encoding.UTF8.GetBytes(SignatureKey))
            .ComputeHash(Encoding.UTF8.GetBytes(notificationUrl + body)));

    private static string PaymentUpdatedBody(string orderId, string status, string? buyerEmailAddress = null, string paymentId = "test-payment", long amountCents = 1500)
    {
        var buyerEmailField = buyerEmailAddress is null ? "" : $""", "buyer_email_address": "{buyerEmailAddress}" """;
        return $$"""
            {
              "merchant_id": "TEST_MERCHANT",
              "type": "payment.updated",
              "event_id": "test-event",
              "created_at": "2026-07-20T12:00:00Z",
              "data": {
                "type": "payment",
                "id": "{{paymentId}}",
                "object": {
                  "payment": {
                    "id": "{{paymentId}}",
                    "order_id": "{{orderId}}",
                    "status": "{{status}}",
                    "amount_money": { "amount": {{amountCents}}, "currency": "USD" }{{buyerEmailField}}
                  }
                }
              }
            }
            """;
    }

    private static async Task<Payment> SeedPaymentAsync(AppDbContext dbContext, Team team, string? squareOrderId, PaymentStatus status = PaymentStatus.Unpaid, bool sessionTestingCompleted = false)
    {
        var vec = new Vec { Name = "ARRL" };
        var user = new User { Name = "System", Email = "system@localhost", Role = UserRole.SystemAdmin };
        var feeConfiguration = new FeeConfiguration
        {
            Vec = vec, EffectiveDate = Now, FeeCollectionEnabled = true, ExamFeeAmount = 15m,
            CreatedByUser = user, CreatedUtc = Now
        };
        // Unique per call (SquareWebhookHandlerTests' unmatched-order tests seed more than one
        // Payment/Session in the same test) — ExamToolsSessionId has a unique index.
        var uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
        var session = new Session
        {
            ExamToolsSessionId = $"session-{uniqueSuffix}", Title = "July Session", ScheduledStartUtc = Now.AddDays(4),
            DurationMinutes = 60, Vec = vec, TeamId = team.Id, FeeConfiguration = feeConfiguration, CreatedUtc = Now,
            TestingCompletedUtc = sessionTestingCompleted ? Now.AddHours(-1) : null
        };
        var candidate = new Candidate
        {
            ExamToolsApplicantId = $"applicant-{uniqueSuffix}", Session = session, Name = "Roana Glory",
            Email = "roana@example.com", DateRegisteredUtc = Now
        };
        var payment = new Payment
        {
            Candidate = candidate, Reason = PaymentReason.InitialExam, Amount = 15m,
            Status = status, SquarePaymentReferenceId = squareOrderId, PaymentLinkUrl = "https://square.link/u/x",
            CreatedUtc = Now
        };
        dbContext.Payments.Add(payment);
        await dbContext.SaveChangesAsync();
        return payment;
    }

    [Fact]
    public async Task ValidSignature_CompletedStatus_MatchingOrderId_MarksPaymentPaid()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var payment = await SeedPaymentAsync(dbContext, team, "order-123");
        var body = PaymentUpdatedBody("order-123", "COMPLETED");
        var signature = ComputeValidSignature(body);

        var outcome = await CreateHandler(dbContext).ProcessAsync(team.Id, body, signature, CancellationToken.None);

        Assert.Equal(SquareWebhookOutcome.Processed, outcome);
        var updated = dbContext.Payments.Single(p => p.Id == payment.Id);
        Assert.Equal(PaymentStatus.Paid, updated.Status);
        Assert.Equal(Now, updated.PaidDateUtc);
    }

    [Fact]
    public async Task ValidSignature_MatchingOrderId_SessionAlreadyCompleted_AlsoCompletesSquareOrder()
    {
        // The session being marked completed before this payment's webhook arrives is the other
        // half of SquarePaymentMatchingService.CompleteOrderIfEligibleAsync's "either side can
        // happen second" pairing — SessionActionServiceTests covers the reverse ordering.
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        team.SquareAccessToken = "sandbox-token";
        await dbContext.SaveChangesAsync();
        var payment = await SeedPaymentAsync(dbContext, team, "order-123", sessionTestingCompleted: true);
        var body = PaymentUpdatedBody("order-123", "COMPLETED");
        var signature = ComputeValidSignature(body);

        var outcome = await CreateHandler(dbContext).ProcessAsync(team.Id, body, signature, CancellationToken.None);

        Assert.Equal(SquareWebhookOutcome.Processed, outcome);
        var updated = dbContext.Payments.Single(p => p.Id == payment.Id);
        Assert.Equal(PaymentStatus.Paid, updated.Status);
        Assert.Equal(Now, updated.SquareOrderCompletedUtc);
    }

    [Fact]
    public async Task InvalidSignature_ReturnsInvalidSignature_AndDoesNotTouchPayment()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var payment = await SeedPaymentAsync(dbContext, team, "order-123");
        var body = PaymentUpdatedBody("order-123", "COMPLETED");

        var outcome = await CreateHandler(dbContext).ProcessAsync(team.Id, body, "not-a-real-signature", CancellationToken.None);

        Assert.Equal(SquareWebhookOutcome.InvalidSignature, outcome);
        Assert.Equal(PaymentStatus.Unpaid, dbContext.Payments.Single(p => p.Id == payment.Id).Status);
    }

    [Fact]
    public async Task MissingSignatureHeader_ReturnsInvalidSignature()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedPaymentAsync(dbContext, team, "order-123");
        var body = PaymentUpdatedBody("order-123", "COMPLETED");

        var outcome = await CreateHandler(dbContext).ProcessAsync(team.Id, body, signatureHeader: null, CancellationToken.None);

        Assert.Equal(SquareWebhookOutcome.InvalidSignature, outcome);
    }

    [Fact]
    public async Task ValidSignature_UnmatchedOrderId_NoBuyerEmail_RecordsForManualReview_ReturnsProcessed()
    {
        // Behavior changed from the original Phase 3 cut: an order this app didn't create used to
        // just be logged and dropped. Now it's persisted as an UnmatchedSquarePayment for a Session
        // Manager to resolve — see SquarePaymentMatchingService.
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedPaymentAsync(dbContext, team, "order-123");
        var body = PaymentUpdatedBody("order-does-not-exist", "COMPLETED", paymentId: "square-payment-1");
        var signature = ComputeValidSignature(body);

        var outcome = await CreateHandler(dbContext).ProcessAsync(team.Id, body, signature, CancellationToken.None);

        Assert.Equal(SquareWebhookOutcome.Processed, outcome);
        var recorded = Assert.Single(dbContext.UnmatchedSquarePayments);
        Assert.Equal(team.Id, recorded.TeamId);
        Assert.Equal("order-does-not-exist", recorded.SquareOrderId);
        Assert.Equal("square-payment-1", recorded.SquarePaymentId);
        Assert.Equal(15m, recorded.AmountUsd);
        Assert.Null(recorded.BuyerEmailAddress);
        Assert.Null(recorded.ResolvedUtc);
    }

    [Fact]
    public async Task ValidSignature_UnmatchedOrderId_BuyerEmailMatchesExactlyOneCandidateWithUnpaidPayment_AutoMatches()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var unpaidPayment = await SeedPaymentAsync(dbContext, team, squareOrderId: null, status: PaymentStatus.Unpaid);
        unpaidPayment.Candidate.Email = "roana@example.com";
        await dbContext.SaveChangesAsync();
        var body = PaymentUpdatedBody("order-from-online-page", "COMPLETED", buyerEmailAddress: "roana@example.com");
        var signature = ComputeValidSignature(body);

        var outcome = await CreateHandler(dbContext).ProcessAsync(team.Id, body, signature, CancellationToken.None);

        Assert.Equal(SquareWebhookOutcome.Processed, outcome);
        Assert.Empty(dbContext.UnmatchedSquarePayments);
        var updated = dbContext.Payments.Single();
        Assert.Equal(PaymentStatus.Paid, updated.Status);
        Assert.Equal("order-from-online-page", updated.SquarePaymentReferenceId);
        Assert.Equal(Now, updated.PaidDateUtc);
    }

    [Fact]
    public async Task ValidSignature_UnmatchedOrderId_BuyerEmailMatches_ButAmountPaidLessThanOwed_StillAutoMatches_FlagsMismatch()
    {
        // This team's separate Square-hosted checkout page only offers $5 (ARRL youth) or $15
        // (standard) — a $5 payment against a Payment created at the $15 rate (youth status isn't
        // confirmed until test day) is a routine, legitimate outcome, not something to withhold
        // Paid status over. It should still auto-match, but flag the mismatch for review.
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var unpaidPayment = await SeedPaymentAsync(dbContext, team, squareOrderId: null, status: PaymentStatus.Unpaid);
        unpaidPayment.Candidate.Email = "roana@example.com";
        await dbContext.SaveChangesAsync();
        var body = PaymentUpdatedBody("order-youth-rate", "COMPLETED", buyerEmailAddress: "roana@example.com", amountCents: 500);
        var signature = ComputeValidSignature(body);

        var outcome = await CreateHandler(dbContext).ProcessAsync(team.Id, body, signature, CancellationToken.None);

        Assert.Equal(SquareWebhookOutcome.Processed, outcome);
        Assert.Empty(dbContext.UnmatchedSquarePayments);
        var updated = dbContext.Payments.Single();
        Assert.Equal(PaymentStatus.Paid, updated.Status);
        Assert.Equal(5m, updated.SquareAmountPaidUsd);
        Assert.Equal(15m, updated.Amount);
        Assert.Equal(Now, updated.AmountMismatchFlaggedUtc);
    }

    [Fact]
    public async Task ValidSignature_UnmatchedOrderId_BuyerEmailMatchesMultipleCandidates_RecordsForManualReview()
    {
        // Don't guess when it's ambiguous (e.g. a shared family email) — fall through to manual review.
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var firstPayment = await SeedPaymentAsync(dbContext, team, squareOrderId: null, status: PaymentStatus.Unpaid);
        firstPayment.Candidate.Email = "family@example.com";
        var secondPayment = await SeedPaymentAsync(dbContext, team, squareOrderId: null, status: PaymentStatus.Unpaid);
        secondPayment.Candidate.Email = "family@example.com";
        await dbContext.SaveChangesAsync();
        var body = PaymentUpdatedBody("order-ambiguous", "COMPLETED", buyerEmailAddress: "family@example.com");
        var signature = ComputeValidSignature(body);

        var outcome = await CreateHandler(dbContext).ProcessAsync(team.Id, body, signature, CancellationToken.None);

        Assert.Equal(SquareWebhookOutcome.Processed, outcome);
        var recorded = Assert.Single(dbContext.UnmatchedSquarePayments);
        Assert.Equal("family@example.com", recorded.BuyerEmailAddress);
        Assert.All(dbContext.Payments, p => Assert.Equal(PaymentStatus.Unpaid, p.Status));
    }

    [Fact]
    public async Task ValidSignature_UnmatchedOrderId_AlreadyRecorded_DoesNotDuplicate_ReturnsIgnored()
    {
        // A redelivery for an order still awaiting manual review must not create a second row.
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var body = PaymentUpdatedBody("order-does-not-exist", "COMPLETED");
        var signature = ComputeValidSignature(body);

        var firstOutcome = await CreateHandler(dbContext).ProcessAsync(team.Id, body, signature, CancellationToken.None);
        var secondOutcome = await CreateHandler(dbContext).ProcessAsync(team.Id, body, signature, CancellationToken.None);

        Assert.Equal(SquareWebhookOutcome.Processed, firstOutcome);
        Assert.Equal(SquareWebhookOutcome.Ignored, secondOutcome);
        Assert.Single(dbContext.UnmatchedSquarePayments);
    }

    [Fact]
    public async Task ValidSignature_NonCompletedStatus_ReturnsIgnored_AndDoesNotTouchPayment()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var payment = await SeedPaymentAsync(dbContext, team, "order-123");
        var body = PaymentUpdatedBody("order-123", "FAILED");
        var signature = ComputeValidSignature(body);

        var outcome = await CreateHandler(dbContext).ProcessAsync(team.Id, body, signature, CancellationToken.None);

        Assert.Equal(SquareWebhookOutcome.Ignored, outcome);
        Assert.Equal(PaymentStatus.Unpaid, dbContext.Payments.Single(p => p.Id == payment.Id).Status);
    }

    [Fact]
    public async Task ValidSignature_AlreadyPaidPayment_IsIgnored_Idempotently()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var payment = await SeedPaymentAsync(dbContext, team, "order-123", PaymentStatus.Paid);
        payment.PaidDateUtc = Now.AddDays(-1);
        await dbContext.SaveChangesAsync();
        var body = PaymentUpdatedBody("order-123", "COMPLETED");
        var signature = ComputeValidSignature(body);

        var outcome = await CreateHandler(dbContext).ProcessAsync(team.Id, body, signature, CancellationToken.None);

        Assert.Equal(SquareWebhookOutcome.Ignored, outcome);
        Assert.Equal(Now.AddDays(-1), dbContext.Payments.Single(p => p.Id == payment.Id).PaidDateUtc);
    }

    [Fact]
    public async Task ValidSignature_WrongEventType_ReturnsIgnored()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedPaymentAsync(dbContext, team, "order-123");
        const string body = """{"type":"payment.created","data":{"object":{}}}""";
        var signature = ComputeValidSignature(body);

        var outcome = await CreateHandler(dbContext).ProcessAsync(team.Id, body, signature, CancellationToken.None);

        Assert.Equal(SquareWebhookOutcome.Ignored, outcome);
    }

    [Fact]
    public async Task TeamNotFound_ReturnsInvalidSignature()
    {
        await using var dbContext = CreateContext();
        var body = PaymentUpdatedBody("order-123", "COMPLETED");
        var signature = ComputeValidSignature(body);

        var outcome = await CreateHandler(dbContext).ProcessAsync(999, body, signature, CancellationToken.None);

        Assert.Equal(SquareWebhookOutcome.InvalidSignature, outcome);
    }

    [Fact]
    public async Task TeamWebhookNotConfigured_ReturnsInvalidSignature()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, signatureConfigured: false);
        var body = PaymentUpdatedBody("order-123", "COMPLETED");

        var outcome = await CreateHandler(dbContext).ProcessAsync(team.Id, body, "any-signature", CancellationToken.None);

        Assert.Equal(SquareWebhookOutcome.InvalidSignature, outcome);
    }

    [Fact]
    public async Task ValidSignature_PaymentBelongsToDifferentTeam_ReturnsIgnored_AndDoesNotTouchPayment()
    {
        // A genuinely valid signature for teamB's own key, but the matched order_id belongs to
        // teamA's payment — almost certainly a misconfigured WebhookNotificationUrl, not an
        // attack. Must not mark the wrong team's payment paid.
        await using var dbContext = CreateContext();
        var teamA = await SeedTeamAsync(dbContext, notificationUrl: "https://vesessionmanager.example/webhooks/square/1");
        var teamB = await SeedTeamAsync(dbContext, notificationUrl: "https://vesessionmanager.example/webhooks/square/2");
        var payment = await SeedPaymentAsync(dbContext, teamA, "order-123");
        var body = PaymentUpdatedBody("order-123", "COMPLETED");
        var signature = ComputeValidSignature(body, teamB.SquareWebhookNotificationUrl!);

        var outcome = await CreateHandler(dbContext).ProcessAsync(teamB.Id, body, signature, CancellationToken.None);

        Assert.Equal(SquareWebhookOutcome.Ignored, outcome);
        Assert.Equal(PaymentStatus.Unpaid, dbContext.Payments.Single(p => p.Id == payment.Id).Status);
    }
}
