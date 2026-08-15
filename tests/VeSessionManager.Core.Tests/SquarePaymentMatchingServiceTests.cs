using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Payments;
using VeSessionManager.Core.Square;
using VeSessionManager.Core.Integrations;
using Xunit;

namespace VeSessionManager.Core.Tests;

public class SquarePaymentMatchingServiceTests
{
    private static readonly DateTime Now = new(2026, 7, 22, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private sealed class FakeSquareClient : ISquareClient
    {
        public List<string> CompletedOrderIds { get; } = [];
        public Exception? ThrowOnCompleteOrder { get; set; }

        public Task<SquarePaymentLink> CreatePaymentLinkAsync(SquareCredentials credentials, SquarePaymentLinkRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by SquarePaymentMatchingServiceTests.");

        public Task CompleteOrderAsync(SquareCredentials credentials, string orderId, CancellationToken cancellationToken)
        {
            if (ThrowOnCompleteOrder is not null)
            {
                throw ThrowOnCompleteOrder;
            }

            CompletedOrderIds.Add(orderId);
            return Task.CompletedTask;
        }

        public Task DeletePaymentLinkAsync(SquareCredentials credentials, string paymentLinkId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by SquarePaymentMatchingServiceTests.");
    }

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static SquarePaymentMatchingService CreateService(AppDbContext dbContext, ISquareClient? squareClient = null) =>
        new(dbContext, squareClient ?? new FakeSquareClient(), new FixedTimeProvider(Now), new TeamIntegrationState(NullLogger<TeamIntegrationState>.Instance), NullLogger<SquarePaymentMatchingService>.Instance);

    private static async Task<(Team Team, Candidate Candidate, Payment Payment)> SeedCandidateWithUnpaidPaymentAsync(
        AppDbContext dbContext, bool squareConfigured = true, bool sessionCompleted = false)
    {
        var team = new Team
        {
            Name = $"TEAM-{Guid.NewGuid():N}",
            SquareAccessToken = squareConfigured ? "sq-token" : null,
            SquareLocationId = squareConfigured ? "sq-location" : null,
            CreatedUtc = Now
        };
        var user = new User { Name = "System", Email = "system@localhost", Role = UserRole.SystemAdmin };
        var vec = new Vec { Name = "ARRL" };
        var feeConfiguration = new FeeConfiguration
        {
            Vec = vec, EffectiveDate = Now, FeeCollectionEnabled = true, ExamFeeAmount = 15m,
            CreatedByUser = user, CreatedUtc = Now
        };
        var session = new Session
        {
            ExamToolsSessionId = $"session-{Guid.NewGuid():N}", Title = "Test Session", ScheduledStartUtc = Now.AddDays(4),
            Team = team, Vec = vec, FeeConfiguration = feeConfiguration, CreatedUtc = Now,
            TestingCompletedUtc = sessionCompleted ? Now.AddHours(-1) : null
        };
        var candidate = new Candidate { Session = session, Name = "Roana Glory", Email = "roana@example.com", DateRegisteredUtc = Now };
        var payment = new Payment { Candidate = candidate, Reason = PaymentReason.InitialExam, Amount = 15m, Status = PaymentStatus.Unpaid, CreatedUtc = Now };
        dbContext.Payments.Add(payment);
        await dbContext.SaveChangesAsync();
        return (team, candidate, payment);
    }

    // ---- DismissAsync (#99) ----

    [Fact]
    public async Task Dismiss_ResolvesRowWithoutMatchingAPayment_AndAuditsOrderIdAndAmount()
    {
        await using var dbContext = CreateContext();
        var (team, _, payment) = await SeedCandidateWithUnpaidPaymentAsync(dbContext);
        var unmatched = new UnmatchedSquarePayment { TeamId = team.Id, SquareOrderId = "order-donation", SquarePaymentId = "sq-9", AmountUsd = 25m, ReceivedUtc = Now.AddMinutes(-5) };
        dbContext.UnmatchedSquarePayments.Add(unmatched);
        await dbContext.SaveChangesAsync();

        var result = await CreateService(dbContext).DismissAsync(unmatched.Id, "  donation, nobody owes it  ", userId: 42, CancellationToken.None);

        Assert.Equal(SquareManualMatchResult.Success, result);
        var resolvedRow = dbContext.UnmatchedSquarePayments.Single();
        Assert.Equal(Now, resolvedRow.ResolvedUtc);
        Assert.Equal(42, resolvedRow.ResolvedByUserId);
        // The whole point: resolved, but pointing at no Payment. That pairing is what tells a
        // dismissal from a match later on.
        Assert.Null(resolvedRow.MatchedPaymentId);
        Assert.Equal("donation, nobody owes it", resolvedRow.ResolutionNote);

        // And the candidate's own payment is untouched — dismissing is not a way to mark someone paid.
        Assert.Equal(PaymentStatus.Unpaid, dbContext.Payments.Single(p => p.Id == payment.Id).Status);

        var audit = Assert.Single(dbContext.AuditLogs, a => a.Action == "SquarePaymentDismissed");
        Assert.Contains("order-donation", audit.Details);
        Assert.Contains("$25.00", audit.Details);
        Assert.Contains("donation, nobody owes it", audit.Details);
    }

    [Fact]
    public async Task Dismiss_NoReasonGiven_StillSucceeds_AndSaysSoInTheAudit()
    {
        await using var dbContext = CreateContext();
        var (team, _, _) = await SeedCandidateWithUnpaidPaymentAsync(dbContext);
        var unmatched = new UnmatchedSquarePayment { TeamId = team.Id, SquareOrderId = "order-2", SquarePaymentId = "sq-2", AmountUsd = 15m, ReceivedUtc = Now };
        dbContext.UnmatchedSquarePayments.Add(unmatched);
        await dbContext.SaveChangesAsync();

        // Whitespace, not null — the caller is a text input, which is what actually arrives.
        var result = await CreateService(dbContext).DismissAsync(unmatched.Id, "   ", userId: 1, CancellationToken.None);

        Assert.Equal(SquareManualMatchResult.Success, result);
        Assert.Null(dbContext.UnmatchedSquarePayments.Single().ResolutionNote);
        Assert.Contains("No reason given.", Assert.Single(dbContext.AuditLogs, a => a.Action == "SquarePaymentDismissed").Details);
    }

    [Fact]
    public async Task Dismiss_NeverCallsSquare()
    {
        await using var dbContext = CreateContext();
        var (team, _, _) = await SeedCandidateWithUnpaidPaymentAsync(dbContext);
        var unmatched = new UnmatchedSquarePayment { TeamId = team.Id, SquareOrderId = "order-3", SquarePaymentId = "sq-3", AmountUsd = 15m, ReceivedUtc = Now };
        dbContext.UnmatchedSquarePayments.Add(unmatched);
        await dbContext.SaveChangesAsync();
        var squareClient = new FakeSquareClient();

        await CreateService(dbContext, squareClient).DismissAsync(unmatched.Id, null, userId: 1, CancellationToken.None);

        // The confirmation wording promises the money is untouched. This is that promise as a test:
        // no order completion, no refund, no Square call of any kind.
        Assert.Empty(squareClient.CompletedOrderIds);
    }

    [Fact]
    public async Task Dismiss_AlreadyResolved_ReturnsAlreadyResolved_AndDoesNotOverwriteTheOriginalResolution()
    {
        await using var dbContext = CreateContext();
        var (team, _, payment) = await SeedCandidateWithUnpaidPaymentAsync(dbContext);
        var unmatched = new UnmatchedSquarePayment
        {
            TeamId = team.Id, SquareOrderId = "order-4", SquarePaymentId = "sq-4", AmountUsd = 15m,
            ReceivedUtc = Now.AddDays(-1), ResolvedUtc = Now.AddDays(-1), ResolvedByUserId = 7, MatchedPaymentId = payment.Id
        };
        dbContext.UnmatchedSquarePayments.Add(unmatched);
        await dbContext.SaveChangesAsync();

        var result = await CreateService(dbContext).DismissAsync(unmatched.Id, "oops", userId: 1, CancellationToken.None);

        Assert.Equal(SquareManualMatchResult.AlreadyResolved, result);
        var row = dbContext.UnmatchedSquarePayments.Single();
        // A dismissal landing on an already-matched row would silently erase the match link.
        Assert.Equal(payment.Id, row.MatchedPaymentId);
        Assert.Equal(7, row.ResolvedByUserId);
        Assert.Null(row.ResolutionNote);
        Assert.Empty(dbContext.AuditLogs.Where(a => a.Action == "SquarePaymentDismissed"));
    }

    [Fact]
    public async Task Dismiss_UnmatchedPaymentNotFound_ReturnsNotFound()
    {
        await using var dbContext = CreateContext();

        var result = await CreateService(dbContext).DismissAsync(999, null, userId: 1, CancellationToken.None);

        Assert.Equal(SquareManualMatchResult.NotFound, result);
    }

    [Fact]
    public async Task Dismiss_ThenManualMatch_IsRefused()
    {
        await using var dbContext = CreateContext();
        var (team, candidate, payment) = await SeedCandidateWithUnpaidPaymentAsync(dbContext);
        var unmatched = new UnmatchedSquarePayment { TeamId = team.Id, SquareOrderId = "order-5", SquarePaymentId = "sq-5", AmountUsd = 15m, ReceivedUtc = Now };
        dbContext.UnmatchedSquarePayments.Add(unmatched);
        await dbContext.SaveChangesAsync();
        var service = CreateService(dbContext);

        await service.DismissAsync(unmatched.Id, "mistake", userId: 1, CancellationToken.None);
        var result = await service.ManuallyMatchAsync(unmatched.Id, candidate.Id, userId: 1, CancellationToken.None);

        // The two actions share one ResolvedUtc guard, so dismissing genuinely closes the row rather
        // than just hiding it from a filter.
        Assert.Equal(SquareManualMatchResult.AlreadyResolved, result);
        Assert.Equal(PaymentStatus.Unpaid, dbContext.Payments.Single(p => p.Id == payment.Id).Status);
    }

    // ---- ManuallyMatchAsync ----

    [Fact]
    public async Task ManuallyMatch_ValidCandidate_MarksPaymentPaid_SetsOrderId_ResolvesRow()
    {
        await using var dbContext = CreateContext();
        var (team, candidate, payment) = await SeedCandidateWithUnpaidPaymentAsync(dbContext);
        var unmatched = new UnmatchedSquarePayment { TeamId = team.Id, SquareOrderId = "order-online-page", SquarePaymentId = "sq-payment-1", AmountUsd = 15m, ReceivedUtc = Now.AddMinutes(-5) };
        dbContext.UnmatchedSquarePayments.Add(unmatched);
        await dbContext.SaveChangesAsync();

        var result = await CreateService(dbContext).ManuallyMatchAsync(unmatched.Id, candidate.Id, userId: 42, CancellationToken.None);

        Assert.Equal(SquareManualMatchResult.Success, result);
        var updatedPayment = dbContext.Payments.Single(p => p.Id == payment.Id);
        Assert.Equal(PaymentStatus.Paid, updatedPayment.Status);
        Assert.Equal("order-online-page", updatedPayment.SquarePaymentReferenceId);
        Assert.Equal(Now, updatedPayment.PaidDateUtc);
        var resolvedRow = dbContext.UnmatchedSquarePayments.Single();
        Assert.Equal(Now, resolvedRow.ResolvedUtc);
        Assert.Equal(42, resolvedRow.ResolvedByUserId);
        Assert.Equal(payment.Id, resolvedRow.MatchedPaymentId);
        Assert.Single(dbContext.AuditLogs, a => a.Action == "SquarePaymentManuallyMatched");
    }

    [Fact]
    public async Task ManuallyMatch_AmountPaidLessThanOwed_StillMatchesPaid_ButFlagsMismatch()
    {
        // The team's separate Square-hosted checkout page only offers $5 (ARRL youth) or $15
        // (standard) — a $5 payment against a Payment created at the $15 rate (youth status isn't
        // known until test day) is a routine, legitimate outcome here, not something to withhold
        // Paid status over. It should still be flagged so a Session Manager can follow up.
        await using var dbContext = CreateContext();
        var (team, candidate, payment) = await SeedCandidateWithUnpaidPaymentAsync(dbContext);
        var unmatched = new UnmatchedSquarePayment { TeamId = team.Id, SquareOrderId = "order-youth-rate", SquarePaymentId = "sq-1", AmountUsd = 5m, ReceivedUtc = Now };
        dbContext.UnmatchedSquarePayments.Add(unmatched);
        await dbContext.SaveChangesAsync();

        var result = await CreateService(dbContext).ManuallyMatchAsync(unmatched.Id, candidate.Id, userId: 1, CancellationToken.None);

        Assert.Equal(SquareManualMatchResult.Success, result);
        var updated = dbContext.Payments.Single(p => p.Id == payment.Id);
        Assert.Equal(PaymentStatus.Paid, updated.Status);
        Assert.Equal(5m, updated.SquareAmountPaidUsd);
        Assert.Equal(Now, updated.AmountMismatchFlaggedUtc);
    }

    [Fact]
    public async Task ManuallyMatch_AmountPaidEqualsOwed_DoesNotFlagMismatch()
    {
        await using var dbContext = CreateContext();
        var (team, candidate, payment) = await SeedCandidateWithUnpaidPaymentAsync(dbContext);
        var unmatched = new UnmatchedSquarePayment { TeamId = team.Id, SquareOrderId = "order-standard-rate", SquarePaymentId = "sq-1", AmountUsd = 15m, ReceivedUtc = Now };
        dbContext.UnmatchedSquarePayments.Add(unmatched);
        await dbContext.SaveChangesAsync();

        await CreateService(dbContext).ManuallyMatchAsync(unmatched.Id, candidate.Id, userId: 1, CancellationToken.None);

        var updated = dbContext.Payments.Single(p => p.Id == payment.Id);
        Assert.Equal(15m, updated.SquareAmountPaidUsd);
        Assert.Null(updated.AmountMismatchFlaggedUtc);
    }

    [Fact]
    public async Task ManuallyMatch_UnmatchedPaymentNotFound_ReturnsNotFound()
    {
        await using var dbContext = CreateContext();
        var (_, candidate, _) = await SeedCandidateWithUnpaidPaymentAsync(dbContext);

        var result = await CreateService(dbContext).ManuallyMatchAsync(999, candidate.Id, userId: 1, CancellationToken.None);

        Assert.Equal(SquareManualMatchResult.NotFound, result);
    }

    [Fact]
    public async Task ManuallyMatch_AlreadyResolved_ReturnsAlreadyResolved_DoesNotReapply()
    {
        await using var dbContext = CreateContext();
        var (team, candidate, payment) = await SeedCandidateWithUnpaidPaymentAsync(dbContext);
        var unmatched = new UnmatchedSquarePayment
        {
            TeamId = team.Id, SquareOrderId = "order-1", SquarePaymentId = "sq-1", AmountUsd = 15m,
            ReceivedUtc = Now.AddDays(-1), ResolvedUtc = Now.AddDays(-1), ResolvedByUserId = 7
        };
        dbContext.UnmatchedSquarePayments.Add(unmatched);
        await dbContext.SaveChangesAsync();

        var result = await CreateService(dbContext).ManuallyMatchAsync(unmatched.Id, candidate.Id, userId: 1, CancellationToken.None);

        Assert.Equal(SquareManualMatchResult.AlreadyResolved, result);
        Assert.Equal(PaymentStatus.Unpaid, dbContext.Payments.Single(p => p.Id == payment.Id).Status);
    }

    [Fact]
    public async Task ManuallyMatch_CandidateOnDifferentTeam_ReturnsCandidateNotFound_DoesNotTouchPayment()
    {
        await using var dbContext = CreateContext();
        var (teamA, _, _) = await SeedCandidateWithUnpaidPaymentAsync(dbContext);
        var (_, candidateB, paymentB) = await SeedCandidateWithUnpaidPaymentAsync(dbContext);
        var unmatched = new UnmatchedSquarePayment { TeamId = teamA.Id, SquareOrderId = "order-1", SquarePaymentId = "sq-1", AmountUsd = 15m, ReceivedUtc = Now };
        dbContext.UnmatchedSquarePayments.Add(unmatched);
        await dbContext.SaveChangesAsync();

        var result = await CreateService(dbContext).ManuallyMatchAsync(unmatched.Id, candidateB.Id, userId: 1, CancellationToken.None);

        Assert.Equal(SquareManualMatchResult.CandidateNotFound, result);
        Assert.Equal(PaymentStatus.Unpaid, dbContext.Payments.Single(p => p.Id == paymentB.Id).Status);
    }

    [Fact]
    public async Task ManuallyMatch_CandidateHasNoUnpaidPayment_ReturnsNoOutstandingPayment()
    {
        await using var dbContext = CreateContext();
        var (team, candidate, payment) = await SeedCandidateWithUnpaidPaymentAsync(dbContext);
        payment.Status = PaymentStatus.Paid;
        await dbContext.SaveChangesAsync();
        var unmatched = new UnmatchedSquarePayment { TeamId = team.Id, SquareOrderId = "order-1", SquarePaymentId = "sq-1", AmountUsd = 15m, ReceivedUtc = Now };
        dbContext.UnmatchedSquarePayments.Add(unmatched);
        await dbContext.SaveChangesAsync();

        var result = await CreateService(dbContext).ManuallyMatchAsync(unmatched.Id, candidate.Id, userId: 1, CancellationToken.None);

        Assert.Equal(SquareManualMatchResult.NoOutstandingPayment, result);
    }

    [Fact]
    public async Task ManuallyMatch_SessionAlreadyCompleted_AlsoCompletesSquareOrder()
    {
        // The other direction from SessionActionServiceTests' equivalent test — here the session
        // was already marked completed *before* the payment gets matched.
        await using var dbContext = CreateContext();
        var (team, candidate, payment) = await SeedCandidateWithUnpaidPaymentAsync(dbContext, sessionCompleted: true);
        var unmatched = new UnmatchedSquarePayment { TeamId = team.Id, SquareOrderId = "order-late-payment", SquarePaymentId = "sq-1", AmountUsd = 15m, ReceivedUtc = Now };
        dbContext.UnmatchedSquarePayments.Add(unmatched);
        await dbContext.SaveChangesAsync();

        var square = new FakeSquareClient();
        var result = await CreateService(dbContext, square).ManuallyMatchAsync(unmatched.Id, candidate.Id, userId: 1, CancellationToken.None);

        Assert.Equal(SquareManualMatchResult.Success, result);
        Assert.Equal("order-late-payment", Assert.Single(square.CompletedOrderIds));
        Assert.Equal(Now, dbContext.Payments.Single(p => p.Id == payment.Id).SquareOrderCompletedUtc);
    }

    [Fact]
    public async Task ManuallyMatch_SquareNotConfigured_MatchesPaymentButSkipsCompletion_DoesNotThrow()
    {
        await using var dbContext = CreateContext();
        var (team, candidate, payment) = await SeedCandidateWithUnpaidPaymentAsync(dbContext, squareConfigured: false, sessionCompleted: true);
        var unmatched = new UnmatchedSquarePayment { TeamId = team.Id, SquareOrderId = "order-1", SquarePaymentId = "sq-1", AmountUsd = 15m, ReceivedUtc = Now };
        dbContext.UnmatchedSquarePayments.Add(unmatched);
        await dbContext.SaveChangesAsync();

        var result = await CreateService(dbContext).ManuallyMatchAsync(unmatched.Id, candidate.Id, userId: 1, CancellationToken.None);

        Assert.Equal(SquareManualMatchResult.Success, result);
        Assert.Equal(PaymentStatus.Paid, dbContext.Payments.Single(p => p.Id == payment.Id).Status);
        Assert.Null(dbContext.Payments.Single(p => p.Id == payment.Id).SquareOrderCompletedUtc);
    }

    [Fact]
    public async Task ManuallyMatch_CompleteOrderCallFails_StillReturnsSuccess_LeavesOrderCompletedUtcNull()
    {
        // Completing the Square order is a housekeeping follow-up, not something that should make
        // the match itself look like it failed — see SquarePaymentMatchingService's own doc comment.
        await using var dbContext = CreateContext();
        var (team, candidate, payment) = await SeedCandidateWithUnpaidPaymentAsync(dbContext, sessionCompleted: true);
        var unmatched = new UnmatchedSquarePayment { TeamId = team.Id, SquareOrderId = "order-1", SquarePaymentId = "sq-1", AmountUsd = 15m, ReceivedUtc = Now };
        dbContext.UnmatchedSquarePayments.Add(unmatched);
        await dbContext.SaveChangesAsync();
        var square = new FakeSquareClient { ThrowOnCompleteOrder = new InvalidOperationException("Square is down") };

        var result = await CreateService(dbContext, square).ManuallyMatchAsync(unmatched.Id, candidate.Id, userId: 1, CancellationToken.None);

        Assert.Equal(SquareManualMatchResult.Success, result);
        var updated = dbContext.Payments.Single(p => p.Id == payment.Id);
        Assert.Equal(PaymentStatus.Paid, updated.Status);
        Assert.Null(updated.SquareOrderCompletedUtc);
    }
}
