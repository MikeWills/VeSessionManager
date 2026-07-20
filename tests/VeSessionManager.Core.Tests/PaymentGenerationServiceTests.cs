using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Payments;
using VeSessionManager.Core.Square;
using Xunit;

namespace VeSessionManager.Core.Tests;

public class PaymentGenerationServiceTests
{
    private static readonly DateTime Now = new(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private sealed record CapturedCall(string ReferenceId, string ItemName, decimal AmountUsd);

    private sealed class FakeSquareClient : ISquareClient
    {
        public List<CapturedCall> Calls { get; } = [];
        public List<SquareCredentials> CredentialsUsed { get; } = [];
        public Exception? ThrowOnNextCall { get; set; }
        private int _nextOrderId = 5000;

        public Task<SquarePaymentLink> CreatePaymentLinkAsync(SquareCredentials credentials, SquarePaymentLinkRequest request, CancellationToken cancellationToken)
        {
            CredentialsUsed.Add(credentials);
            Calls.Add(new CapturedCall(request.ReferenceId, request.ItemName, request.AmountUsd));
            if (ThrowOnNextCall is not null)
            {
                var ex = ThrowOnNextCall;
                ThrowOnNextCall = null;
                throw ex;
            }
            var orderId = $"order-{_nextOrderId++}";
            return Task.FromResult(new SquarePaymentLink { OrderId = orderId, Url = $"https://square.link/u/{orderId}" });
        }
    }

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static PaymentGenerationService CreateService(AppDbContext dbContext, ISquareClient square) =>
        new(dbContext, square, new FixedTimeProvider(Now), NullLogger<PaymentGenerationService>.Instance);

    /// <summary>Seeds a Team. squareConfigured=true (default) sets AccessToken/LocationId so Team.IsSquareConfigured is true.</summary>
    private static async Task<Team> SeedTeamAsync(AppDbContext dbContext, bool squareConfigured = true)
    {
        var team = new Team
        {
            Name = "TESTTEAM",
            SquareAccessToken = squareConfigured ? "square-token" : null,
            SquareLocationId = squareConfigured ? "square-location" : null,
            CreatedUtc = Now
        };
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        return team;
    }

    /// <summary>Seeds Vec/User/FeeConfiguration/Session/Candidate. FeeCollectionEnabled defaults true, $15.</summary>
    private static async Task<Candidate> SeedCandidateAsync(
        AppDbContext dbContext, Team team, bool feeCollectionEnabled = true, decimal examFeeAmount = 15m,
        SessionStatus sessionStatus = SessionStatus.Active, bool purged = false)
    {
        var vec = new Vec { Name = "ARRL" };
        var user = new User { Name = "System", Email = "system@localhost", Role = UserRole.Admin };
        var feeConfiguration = new FeeConfiguration
        {
            Vec = vec,
            EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            FeeCollectionEnabled = feeCollectionEnabled,
            ExamFeeAmount = feeCollectionEnabled ? examFeeAmount : null,
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
            Name = purged ? null : "Roana Glory",
            Email = purged ? null : "roana@example.com",
            DateRegisteredUtc = Now,
            PiiPurgedUtc = purged ? Now : null
        };
        dbContext.Candidates.Add(candidate);
        await dbContext.SaveChangesAsync();
        return candidate;
    }

    [Fact]
    public async Task NewCandidate_WithFeeCollectionEnabled_CreatesUnpaidPaymentAndGeneratesLink()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var candidate = await SeedCandidateAsync(dbContext, team);
        var square = new FakeSquareClient();

        var result = await CreateService(dbContext, square).RunAsync(team, CancellationToken.None);

        Assert.Equal(1, result.PaymentsCreated);
        Assert.Equal(1, result.LinksGenerated);
        var payment = dbContext.Payments.Single();
        Assert.Equal(candidate.Id, payment.CandidateId);
        Assert.Equal(PaymentReason.InitialExam, payment.Reason);
        Assert.Equal(15m, payment.Amount);
        Assert.Equal(PaymentStatus.Unpaid, payment.Status);
        Assert.NotNull(payment.PaymentLinkUrl);
        Assert.NotNull(payment.SquarePaymentReferenceId);

        var call = Assert.Single(square.Calls);
        Assert.Equal(payment.Id.ToString(), call.ReferenceId);
        Assert.Equal(15m, call.AmountUsd);
        Assert.Equal(team.Id, Assert.Single(square.CredentialsUsed).TeamId);
    }

    [Fact]
    public async Task NewCandidate_WithFeeCollectionDisabled_CreatesNotApplicablePayment_NeverCallsSquare()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedCandidateAsync(dbContext, team, feeCollectionEnabled: false);
        var square = new FakeSquareClient();

        var result = await CreateService(dbContext, square).RunAsync(team, CancellationToken.None);

        Assert.Equal(1, result.PaymentsCreated);
        Assert.Equal(0, result.LinksGenerated);
        var payment = dbContext.Payments.Single();
        Assert.Equal(PaymentStatus.NotApplicable, payment.Status);
        Assert.Equal(0m, payment.Amount);
        Assert.Null(payment.PaymentLinkUrl);
        Assert.Empty(square.Calls);
    }

    [Fact]
    public async Task Repoll_DoesNotDuplicatePaymentRow_OrCallSquareTwice()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedCandidateAsync(dbContext, team);
        var square = new FakeSquareClient();
        await CreateService(dbContext, square).RunAsync(team, CancellationToken.None);

        var result = await CreateService(dbContext, square).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.PaymentsCreated);
        Assert.Equal(0, result.LinksGenerated);
        Assert.Single(dbContext.Payments);
        Assert.Single(square.Calls);
    }

    [Fact]
    public async Task LinkGenerationFailure_LeavesPaymentRowIntact_AndRetriesOnlyTheLink()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedCandidateAsync(dbContext, team);
        var square = new FakeSquareClient { ThrowOnNextCall = new InvalidOperationException("Square unavailable") };

        var result = await CreateService(dbContext, square).RunAsync(team, CancellationToken.None);

        Assert.Equal(1, result.PaymentsCreated);
        Assert.Equal(0, result.LinksGenerated);
        Assert.Equal(1, result.LinksFailed);
        var payment = dbContext.Payments.Single();
        Assert.Equal(PaymentStatus.Unpaid, payment.Status);
        Assert.Null(payment.PaymentLinkUrl);

        var retryResult = await CreateService(dbContext, square).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, retryResult.PaymentsCreated); // no duplicate row
        Assert.Equal(1, retryResult.LinksGenerated);
        Assert.Single(dbContext.Payments);
        Assert.NotNull(dbContext.Payments.Single().PaymentLinkUrl);
        Assert.Equal(2, square.Calls.Count); // failed attempt + successful retry
    }

    [Fact]
    public async Task CandidateInCancelledSession_IsNotGivenAPayment()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedCandidateAsync(dbContext, team, sessionStatus: SessionStatus.Cancelled);
        var square = new FakeSquareClient();

        var result = await CreateService(dbContext, square).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.PaymentsCreated);
        Assert.Empty(dbContext.Payments);
    }

    [Fact]
    public async Task PurgedCandidate_IsNotGivenAPayment()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedCandidateAsync(dbContext, team, purged: true);
        var square = new FakeSquareClient();

        var result = await CreateService(dbContext, square).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.PaymentsCreated);
        Assert.Empty(dbContext.Payments);
    }

    [Fact]
    public async Task CreateRetestPaymentAsync_CreatesSecondPaymentRow_TrackedIndependently()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var candidate = await SeedCandidateAsync(dbContext, team);
        var square = new FakeSquareClient();
        var service = CreateService(dbContext, square);
        await service.RunAsync(team, CancellationToken.None); // initial payment + link

        var initial = dbContext.Payments.Single();
        initial.Status = PaymentStatus.Paid;
        initial.PaidDateUtc = Now;
        await dbContext.SaveChangesAsync();

        var retest = await service.CreateRetestPaymentAsync(candidate.Id, CancellationToken.None);

        var payments = dbContext.Payments.OrderBy(p => p.Id).ToList();
        Assert.Equal(2, payments.Count);
        Assert.Equal(PaymentReason.InitialExam, payments[0].Reason);
        Assert.Equal(PaymentStatus.Paid, payments[0].Status); // untouched by the retest creation
        Assert.Equal(PaymentReason.Retest, payments[1].Reason);
        Assert.Equal(PaymentStatus.Unpaid, payments[1].Status);
        Assert.NotNull(payments[1].PaymentLinkUrl);
        Assert.Equal(retest.Id, payments[1].Id);

        // Each payment got its own distinct Square order/link — not the initial payment's reused.
        Assert.Equal(2, square.Calls.Count);
        Assert.NotEqual(square.Calls[0].ReferenceId, square.Calls[1].ReferenceId);
    }

    [Fact]
    public async Task CreateRetestPaymentAsync_WithFeeCollectionDisabled_CreatesNotApplicablePayment()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var candidate = await SeedCandidateAsync(dbContext, team, feeCollectionEnabled: false);
        var square = new FakeSquareClient();
        var service = CreateService(dbContext, square);
        await service.RunAsync(team, CancellationToken.None);

        var retest = await service.CreateRetestPaymentAsync(candidate.Id, CancellationToken.None);

        Assert.Equal(PaymentStatus.NotApplicable, retest.Status);
        Assert.Null(retest.PaymentLinkUrl);
        Assert.Empty(square.Calls);
    }

    [Fact]
    public async Task SquareNotConfigured_StillCreatesPaymentRow_ButSkipsLinkGeneration_NoFailureCounted()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, squareConfigured: false);
        await SeedCandidateAsync(dbContext, team);
        var square = new FakeSquareClient();

        var result = await CreateService(dbContext, square).RunAsync(team, CancellationToken.None);

        Assert.Equal(1, result.PaymentsCreated);
        Assert.Equal(0, result.LinksGenerated);
        Assert.Equal(0, result.LinksFailed); // not attempted, so not a "failure"
        var payment = dbContext.Payments.Single();
        Assert.Equal(PaymentStatus.Unpaid, payment.Status);
        Assert.Null(payment.PaymentLinkUrl);
        Assert.Empty(square.Calls); // CreatePaymentLinkAsync itself must never be invoked

        // Once Square becomes configured, the very next poll must backfill the link with no other change.
        team.SquareAccessToken = "square-token";
        team.SquareLocationId = "square-location";
        await dbContext.SaveChangesAsync();
        var retryResult = await CreateService(dbContext, square).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, retryResult.PaymentsCreated); // no duplicate row
        Assert.Equal(1, retryResult.LinksGenerated);
        Assert.NotNull(dbContext.Payments.Single().PaymentLinkUrl);
    }

    [Fact]
    public async Task CreateRetestPaymentAsync_WithSquareNotConfigured_CreatesPaymentRowWithoutAttemptingLink()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, squareConfigured: false);
        var candidate = await SeedCandidateAsync(dbContext, team);
        var square = new FakeSquareClient();
        var service = CreateService(dbContext, square);
        await service.RunAsync(team, CancellationToken.None); // initial payment, also skipped

        var retest = await service.CreateRetestPaymentAsync(candidate.Id, CancellationToken.None);

        Assert.Equal(PaymentStatus.Unpaid, retest.Status);
        Assert.Null(retest.PaymentLinkUrl);
        Assert.Empty(square.Calls);
    }

    [Fact]
    public async Task TwoTeams_EachGeneratesLinkWithItsOwnSquareCredentials()
    {
        await using var dbContext = CreateContext();
        var teamA = await SeedTeamAsync(dbContext);
        var teamB = await SeedTeamAsync(dbContext);
        await SeedCandidateAsync(dbContext, teamA);
        await SeedCandidateAsync(dbContext, teamB);
        var square = new FakeSquareClient();

        await CreateService(dbContext, square).RunAsync(teamA, CancellationToken.None);
        await CreateService(dbContext, square).RunAsync(teamB, CancellationToken.None);

        Assert.Equal([teamA.Id, teamB.Id], square.CredentialsUsed.Select(c => c.TeamId));
    }
}
