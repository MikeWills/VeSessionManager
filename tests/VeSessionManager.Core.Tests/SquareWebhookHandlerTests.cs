using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
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

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static SquareWebhookHandler CreateHandler(AppDbContext dbContext) => new(
        dbContext,
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

    private static string PaymentUpdatedBody(string orderId, string status) => $$"""
        {
          "merchant_id": "TEST_MERCHANT",
          "type": "payment.updated",
          "event_id": "test-event",
          "created_at": "2026-07-20T12:00:00Z",
          "data": {
            "type": "payment",
            "id": "test-payment",
            "object": {
              "payment": {
                "id": "test-payment",
                "order_id": "{{orderId}}",
                "status": "{{status}}",
                "amount_money": { "amount": 1500, "currency": "USD" }
              }
            }
          }
        }
        """;

    private static async Task<Payment> SeedPaymentAsync(AppDbContext dbContext, Team team, string squareOrderId, PaymentStatus status = PaymentStatus.Unpaid)
    {
        var vec = new Vec { Name = "ARRL" };
        var user = new User { Name = "System", Email = "system@localhost", Role = UserRole.SystemAdmin };
        var feeConfiguration = new FeeConfiguration
        {
            Vec = vec, EffectiveDate = Now, FeeCollectionEnabled = true, ExamFeeAmount = 15m,
            CreatedByUser = user, CreatedUtc = Now
        };
        var session = new Session
        {
            ExamToolsSessionId = "session-1", Title = "July Session", ScheduledStartUtc = Now.AddDays(4),
            DurationMinutes = 60, Vec = vec, TeamId = team.Id, FeeConfiguration = feeConfiguration, CreatedUtc = Now
        };
        var candidate = new Candidate
        {
            ExamToolsApplicantId = "applicant-1", Session = session, Name = "Roana Glory",
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
    public async Task ValidSignature_UnmatchedOrderId_ReturnsIgnored_DoesNotThrow()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedPaymentAsync(dbContext, team, "order-123");
        var body = PaymentUpdatedBody("order-does-not-exist", "COMPLETED");
        var signature = ComputeValidSignature(body);

        var outcome = await CreateHandler(dbContext).ProcessAsync(team.Id, body, signature, CancellationToken.None);

        Assert.Equal(SquareWebhookOutcome.Ignored, outcome);
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
