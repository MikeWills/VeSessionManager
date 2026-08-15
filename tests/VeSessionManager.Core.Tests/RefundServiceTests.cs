using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Integrations;
using VeSessionManager.Core.Payments;
using VeSessionManager.Core.Square;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Issuing refunds through Square (#375).
///
/// <para>The tests worth reading are the ones about what happens when the call does <b>not</b>
/// cleanly succeed — the persisted idempotency key, and the difference between "Square said no" and
/// "we never heard back". Those two look identical from the outside and must be handled in opposite
/// ways: one is final, the other must be retried and must never become a second refund.</para>
/// </summary>
public class RefundServiceTests
{
    private static readonly DateTime Now = new(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private sealed class FakeSquareClient : ISquareClient
    {
        public List<SquareRefundRequest> RefundRequests { get; } = [];
        public Exception? ThrowOnRefund { get; set; }
        public string RefundStatusToReturn { get; set; } = "PENDING";

        public Task<SquareRefund> RefundPaymentAsync(SquareCredentials credentials, SquareRefundRequest request, CancellationToken cancellationToken)
        {
            RefundRequests.Add(request);
            if (ThrowOnRefund is not null)
            {
                throw ThrowOnRefund;
            }

            return Task.FromResult(new SquareRefund
            {
                // Derived from the idempotency key so a test can assert that a retry reused it and
                // therefore got the same refund back, which is Square's actual guarantee.
                Id = $"refund-for-{request.IdempotencyKey}",
                Status = RefundStatusToReturn,
                AmountUsd = request.AmountUsd
            });
        }

        public Task<SquareRefund> GetRefundAsync(SquareCredentials credentials, string squareRefundId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Status polling belongs to RefundStatusService.");

        public Task<SquarePaymentLink> CreatePaymentLinkAsync(SquareCredentials credentials, SquarePaymentLinkRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by RefundServiceTests.");

        public Task CompleteOrderAsync(SquareCredentials credentials, string orderId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeletePaymentLinkAsync(SquareCredentials credentials, string paymentLinkId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static RefundService CreateService(AppDbContext dbContext, ISquareClient square) =>
        new(dbContext, square, new FixedTimeProvider(Now),
            new TeamIntegrationState(NullLogger<TeamIntegrationState>.Instance),
            NullLogger<RefundService>.Instance);

    private static async Task<(Team Team, Payment Payment, int UserId)> SeedPaidPaymentAsync(
        AppDbContext dbContext,
        string? squarePaymentId = "sq-payment-1",
        PaymentStatus status = PaymentStatus.Paid,
        decimal amount = 15m,
        decimal? squareAmountPaid = null,
        DateTime? paidUtc = null,
        bool squareConfigured = true)
    {
        var team = new Team
        {
            Name = "TESTTEAM",
            SquareAccessToken = squareConfigured ? "square-token" : null,
            SquareLocationId = squareConfigured ? "square-location" : null,
            CreatedUtc = Now
        };
        var vec = new Vec { Name = "ARRL" };
        var user = new User { Name = "System", Email = "system@localhost", Role = UserRole.SystemAdmin };
        var feeConfiguration = new FeeConfiguration
        {
            Vec = vec,
            EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            FeeCollectionEnabled = true,
            ExamFeeAmount = amount,
            CreatedByUser = user,
            CreatedUtc = Now
        };
        var session = new Session
        {
            ExamToolsSessionId = "session-1",
            Title = "August Session",
            ScheduledStartUtc = Now.AddDays(-7),
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
            DateRegisteredUtc = Now.AddDays(-14)
        };
        var payment = new Payment
        {
            Candidate = candidate,
            Reason = PaymentReason.InitialExam,
            Amount = amount,
            SquareAmountPaidUsd = squareAmountPaid,
            Status = status,
            SquarePaymentId = squarePaymentId,
            SquarePaymentReferenceId = "sq-order-1",
            PaidDateUtc = paidUtc ?? Now.AddDays(-5),
            CreatedUtc = Now.AddDays(-14)
        };

        dbContext.AddRange(team, vec, user, feeConfiguration, session, candidate, payment);
        await dbContext.SaveChangesAsync();
        return (team, payment, user.Id);
    }

    // ---- The happy path, and what "success" is allowed to claim -------------------------------

    [Fact]
    public async Task AFullRefundIsSentToSquareAndRecorded()
    {
        using var dbContext = CreateContext();
        var (_, payment, userId) = await SeedPaidPaymentAsync(dbContext);
        var square = new FakeSquareClient();

        var outcome = await CreateService(dbContext, square).RefundPaymentAsync(payment.Id, 15m, "duplicate", userId, default);

        Assert.Equal(RefundResult.Success, outcome.Result);
        var request = Assert.Single(square.RefundRequests);
        Assert.Equal("sq-payment-1", request.SquarePaymentId);
        Assert.Equal(15m, request.AmountUsd);
        Assert.Equal("duplicate", request.Reason);

        var refund = Assert.Single(await dbContext.Refunds.ToListAsync());
        Assert.Equal(payment.Id, refund.PaymentId);
        Assert.Null(refund.UnmatchedSquarePaymentId);
        Assert.Equal(request.IdempotencyKey, refund.SquareIdempotencyKey);
        Assert.Equal($"refund-for-{request.IdempotencyKey}", refund.SquareRefundId);
    }

    /// <summary>
    /// The single most consequential distinction in this feature. Square answers PENDING and takes
    /// up to 14 days; a refund recorded as settled at that point is a lie the status job would never
    /// correct, because settled is exactly what makes it stop looking.
    /// </summary>
    [Fact]
    public async Task APendingRefundIsNotMarkedSettled()
    {
        using var dbContext = CreateContext();
        var (_, payment, userId) = await SeedPaidPaymentAsync(dbContext);
        var square = new FakeSquareClient { RefundStatusToReturn = "PENDING" };

        var outcome = await CreateService(dbContext, square).RefundPaymentAsync(payment.Id, 15m, null, userId, default);

        Assert.Equal(RefundStatus.Pending, outcome.Status);
        var refund = Assert.Single(await dbContext.Refunds.ToListAsync());
        Assert.Null(refund.SettledUtc);
        Assert.NotNull(refund.SubmittedUtc);
    }

    [Fact]
    public async Task ARefundSquareCompletesImmediatelyIsSettled()
    {
        using var dbContext = CreateContext();
        var (_, payment, userId) = await SeedPaidPaymentAsync(dbContext);
        var square = new FakeSquareClient { RefundStatusToReturn = "COMPLETED" };

        var outcome = await CreateService(dbContext, square).RefundPaymentAsync(payment.Id, 15m, null, userId, default);

        Assert.Equal(RefundStatus.Completed, outcome.Status);
        Assert.Equal(Now, Assert.Single(await dbContext.Refunds.ToListAsync()).SettledUtc);
    }

    /// <summary>
    /// An unrecognized status must not settle. Guessing terminal freezes whatever it guessed on the
    /// screen forever; guessing pending costs one more poll.
    /// </summary>
    [Fact]
    public async Task AnUnrecognizedSquareStatusIsTreatedAsStillPending()
    {
        using var dbContext = CreateContext();
        var (_, payment, userId) = await SeedPaidPaymentAsync(dbContext);
        var square = new FakeSquareClient { RefundStatusToReturn = "SOMETHING_NEW" };

        var outcome = await CreateService(dbContext, square).RefundPaymentAsync(payment.Id, 15m, null, userId, default);

        Assert.Equal(RefundStatus.Pending, outcome.Status);
        Assert.Null(Assert.Single(await dbContext.Refunds.ToListAsync()).SettledUtc);
    }

    /// <summary>Refunding must not move the Payment off Paid — PaymentGenerationService would generate it a fresh checkout link and PaymentReminderService would chase the candidate for money they just got back.</summary>
    [Fact]
    public async Task RefundingLeavesThePaymentMarkedPaid()
    {
        using var dbContext = CreateContext();
        var (_, payment, userId) = await SeedPaidPaymentAsync(dbContext);

        await CreateService(dbContext, new FakeSquareClient()).RefundPaymentAsync(payment.Id, 15m, null, userId, default);

        Assert.Equal(PaymentStatus.Paid, (await dbContext.Payments.FindAsync(payment.Id))!.Status);
    }

    // ---- Retry safety -------------------------------------------------------------------------

    /// <summary>
    /// The crash path the save-before-call ordering exists for. A refund whose call never came back
    /// is in Submitting with no refund id; asking again must re-send the <b>same</b> key — which
    /// Square answers with the original refund — rather than issue a second one.
    /// </summary>
    [Fact]
    public async Task ARetryAfterAFailedCallReusesTheSameIdempotencyKeyAndCreatesNoSecondRefund()
    {
        using var dbContext = CreateContext();
        var (_, payment, userId) = await SeedPaidPaymentAsync(dbContext);
        var square = new FakeSquareClient { ThrowOnRefund = new HttpRequestException("connection reset") };
        var service = CreateService(dbContext, square);

        var first = await service.RefundPaymentAsync(payment.Id, 15m, null, userId, default);
        Assert.Equal(RefundResult.CallFailed, first.Result);

        var keyFromFirstAttempt = Assert.Single(await dbContext.Refunds.ToListAsync()).SquareIdempotencyKey;

        square.ThrowOnRefund = null;
        var second = await service.RefundPaymentAsync(payment.Id, 15m, null, userId, default);

        Assert.Equal(RefundResult.Success, second.Result);
        Assert.Equal(2, square.RefundRequests.Count);
        Assert.All(square.RefundRequests, r => Assert.Equal(keyFromFirstAttempt, r.IdempotencyKey));

        // One row, not two — the retry resumed the existing refund rather than starting another.
        var refund = Assert.Single(await dbContext.Refunds.ToListAsync());
        Assert.Equal($"refund-for-{keyFromFirstAttempt}", refund.SquareRefundId);
    }

    /// <summary>
    /// A transport failure must NOT settle the refund. Settling would strand it: the status job only
    /// looks at unsettled rows, so a refund Square may well have accepted would never be recovered.
    /// </summary>
    [Fact]
    public async Task AFailedCallLeavesTheRefundInFlightRatherThanFailed()
    {
        using var dbContext = CreateContext();
        var (_, payment, userId) = await SeedPaidPaymentAsync(dbContext);
        var square = new FakeSquareClient { ThrowOnRefund = new TimeoutException("no answer") };

        await CreateService(dbContext, square).RefundPaymentAsync(payment.Id, 15m, null, userId, default);

        var refund = Assert.Single(await dbContext.Refunds.ToListAsync());
        Assert.Equal(RefundStatus.Submitting, refund.Status);
        Assert.Null(refund.SettledUtc);
        Assert.Null(refund.SquareRefundId);
    }

    /// <summary>
    /// The opposite case, and the reason SquareRefundException is its own type: Square answered, and
    /// the answer was no. The same key would earn the same refusal, so this settles.
    /// </summary>
    [Fact]
    public async Task ARefundSquareRefusesSettlesAsFailedAndKeepsTheReason()
    {
        using var dbContext = CreateContext();
        var (_, payment, userId) = await SeedPaidPaymentAsync(dbContext);
        var square = new FakeSquareClient
        {
            ThrowOnRefund = new SquareRefundException("REFUND_AMOUNT_INVALID: exceeds the refundable amount")
        };

        var outcome = await CreateService(dbContext, square).RefundPaymentAsync(payment.Id, 15m, null, userId, default);

        Assert.Equal(RefundResult.SquareRefused, outcome.Result);
        var refund = Assert.Single(await dbContext.Refunds.ToListAsync());
        Assert.Equal(RefundStatus.Failed, refund.Status);
        Assert.Equal(Now, refund.SettledUtc);
        Assert.Contains("REFUND_AMOUNT_INVALID", refund.FailureDetail);
    }

    // ---- Guards -------------------------------------------------------------------------------

    /// <summary>
    /// The ceiling is what Square took, not what was owed. A $5 youth payment against a $15 Payment
    /// is routine here, and offering to refund $15 would have Square refuse the whole thing.
    /// </summary>
    [Fact]
    public async Task TheRefundableCeilingIsWhatSquareActuallyTook()
    {
        using var dbContext = CreateContext();
        var (_, payment, userId) = await SeedPaidPaymentAsync(dbContext, amount: 15m, squareAmountPaid: 5m);
        var square = new FakeSquareClient();

        var tooMuch = await CreateService(dbContext, square).RefundPaymentAsync(payment.Id, 15m, null, userId, default);

        Assert.Equal(RefundResult.AmountInvalid, tooMuch.Result);
        Assert.Equal(5m, tooMuch.RemainingRefundableUsd);
        Assert.Empty(square.RefundRequests);
    }

    [Fact]
    public async Task APaymentWithNoSquarePaymentIdIsRefusedBeforeAnyCall()
    {
        using var dbContext = CreateContext();
        var (_, payment, userId) = await SeedPaidPaymentAsync(dbContext, squarePaymentId: null);
        var square = new FakeSquareClient();

        var outcome = await CreateService(dbContext, square).RefundPaymentAsync(payment.Id, 15m, null, userId, default);

        Assert.Equal(RefundResult.NoSquarePaymentId, outcome.Result);
        Assert.Empty(square.RefundRequests);
        Assert.Empty(await dbContext.Refunds.ToListAsync());
    }

    [Fact]
    public async Task AnUnpaidPaymentCannotBeRefunded()
    {
        using var dbContext = CreateContext();
        var (_, payment, userId) = await SeedPaidPaymentAsync(dbContext, status: PaymentStatus.Unpaid);
        var square = new FakeSquareClient();

        var outcome = await CreateService(dbContext, square).RefundPaymentAsync(payment.Id, 15m, null, userId, default);

        Assert.Equal(RefundResult.NotPaid, outcome.Result);
        Assert.Empty(square.RefundRequests);
    }

    [Fact]
    public async Task APaymentPastSquaresOneYearWindowIsRefusedBeforeAnyCall()
    {
        using var dbContext = CreateContext();
        var (_, payment, userId) = await SeedPaidPaymentAsync(dbContext, paidUtc: Now.AddDays(-400));
        var square = new FakeSquareClient();

        var outcome = await CreateService(dbContext, square).RefundPaymentAsync(payment.Id, 15m, null, userId, default);

        Assert.Equal(RefundResult.TooOld, outcome.Result);
        Assert.Empty(square.RefundRequests);
    }

    [Fact]
    public async Task ATeamWithNoSquareCredentialsIsToldSoRatherThanFailingAtSquare()
    {
        using var dbContext = CreateContext();
        var (_, payment, userId) = await SeedPaidPaymentAsync(dbContext, squareConfigured: false);
        var square = new FakeSquareClient();

        var outcome = await CreateService(dbContext, square).RefundPaymentAsync(payment.Id, 15m, null, userId, default);

        Assert.Equal(RefundResult.SquareNotConfigured, outcome.Result);
        Assert.Empty(square.RefundRequests);
    }

    [Fact]
    public async Task ZeroAndNegativeAmountsAreRefused()
    {
        using var dbContext = CreateContext();
        var (_, payment, userId) = await SeedPaidPaymentAsync(dbContext);
        var square = new FakeSquareClient();
        var service = CreateService(dbContext, square);

        Assert.Equal(RefundResult.AmountInvalid, (await service.RefundPaymentAsync(payment.Id, 0m, null, userId, default)).Result);
        Assert.Equal(RefundResult.AmountInvalid, (await service.RefundPaymentAsync(payment.Id, -5m, null, userId, default)).Result);
        Assert.Empty(square.RefundRequests);
    }

    /// <summary>Two partial refunds are allowed; a third that would exceed the total is not.</summary>
    [Fact]
    public async Task PartialRefundsAccumulateAgainstTheTotal()
    {
        using var dbContext = CreateContext();
        var (_, payment, userId) = await SeedPaidPaymentAsync(dbContext);
        var square = new FakeSquareClient { RefundStatusToReturn = "COMPLETED" };
        var service = CreateService(dbContext, square);

        Assert.Equal(RefundResult.Success, (await service.RefundPaymentAsync(payment.Id, 10m, null, userId, default)).Result);
        Assert.Equal(RefundResult.Success, (await service.RefundPaymentAsync(payment.Id, 5m, null, userId, default)).Result);

        var overdrawn = await service.RefundPaymentAsync(payment.Id, 1m, null, userId, default);
        Assert.Equal(RefundResult.AmountInvalid, overdrawn.Result);
        Assert.Equal(0m, overdrawn.RemainingRefundableUsd);
        Assert.Equal(2, square.RefundRequests.Count);
    }

    // ---- The unmatched-payment side -----------------------------------------------------------

    /// <summary>
    /// The half of #375 that needed no schema change: UnmatchedSquarePayment has held Square's
    /// payment id since it was written.
    /// </summary>
    [Fact]
    public async Task AnUnmatchedPaymentRefundsAgainstTheIdItAlreadyStores()
    {
        using var dbContext = CreateContext();
        var team = new Team
        {
            Name = "TESTTEAM",
            SquareAccessToken = "square-token",
            SquareLocationId = "square-location",
            CreatedUtc = Now
        };
        var user = new User { Name = "System", Email = "system@localhost", Role = UserRole.SystemAdmin };
        var unmatched = new UnmatchedSquarePayment
        {
            Team = team,
            SquareOrderId = "sq-order-9",
            SquarePaymentId = "sq-payment-9",
            AmountUsd = 20m,
            ReceivedUtc = Now.AddDays(-2)
        };
        dbContext.AddRange(team, user, unmatched);
        await dbContext.SaveChangesAsync();

        var square = new FakeSquareClient();
        var outcome = await CreateService(dbContext, square)
            .RefundUnmatchedPaymentAsync(unmatched.Id, 20m, "paid twice", user.Id, default);

        Assert.Equal(RefundResult.Success, outcome.Result);
        Assert.Equal("sq-payment-9", Assert.Single(square.RefundRequests).SquarePaymentId);

        var refund = Assert.Single(await dbContext.Refunds.ToListAsync());
        Assert.Equal(unmatched.Id, refund.UnmatchedSquarePaymentId);
        Assert.Null(refund.PaymentId);
        Assert.Equal(team.Id, refund.TeamId);
    }

    /// <summary>Refunding does not resolve the row — the page dismisses it separately, and only once the refund has actually gone through.</summary>
    [Fact]
    public async Task RefundingAnUnmatchedPaymentDoesNotResolveIt()
    {
        using var dbContext = CreateContext();
        var team = new Team { Name = "TESTTEAM", SquareAccessToken = "t", SquareLocationId = "l", CreatedUtc = Now };
        var user = new User { Name = "System", Email = "system@localhost", Role = UserRole.SystemAdmin };
        var unmatched = new UnmatchedSquarePayment
        {
            Team = team, SquareOrderId = "sq-order-9", SquarePaymentId = "sq-payment-9",
            AmountUsd = 20m, ReceivedUtc = Now.AddDays(-2)
        };
        dbContext.AddRange(team, user, unmatched);
        await dbContext.SaveChangesAsync();

        await CreateService(dbContext, new FakeSquareClient())
            .RefundUnmatchedPaymentAsync(unmatched.Id, 20m, null, user.Id, default);

        Assert.Null((await dbContext.UnmatchedSquarePayments.FindAsync(unmatched.Id))!.ResolvedUtc);
    }
}
