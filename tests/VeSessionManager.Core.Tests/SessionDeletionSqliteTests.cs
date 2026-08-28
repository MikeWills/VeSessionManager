using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Integrations;
using VeSessionManager.Core.Payments;
using VeSessionManager.Core.Sessions;
using VeSessionManager.Core.Square;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// SessionActionService.DeleteAsync against real SQLite — the InMemory provider does not enforce
/// foreign keys, so the bug this pins (a session whose payment had been refunded could not be
/// deleted at all: Refund -&gt; Payment is Restrict, the delete never removed the refunds, and the
/// FK violation surfaced as an error page) was invisible to <see cref="SessionActionServiceTests"/>.
/// Found live on the WX0MIK test session that carried #431's real refunded payment (2026-08-28).
/// </summary>
public class SessionDeletionSqliteTests
{
    private static readonly DateTime Now = new(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private sealed class ThrowingSquareClient : ISquareClient
    {
        // Deleting a session makes no Square calls; throwing keeps that assertion live.
        public Task<SquarePaymentLink> CreatePaymentLinkAsync(SquareCredentials credentials, SquarePaymentLinkRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by SessionDeletionSqliteTests.");

        public Task CompleteOrderAsync(SquareCredentials credentials, string orderId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by SessionDeletionSqliteTests.");

        public Task DeletePaymentLinkAsync(SquareCredentials credentials, string paymentLinkId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by SessionDeletionSqliteTests.");

        public Task<SquareRefund> RefundPaymentAsync(SquareCredentials credentials, SquareRefundRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by SessionDeletionSqliteTests.");

        public Task<SquareRefund> GetRefundAsync(SquareCredentials credentials, string squareRefundId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by SessionDeletionSqliteTests.");
    }

    private static SessionActionService NewService(AppDbContext dbContext) => new(
        dbContext,
        new SquarePaymentMatchingService(dbContext, new ThrowingSquareClient(), new FixedTimeProvider(Now),
            new TeamIntegrationState(NullLogger<TeamIntegrationState>.Instance), NullLogger<SquarePaymentMatchingService>.Instance),
        new FixedTimeProvider(Now),
        NullLogger<SessionActionService>.Instance);

    [Fact]
    public async Task DeleteAsync_SessionWithRefundedPayment_DeletesRefundRowsToo()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        try
        {
            await using var dbContext = new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
            await dbContext.Database.EnsureCreatedAsync();

            var user = new User { Name = "Admin", Email = "admin@example.org", Role = UserRole.SystemAdmin };
            var vec = new Vec { Name = "ARRL" };
            var team = new Team { Name = "TESTTEAM", CreatedUtc = Now };
            var feeConfiguration = new FeeConfiguration
            {
                Vec = vec, EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                FeeCollectionEnabled = true, ExamFeeAmount = 15m, CreatedByUser = user, CreatedUtc = Now
            };
            var session = new Session
            {
                ExamToolsSessionId = "session-1", Title = "Test Session", ScheduledStartUtc = Now,
                Team = team, Vec = vec, FeeConfiguration = feeConfiguration, CreatedUtc = Now
            };
            var candidate = new Candidate { Session = session, Name = "Refunded Candidate", Email = "candidate@example.com", DateRegisteredUtc = Now };
            var payment = new Payment
            {
                Candidate = candidate, Amount = 15m, Status = PaymentStatus.Paid,
                SquarePaymentId = "sq-payment-1", CreatedUtc = Now
            };
            var refund = new Refund
            {
                Team = team, Payment = payment, SquarePaymentId = "sq-payment-1", AmountUsd = 15m,
                SquareIdempotencyKey = Guid.NewGuid().ToString("N"), Status = RefundStatus.Pending,
                RequestedByUser = user, RequestedUtc = Now
            };
            dbContext.Refunds.Add(refund);
            await dbContext.SaveChangesAsync();

            var result = await NewService(dbContext).DeleteAsync(session.Id, user.Id, CancellationToken.None);

            Assert.Equal(SessionActionResult.Success, result.Result);
            Assert.Empty(await dbContext.Refunds.ToListAsync());
            Assert.Empty(await dbContext.Payments.ToListAsync());
            Assert.Empty(await dbContext.Sessions.ToListAsync());
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }
}
