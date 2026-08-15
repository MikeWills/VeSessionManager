using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Payments;
using VeSessionManager.Core.Square;
using VeSessionManager.Core.Integrations;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Issue #281: the payment <i>creation</i> pass filtered on <c>Status == Active</c> and the
/// <i>link-generation</i> pass did not.
///
/// <para><b>The ordering that makes it reachable is a normal one.</b> Square is an optional
/// integration and is often configured after a team is already running: payment rows accumulate
/// waiting for credentials, by design, and generate on the first poll after the token is set. If a
/// session is cancelled in ExamTools during that window, its rows were still sitting there — so
/// enabling Square minted live checkout links for a session that will never happen.</para>
///
/// <para><c>SessionEventSchedulingService</c> explicitly tears down Zoom and Discord for a cancelled
/// session. Nothing tears down a payment link, so the only defence is never minting one.</para>
/// </summary>
public class CancelledSessionPaymentLinkTests
{
    private static readonly DateTime Now = new(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private sealed class RecordingSquareClient : ISquareClient
    {
        // #375 added these to ISquareClient. Not exercised here — throwing rather than returning a
        // stub keeps that true: if this test ever starts refunding, it says so instead of passing
        // against a fake that quietly agrees.
        public Task<SquareRefund> RefundPaymentAsync(SquareCredentials credentials, SquareRefundRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Refunds are not exercised by this test.");

        public Task<SquareRefund> GetRefundAsync(SquareCredentials credentials, string squareRefundId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Refunds are not exercised by this test.");

        public List<string> LinkedReferenceIds { get; } = [];
        private int _next = 5000;

        public Task<SquarePaymentLink> CreatePaymentLinkAsync(
            SquareCredentials credentials, SquarePaymentLinkRequest request, CancellationToken cancellationToken)
        {
            LinkedReferenceIds.Add(request.ReferenceId);
            var orderId = $"order-{_next++}";
            return Task.FromResult(new SquarePaymentLink
            {
                Id = $"link-{orderId}", OrderId = orderId, Url = $"https://square.link/u/{orderId}"
            });
        }

        public Task CompleteOrderAsync(SquareCredentials c, string orderId, CancellationToken ct) => Task.CompletedTask;
        public Task DeletePaymentLinkAsync(SquareCredentials c, string linkId, CancellationToken ct) => Task.CompletedTask;
    }

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    /// <summary>
    /// Reproduces the real ordering: a Payment already exists with no link (Square was unconfigured
    /// when it was created), and its session has since been cancelled.
    /// </summary>
    private static async Task<(Team Team, Payment Payment)> SeedUnlinkedPaymentAsync(
        AppDbContext dbContext, SessionStatus sessionStatus)
    {
        var team = new Team
        {
            Name = "TESTTEAM",
            SquareAccessToken = "square-token",
            SquareLocationId = "square-location",
            CreatedUtc = Now
        };
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();

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
            TeamId = team.Id,
            FeeConfiguration = feeConfiguration,
            Status = sessionStatus,
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
            Amount = 15m,
            Reason = PaymentReason.InitialExam,
            Status = PaymentStatus.Unpaid,
            CreatedUtc = Now
        };
        dbContext.Payments.Add(payment);
        await dbContext.SaveChangesAsync();
        return (team, payment);
    }

    private static PaymentGenerationService CreateService(AppDbContext dbContext, ISquareClient square) =>
        new(dbContext, square, new FixedTimeProvider(Now), new TeamIntegrationState(NullLogger<TeamIntegrationState>.Instance), NullLogger<PaymentGenerationService>.Instance);

    // ---- The regression ------------------------------------------------------------------------

    [Fact]
    public async Task ACancelledSessionsPendingPayment_NeverGetsALink()
    {
        await using var dbContext = CreateContext();
        var square = new RecordingSquareClient();
        var (team, payment) = await SeedUnlinkedPaymentAsync(dbContext, SessionStatus.Cancelled);

        await CreateService(dbContext, square).RunAsync(team, CancellationToken.None);

        Assert.Empty(square.LinkedReferenceIds);

        var stored = await dbContext.Payments.AsNoTracking().SingleAsync(p => p.Id == payment.Id);
        Assert.Null(stored.PaymentLinkUrl);
    }

    // ---- What must keep working ------------------------------------------------------------------

    /// <summary>
    /// The half that keeps the test above honest: an identical payment on an active session must
    /// still get its link, or the fix would read as correct while having disabled the feature.
    /// </summary>
    [Fact]
    public async Task AnActiveSessionsPendingPayment_StillGetsItsLink()
    {
        await using var dbContext = CreateContext();
        var square = new RecordingSquareClient();
        var (team, payment) = await SeedUnlinkedPaymentAsync(dbContext, SessionStatus.Active);

        await CreateService(dbContext, square).RunAsync(team, CancellationToken.None);

        Assert.Single(square.LinkedReferenceIds);

        var stored = await dbContext.Payments.AsNoTracking().SingleAsync(p => p.Id == payment.Id);
        Assert.NotNull(stored.PaymentLinkUrl);
    }
}
