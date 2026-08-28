using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Payments;
using VeSessionManager.Core.Square;
using VeSessionManager.Core.Integrations;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// PaymentGenerationService's per-candidate save + DbUpdateException handling only means anything
/// against a provider that actually enforces the (CandidateId, Reason) unique index — EF InMemory
/// never raises the violation, so an InMemory test would exercise the happy path and call it a pass.
/// Real SQLite, as in <see cref="VecExamToolsCodeSqliteTests"/>.
/// </summary>
public class PaymentGenerationCollisionSqliteTests
{
    private static readonly DateTime Now = new(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);

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

        public List<string> ReferenceIds { get; } = [];
        private int _nextOrderId = 5000;

        public Task<SquarePaymentLink> CreatePaymentLinkAsync(SquareCredentials credentials, SquarePaymentLinkRequest request, CancellationToken cancellationToken)
        {
            ReferenceIds.Add(request.ReferenceId);
            var orderId = $"order-{_nextOrderId++}";
            return Task.FromResult(new SquarePaymentLink { Id = $"link-{orderId}", OrderId = orderId, Url = $"https://square.link/u/{orderId}" });
        }

        public Task CompleteOrderAsync(SquareCredentials credentials, string orderId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeletePaymentLinkAsync(SquareCredentials credentials, string paymentLinkId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>
    /// Models the actual race: the *other* process (Web's manual refresh vs. the Worker's tick)
    /// commits its InitialExam payment after this run has already read the candidate list and
    /// concluded there wasn't one. Firing on the first SaveChangesAsync of the run puts the collision
    /// on the first candidate, so the test also proves the loop carries on to the second.
    /// </summary>
    private sealed class RivalProcessInterceptor(int candidateId) : SaveChangesInterceptor
    {
        public bool Armed { get; set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Armed && eventData.Context is not null)
            {
                Armed = false;
                await eventData.Context.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO "Payments" ("CandidateId", "Reason", "Amount", "Status", "CreatedUtc")
                    VALUES ({0}, 0, 15, 0, '2026-08-03 12:00:00')
                    """.Replace("{0}", candidateId.ToString()), cancellationToken);
            }

            return result;
        }
    }

    [Fact]
    public async Task RunAsync_WhenAnotherProcessAlreadyCreatedOnePayment_StillCreatesTheOther_AndCountsTheSkip()
    {
        // Arrange — two candidates on one team, both needing an InitialExam payment.
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var _ = connection;

        var team = new Team { Name = "TESTTEAM", SquareAccessToken = "square-token", SquareLocationId = "square-location", CreatedUtc = Now };
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
            Title = "August Session",
            ScheduledStartUtc = Now.AddDays(4),
            DurationMinutes = 60,
            Vec = vec,
            Team = team,
            FeeConfiguration = feeConfiguration,
            Status = SessionStatus.Active,
            CreatedUtc = Now
        };

        int candidateAId;
        int candidateBId;
        await using (var seedContext = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options))
        {
            await seedContext.Database.EnsureCreatedAsync();
            var candidateA = new Candidate { ExamToolsApplicantId = "applicant-a", Session = session, Name = "Ada", Email = "a@example.com", DateRegisteredUtc = Now };
            var candidateB = new Candidate { ExamToolsApplicantId = "applicant-b", Session = session, Name = "Bo", Email = "b@example.com", DateRegisteredUtc = Now };
            seedContext.Candidates.AddRange(candidateA, candidateB);
            await seedContext.SaveChangesAsync();
            candidateAId = candidateA.Id;
            candidateBId = candidateB.Id;
        }

        var interceptor = new RivalProcessInterceptor(candidateAId);
        await using var dbContext = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection).AddInterceptors(interceptor).Options);
        var teamForRun = await dbContext.Teams.SingleAsync();
        var square = new FakeSquareClient();
        var service = new PaymentGenerationService(dbContext, square, new FixedTimeProvider(Now), new TeamIntegrationState(NullLogger<TeamIntegrationState>.Instance), NullLogger<PaymentGenerationService>.Instance);

        // Act — the rival insert lands on the run's first save, i.e. after its candidate query.
        interceptor.Armed = true;
        var result = await service.RunAsync(teamForRun, CancellationToken.None); // must not throw

        // Assert — one collision cost only its own row; the second candidate is still served.
        Assert.Equal(1, result.PaymentsCreated);
        Assert.Equal(1, result.PaymentsSkippedAlreadyExisted);

        dbContext.ChangeTracker.Clear();
        var payments = await dbContext.Payments.OrderBy(p => p.CandidateId).ToListAsync();
        Assert.Equal(2, payments.Count);
        Assert.Single(payments, p => p.CandidateId == candidateAId); // the rival's row, not a duplicate
        Assert.Single(payments, p => p.CandidateId == candidateBId); // the key assertion: not rolled back
    }
}
