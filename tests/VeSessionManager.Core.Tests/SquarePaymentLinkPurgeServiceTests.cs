using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Payments;
using VeSessionManager.Core.Square;
using VeSessionManager.Core.Integrations;
using Xunit;

namespace VeSessionManager.Core.Tests;

public class SquarePaymentLinkPurgeServiceTests
{
    private static readonly DateTime Now = new(2026, 7, 28, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private sealed class FakeSquareClient : ISquareClient
    {
        // #375 added these to ISquareClient. Not exercised here — throwing rather than returning a
        // stub keeps that true: if this test ever starts refunding, it says so instead of passing
        // against a fake that quietly agrees.
        public Task<SquareRefund> RefundPaymentAsync(SquareCredentials credentials, SquareRefundRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Refunds are not exercised by this test.");

        public Task<SquareRefund> GetRefundAsync(SquareCredentials credentials, string squareRefundId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Refunds are not exercised by this test.");

        public List<string> DeletedPaymentLinkIds { get; } = [];
        public Exception? ThrowOnDelete { get; set; }

        public Task<SquarePaymentLink> CreatePaymentLinkAsync(SquareCredentials credentials, SquarePaymentLinkRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by SquarePaymentLinkPurgeServiceTests.");

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

    private static SquarePaymentLinkPurgeService CreateService(AppDbContext dbContext, ISquareClient square) =>
        new(dbContext, square, new FixedTimeProvider(Now), new TeamIntegrationState(NullLogger<TeamIntegrationState>.Instance), NullLogger<SquarePaymentLinkPurgeService>.Instance);

    /// <summary>Seeds a Team. squareConfigured=true (default) sets AccessToken so Team.IsSquareConfigured is true. purgeDays defaults to 30 (the entity's own default).</summary>
    private static async Task<Team> SeedTeamAsync(AppDbContext dbContext, bool squareConfigured = true, int purgeDays = 30)
    {
        var team = new Team
        {
            Name = "TESTTEAM",
            SquareAccessToken = squareConfigured ? "square-token" : null,
            SquareLocationId = squareConfigured ? "square-location" : null,
            PurgeUnpaidLinkDays = purgeDays,
            CreatedUtc = Now
        };
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        return team;
    }

    /// <summary>Seeds Vec/User/FeeConfiguration/Session/Candidate/Payment. createdDaysAgo controls
    /// how old the Payment is (against Now) — the query's staleness threshold check.</summary>
    private static async Task<Payment> SeedPaymentAsync(
        AppDbContext dbContext, Team team, int createdDaysAgo, PaymentStatus status = PaymentStatus.Unpaid,
        string? paymentLinkUrl = "https://square.link/u/order-old", string? squarePaymentLinkId = "link-old",
        DateTime? squareLinkPurgedUtc = null)
    {
        var vec = new Vec { Name = "ARRL" };
        var user = new User { Name = "System", Email = "system@localhost", Role = UserRole.SystemAdmin };
        var feeConfiguration = new FeeConfiguration
        {
            Vec = vec,
            EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            FeeCollectionEnabled = true,
            ExamFeeAmount = 15m,
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
        var payment = new Payment
        {
            Candidate = candidate,
            Reason = PaymentReason.InitialExam,
            Amount = 15m,
            Status = status,
            PaymentLinkUrl = paymentLinkUrl,
            SquarePaymentReferenceId = paymentLinkUrl is not null ? "order-old" : null,
            SquarePaymentLinkId = squarePaymentLinkId,
            SquareLinkPurgedUtc = squareLinkPurgedUtc,
            CreatedUtc = Now.AddDays(-createdDaysAgo)
        };
        dbContext.Payments.Add(payment);
        await dbContext.SaveChangesAsync();
        return payment;
    }

    [Fact]
    public async Task StaleUnpaidPayment_LinkDeleted_FieldsCleared_PurgedUtcSet()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var payment = await SeedPaymentAsync(dbContext, team, createdDaysAgo: 31);
        var square = new FakeSquareClient();

        var result = await CreateService(dbContext, square).RunAsync(team, CancellationToken.None);

        Assert.Equal(1, result.Purged);
        Assert.Equal(0, result.Failed);
        Assert.Equal(new[] { "link-old" }, square.DeletedPaymentLinkIds);
        var updated = await dbContext.Payments.SingleAsync(p => p.Id == payment.Id);
        Assert.Null(updated.PaymentLinkUrl);
        Assert.Null(updated.SquarePaymentReferenceId);
        Assert.Null(updated.SquarePaymentLinkId);
        Assert.Equal(Now, updated.SquareLinkPurgedUtc);
    }

    [Fact]
    public async Task PaymentYoungerThanThreshold_IsUntouched()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, purgeDays: 30);
        var payment = await SeedPaymentAsync(dbContext, team, createdDaysAgo: 29);
        var square = new FakeSquareClient();

        var result = await CreateService(dbContext, square).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.Purged);
        Assert.Empty(square.DeletedPaymentLinkIds);
        var unchanged = await dbContext.Payments.SingleAsync(p => p.Id == payment.Id);
        Assert.NotNull(unchanged.PaymentLinkUrl);
    }

    [Fact]
    public async Task PerTeamThreshold_IsRespected()
    {
        await using var dbContext = CreateContext();
        var strictTeam = await SeedTeamAsync(dbContext, purgeDays: 10);
        var lenientTeam = await SeedTeamAsync(dbContext, purgeDays: 60);
        var strictPayment = await SeedPaymentAsync(dbContext, strictTeam, createdDaysAgo: 15);
        var lenientPayment = await SeedPaymentAsync(dbContext, lenientTeam, createdDaysAgo: 15);
        var square = new FakeSquareClient();

        var strictResult = await CreateService(dbContext, square).RunAsync(strictTeam, CancellationToken.None);
        var lenientResult = await CreateService(dbContext, square).RunAsync(lenientTeam, CancellationToken.None);

        Assert.Equal(1, strictResult.Purged);
        Assert.Equal(0, lenientResult.Purged);
        Assert.NotNull((await dbContext.Payments.SingleAsync(p => p.Id == lenientPayment.Id)).PaymentLinkUrl);
        Assert.Null((await dbContext.Payments.SingleAsync(p => p.Id == strictPayment.Id)).PaymentLinkUrl);
    }

    [Fact]
    public async Task DeleteFails_PaymentLeftUntouchedForRetry()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var payment = await SeedPaymentAsync(dbContext, team, createdDaysAgo: 31);
        var square = new FakeSquareClient { ThrowOnDelete = new InvalidOperationException("Square unavailable") };

        var result = await CreateService(dbContext, square).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.Purged);
        Assert.Equal(1, result.Failed);
        var unchanged = await dbContext.Payments.SingleAsync(p => p.Id == payment.Id);
        Assert.NotNull(unchanged.PaymentLinkUrl);
        Assert.Null(unchanged.SquareLinkPurgedUtc);
    }

    [Fact]
    public async Task MissingSquarePaymentLinkId_ClearsLocalFields_WithoutCallingSquare()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var payment = await SeedPaymentAsync(dbContext, team, createdDaysAgo: 31, squarePaymentLinkId: null);
        var square = new FakeSquareClient();

        var result = await CreateService(dbContext, square).RunAsync(team, CancellationToken.None);

        Assert.Equal(1, result.Purged);
        Assert.Empty(square.DeletedPaymentLinkIds);
        var updated = await dbContext.Payments.SingleAsync(p => p.Id == payment.Id);
        Assert.Null(updated.PaymentLinkUrl);
        Assert.Equal(Now, updated.SquareLinkPurgedUtc);
    }

    [Fact]
    public async Task SquareNotConfigured_SkipsQuietly_NoChanges()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, squareConfigured: false);
        var payment = await SeedPaymentAsync(dbContext, team, createdDaysAgo: 31);
        var square = new FakeSquareClient();

        var result = await CreateService(dbContext, square).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.Purged);
        Assert.Empty(square.DeletedPaymentLinkIds);
        var unchanged = await dbContext.Payments.SingleAsync(p => p.Id == payment.Id);
        Assert.NotNull(unchanged.PaymentLinkUrl);
    }

    [Fact]
    public async Task AlreadyPurgedPayment_IsNeverReprocessed()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedPaymentAsync(dbContext, team, createdDaysAgo: 31, paymentLinkUrl: null, squarePaymentLinkId: null, squareLinkPurgedUtc: Now.AddDays(-1));
        var square = new FakeSquareClient();

        var result = await CreateService(dbContext, square).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.Purged);
        Assert.Empty(square.DeletedPaymentLinkIds);
    }

    [Fact]
    public async Task PaidPayment_IsNeverPurged()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var payment = await SeedPaymentAsync(dbContext, team, createdDaysAgo: 31, status: PaymentStatus.Paid);
        var square = new FakeSquareClient();

        var result = await CreateService(dbContext, square).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.Purged);
        Assert.Empty(square.DeletedPaymentLinkIds);
        var unchanged = await dbContext.Payments.SingleAsync(p => p.Id == payment.Id);
        Assert.NotNull(unchanged.PaymentLinkUrl);
    }
}
