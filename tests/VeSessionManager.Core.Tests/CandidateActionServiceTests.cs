using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VeSessionManager.Core.CandidateActions;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Payments;
using VeSessionManager.Core.Square;
using Xunit;

namespace VeSessionManager.Core.Tests;

public class CandidateActionServiceTests
{
    private static readonly DateTime Now = new(2026, 7, 22, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private sealed class FakeSquareClient : ISquareClient
    {
        private int _nextOrderId = 9000;

        public Task<SquarePaymentLink> CreatePaymentLinkAsync(SquareCredentials credentials, SquarePaymentLinkRequest request, CancellationToken cancellationToken)
        {
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

    private static CandidateActionService CreateService(AppDbContext dbContext) => new(
        dbContext,
        new PaymentGenerationService(dbContext, new FakeSquareClient(), new FixedTimeProvider(Now), NullLogger<PaymentGenerationService>.Instance),
        new FixedTimeProvider(Now),
        NullLogger<CandidateActionService>.Instance);

    private static async Task<(Team Team, User User, Vec Vec)> SeedTeamAsync(AppDbContext dbContext)
    {
        var team = new Team { Name = "TESTTEAM", CreatedUtc = Now };
        var user = new User { Name = "Session Manager", Email = "sm@example.com", Role = UserRole.SessionManager };
        var vec = new Vec { Name = "ARRL" };
        dbContext.Teams.Add(team);
        dbContext.Users.Add(user);
        dbContext.Vecs.Add(vec);
        await dbContext.SaveChangesAsync();
        return (team, user, vec);
    }

    private static async Task<Session> SeedSessionAsync(AppDbContext dbContext, Team team, Vec vec, User user, string examToolsSessionId = "session-1", SessionStatus status = SessionStatus.Active)
    {
        var feeConfiguration = new FeeConfiguration
        {
            Vec = vec, EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            FeeCollectionEnabled = true, ExamFeeAmount = 15m, CreatedByUser = user, CreatedUtc = Now
        };
        var session = new Session
        {
            ExamToolsSessionId = examToolsSessionId, Title = "Test Session", ScheduledStartUtc = Now.AddDays(1),
            TeamId = team.Id, Vec = vec, FeeConfiguration = feeConfiguration, Status = status, CreatedUtc = Now
        };
        dbContext.Sessions.Add(session);
        await dbContext.SaveChangesAsync();
        return session;
    }

    private static async Task<Candidate> SeedCandidateAsync(
        AppDbContext dbContext, Session session, CandidateApplicationStatus status = CandidateApplicationStatus.Unmatched, bool tested = false)
    {
        var candidate = new Candidate
        {
            SessionId = session.Id, Name = "Test Candidate", Email = "candidate@example.com",
            DateRegisteredUtc = Now, ApplicationStatus = status, Tested = tested
        };
        dbContext.Candidates.Add(candidate);
        await dbContext.SaveChangesAsync();
        return candidate;
    }

    // ---- MarkFailedAsync ----

    [Fact]
    public async Task MarkFailed_FromReceived_SetsFailedAndAuditsIt()
    {
        await using var dbContext = CreateContext();
        var (team, user, vec) = await SeedTeamAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, team, vec, user);
        var candidate = await SeedCandidateAsync(dbContext, session, CandidateApplicationStatus.Received);

        var result = await CreateService(dbContext).MarkFailedAsync(candidate.Id, user.Id, CancellationToken.None);

        Assert.Equal(CandidateActionResult.Success, result);
        var updated = dbContext.Candidates.Single();
        Assert.Equal(CandidateApplicationStatus.Failed, updated.ApplicationStatus);
        Assert.Equal(user.Id, updated.ResultMarkedByUserId);
        Assert.Equal(Now, updated.ResultMarkedUtc);
        Assert.Single(dbContext.AuditLogs, a => a.Action == "CandidateMarkedFailed");
    }

    [Fact]
    public async Task MarkFailed_AlreadyGranted_ReturnsInvalidState_DoesNotChangeStatus()
    {
        await using var dbContext = CreateContext();
        var (team, user, vec) = await SeedTeamAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, team, vec, user);
        var candidate = await SeedCandidateAsync(dbContext, session, CandidateApplicationStatus.Granted);

        var result = await CreateService(dbContext).MarkFailedAsync(candidate.Id, user.Id, CancellationToken.None);

        Assert.Equal(CandidateActionResult.InvalidState, result);
        Assert.Equal(CandidateApplicationStatus.Granted, dbContext.Candidates.Single().ApplicationStatus);
    }

    // ---- DeleteAsync ----

    [Fact]
    public async Task Delete_WhenNotTested_ClearsPiiAndSetsNotTestedImmediately()
    {
        await using var dbContext = CreateContext();
        var (team, user, vec) = await SeedTeamAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, team, vec, user);
        var candidate = await SeedCandidateAsync(dbContext, session);
        candidate.Frn = "0012345678";
        candidate.HasFelonyDisclosure = true;
        await dbContext.SaveChangesAsync();

        var result = await CreateService(dbContext).DeleteAsync(candidate.Id, user.Id, CancellationToken.None);

        Assert.Equal(CandidateActionResult.Success, result);
        var updated = dbContext.Candidates.Single();
        Assert.Equal(CandidateApplicationStatus.NotTested, updated.ApplicationStatus);
        Assert.Null(updated.Name);
        Assert.Null(updated.Email);
        Assert.Null(updated.Frn);
        Assert.Null(updated.HasFelonyDisclosure);
        Assert.Equal(Now, updated.PiiPurgedUtc); // immediate, not the delayed Phase 10 window
    }

    [Fact]
    public async Task Delete_WhenAlreadyTested_Refuses_NoChanges()
    {
        await using var dbContext = CreateContext();
        var (team, user, vec) = await SeedTeamAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, team, vec, user);
        var candidate = await SeedCandidateAsync(dbContext, session, tested: true);

        var result = await CreateService(dbContext).DeleteAsync(candidate.Id, user.Id, CancellationToken.None);

        Assert.Equal(CandidateActionResult.AlreadyTested, result);
        Assert.NotNull(dbContext.Candidates.Single().Name);
    }

    [Fact]
    public async Task Delete_AlreadyNotTested_IsIdempotent_NoDuplicateAuditEntry()
    {
        await using var dbContext = CreateContext();
        var (team, user, vec) = await SeedTeamAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, team, vec, user);
        var candidate = await SeedCandidateAsync(dbContext, session, CandidateApplicationStatus.NotTested);

        var result = await CreateService(dbContext).DeleteAsync(candidate.Id, user.Id, CancellationToken.None);

        Assert.Equal(CandidateActionResult.AlreadyDone, result);
        Assert.Empty(dbContext.AuditLogs);
    }

    // ---- MoveAsync ----

    [Fact]
    public async Task Move_SameVec_NotTested_Succeeds_PaymentsUnchanged()
    {
        await using var dbContext = CreateContext();
        var (team, user, vec) = await SeedTeamAsync(dbContext);
        var sourceSession = await SeedSessionAsync(dbContext, team, vec, user, "session-source");
        var targetSession = await SeedSessionAsync(dbContext, team, vec, user, "session-target");
        var candidate = await SeedCandidateAsync(dbContext, sourceSession);
        dbContext.Payments.Add(new Payment { CandidateId = candidate.Id, Reason = PaymentReason.InitialExam, Amount = 15m, Status = PaymentStatus.Paid, CreatedUtc = Now });
        await dbContext.SaveChangesAsync();

        var result = await CreateService(dbContext).MoveAsync(candidate.Id, targetSession.Id, user.Id, CancellationToken.None);

        Assert.Equal(CandidateMoveResult.Success, result);
        var updated = dbContext.Candidates.Single();
        Assert.Equal(targetSession.Id, updated.SessionId);
        var payment = dbContext.Payments.Single();
        Assert.Equal(PaymentStatus.Paid, payment.Status); // carried over unchanged, no new charge
    }

    [Fact]
    public async Task Move_DifferentVec_Refuses()
    {
        await using var dbContext = CreateContext();
        var (team, user, vecA) = await SeedTeamAsync(dbContext);
        var vecB = new Vec { Name = "W5YI" };
        dbContext.Vecs.Add(vecB);
        await dbContext.SaveChangesAsync();
        var sourceSession = await SeedSessionAsync(dbContext, team, vecA, user, "session-a");
        var targetSession = await SeedSessionAsync(dbContext, team, vecB, user, "session-b");
        var candidate = await SeedCandidateAsync(dbContext, sourceSession);

        var result = await CreateService(dbContext).MoveAsync(candidate.Id, targetSession.Id, user.Id, CancellationToken.None);

        Assert.Equal(CandidateMoveResult.DifferentVec, result);
        Assert.Equal(sourceSession.Id, dbContext.Candidates.Single().SessionId);
    }

    [Fact]
    public async Task Move_AlreadyTested_Refuses()
    {
        await using var dbContext = CreateContext();
        var (team, user, vec) = await SeedTeamAsync(dbContext);
        var sourceSession = await SeedSessionAsync(dbContext, team, vec, user, "session-a");
        var targetSession = await SeedSessionAsync(dbContext, team, vec, user, "session-b");
        var candidate = await SeedCandidateAsync(dbContext, sourceSession, tested: true);

        var result = await CreateService(dbContext).MoveAsync(candidate.Id, targetSession.Id, user.Id, CancellationToken.None);

        Assert.Equal(CandidateMoveResult.AlreadyTested, result);
    }

    [Fact]
    public async Task Move_TargetSessionNotFound_Refuses()
    {
        await using var dbContext = CreateContext();
        var (team, user, vec) = await SeedTeamAsync(dbContext);
        var sourceSession = await SeedSessionAsync(dbContext, team, vec, user);
        var candidate = await SeedCandidateAsync(dbContext, sourceSession);

        var result = await CreateService(dbContext).MoveAsync(candidate.Id, 999, user.Id, CancellationToken.None);

        Assert.Equal(CandidateMoveResult.TargetSessionNotFound, result);
    }

    // ---- AddWalkInAsync ----

    [Fact]
    public async Task AddWalkIn_NoFrnProvided_FlagsFrnMissingAtRegistration()
    {
        await using var dbContext = CreateContext();
        var (team, user, vec) = await SeedTeamAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, team, vec, user);

        var candidate = await CreateService(dbContext).AddWalkInAsync(session.Id, "Walk In", "Walk", "walkin@example.com", frn: null, user.Id, CancellationToken.None);

        Assert.Equal(session.Id, candidate.SessionId);
        Assert.Null(candidate.ExamToolsApplicantId);
        Assert.True(candidate.FrnMissingAtRegistration);
        Assert.Null(candidate.Frn);
    }

    [Fact]
    public async Task AddWalkIn_FrnProvided_DoesNotFlagMissing()
    {
        await using var dbContext = CreateContext();
        var (team, user, vec) = await SeedTeamAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, team, vec, user);

        var candidate = await CreateService(dbContext).AddWalkInAsync(session.Id, "Walk In", "Walk", "walkin@example.com", frn: "0099999999", user.Id, CancellationToken.None);

        Assert.False(candidate.FrnMissingAtRegistration);
        Assert.Equal("0099999999", candidate.Frn);
    }

    // ---- MarkPaidManuallyAsync ----

    [Fact]
    public async Task MarkPaidManually_FromUnpaid_Succeeds()
    {
        await using var dbContext = CreateContext();
        var (team, user, vec) = await SeedTeamAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, team, vec, user);
        var candidate = await SeedCandidateAsync(dbContext, session);
        var payment = new Payment { CandidateId = candidate.Id, Reason = PaymentReason.InitialExam, Amount = 15m, Status = PaymentStatus.Unpaid, CreatedUtc = Now };
        dbContext.Payments.Add(payment);
        await dbContext.SaveChangesAsync();

        var result = await CreateService(dbContext).MarkPaidManuallyAsync(payment.Id, user.Id, CancellationToken.None);

        Assert.Equal(CandidateActionResult.Success, result);
        var updated = dbContext.Payments.Single();
        Assert.Equal(PaymentStatus.Paid, updated.Status);
        Assert.Equal(Now, updated.PaidDateUtc);
    }

    [Fact]
    public async Task MarkPaidManually_NotApplicable_Refuses()
    {
        await using var dbContext = CreateContext();
        var (team, user, vec) = await SeedTeamAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, team, vec, user);
        var candidate = await SeedCandidateAsync(dbContext, session);
        var payment = new Payment { CandidateId = candidate.Id, Reason = PaymentReason.InitialExam, Amount = 0m, Status = PaymentStatus.NotApplicable, CreatedUtc = Now };
        dbContext.Payments.Add(payment);
        await dbContext.SaveChangesAsync();

        var result = await CreateService(dbContext).MarkPaidManuallyAsync(payment.Id, user.Id, CancellationToken.None);

        Assert.Equal(CandidateActionResult.InvalidState, result);
    }

    // ---- CreateRetestPaymentAsync ----

    [Fact]
    public async Task CreateRetestPayment_CandidateFailed_Succeeds()
    {
        await using var dbContext = CreateContext();
        var (team, user, vec) = await SeedTeamAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, team, vec, user);
        var candidate = await SeedCandidateAsync(dbContext, session, CandidateApplicationStatus.Failed);

        var result = await CreateService(dbContext).CreateRetestPaymentAsync(candidate.Id, user.Id, CancellationToken.None);

        Assert.Equal(CandidateActionResult.Success, result);
        var payment = Assert.Single(dbContext.Payments);
        Assert.Equal(PaymentReason.Retest, payment.Reason);
        Assert.Single(dbContext.AuditLogs, a => a.Action == "RetestPaymentCreated");
    }

    [Fact]
    public async Task CreateRetestPayment_CandidateNotFailed_Refuses()
    {
        await using var dbContext = CreateContext();
        var (team, user, vec) = await SeedTeamAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, team, vec, user);
        var candidate = await SeedCandidateAsync(dbContext, session, CandidateApplicationStatus.Received);

        var result = await CreateService(dbContext).CreateRetestPaymentAsync(candidate.Id, user.Id, CancellationToken.None);

        Assert.Equal(CandidateActionResult.InvalidState, result);
        Assert.Empty(dbContext.Payments);
    }
}
