using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.ExamTools;
using VeSessionManager.Core.Ingestion;
using Xunit;

namespace VeSessionManager.Core.Tests;

public class SessionIngestionServiceTests
{
    private static readonly DateTime Now = new(2026, 7, 19, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SessionStart = new(2026, 7, 24, 17, 0, 0, DateTimeKind.Utc);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private sealed class FakeExamToolsClient : IExamToolsClient
    {
        public List<ExamToolsSession> Sessions { get; } = [];
        public Dictionary<string, List<ExamToolsApplicant>> ApplicantsBySession { get; } = [];
        public List<string> ApplicantFetches { get; } = [];

        public Task<IReadOnlyList<ExamToolsSession>> GetTeamSessionsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExamToolsSession>>(Sessions);

        public Task<IReadOnlyList<ExamToolsApplicant>> GetSessionApplicantsAsync(string examToolsSessionId, CancellationToken cancellationToken)
        {
            ApplicantFetches.Add(examToolsSessionId);
            return Task.FromResult<IReadOnlyList<ExamToolsApplicant>>(
                ApplicantsBySession.TryGetValue(examToolsSessionId, out var applicants) ? applicants : []);
        }
    }

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    /// <summary>Seeds the Vec/User/FeeConfiguration rows ingestion depends on (mirrors DevDataSeeder).</summary>
    private static async Task SeedVecAndFeeConfigAsync(AppDbContext dbContext)
    {
        var vec = new Vec { Name = "ARRL" };
        var user = new User { Name = "System", Email = "system@localhost", Role = UserRole.Admin };
        dbContext.FeeConfigurations.Add(new FeeConfiguration
        {
            Vec = vec,
            EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            FeeCollectionEnabled = true,
            ExamFeeAmount = 15m,
            RetainedAmount = 7m,
            CreatedByUser = user,
            CreatedUtc = Now
        });
        await dbContext.SaveChangesAsync();
    }

    private static SessionIngestionService CreateService(AppDbContext dbContext, FakeExamToolsClient client) =>
        new(dbContext, client, new FixedTimeProvider(Now), NullLogger<SessionIngestionService>.Instance);

    private static ExamToolsSession PendingSession(
        string id = "session-1", DateTime? date = null, int? applicantCount = 0, string summary = "July Session") =>
        new()
        {
            Id = id,
            Date = date ?? SessionStart,
            Vec = "arrl",
            State = "pend",
            ApplicantCount = applicantCount,
            SessionDef = new ExamToolsSessionDef { Summary = summary }
        };

    private static ExamToolsApplicant Applicant(
        string id = "applicant-1", string first = "Roana", string last = "Glory",
        string email = "roana@example.com", string frn = "0012345678") =>
        new()
        {
            Id = id,
            Firstname = first,
            Lastname = last,
            Email = email,
            Frn = frn,
            HasFelony = false,
            Created = new DateTime(2026, 7, 10, 2, 28, 2, DateTimeKind.Utc)
        };

    [Fact]
    public async Task NewPendingSession_IsInserted_AndRepollDoesNotDuplicate()
    {
        await using var dbContext = CreateContext();
        await SeedVecAndFeeConfigAsync(dbContext);
        var client = new FakeExamToolsClient();
        client.Sessions.Add(PendingSession());
        var sut = CreateService(dbContext, client);

        var result = await sut.RunAsync(CancellationToken.None);

        Assert.Equal(1, result.SessionsAdded);
        var session = Assert.Single(dbContext.Sessions);
        Assert.Equal("session-1", session.ExamToolsSessionId);
        Assert.Equal("July Session", session.Title);
        Assert.Equal(SessionStart, session.ScheduledStartUtc);
        Assert.Equal(SessionStatus.Active, session.Status);
        Assert.Equal(Now, session.CreatedUtc);
        Assert.NotEqual(0, session.VecId);
        Assert.NotEqual(0, session.FeeConfigurationId);

        var repollResult = await CreateService(dbContext, client).RunAsync(CancellationToken.None);
        Assert.Equal(0, repollResult.SessionsAdded);
        Assert.Single(dbContext.Sessions);
    }

    [Fact]
    public async Task StalePendingSessionInThePast_IsNotIngested()
    {
        await using var dbContext = CreateContext();
        await SeedVecAndFeeConfigAsync(dbContext);
        var client = new FakeExamToolsClient();
        // Observed on the real dev feed: sessions from years ago still in state "pend".
        client.Sessions.Add(PendingSession(id: "stale-session", date: Now.AddYears(-2)));

        var result = await CreateService(dbContext, client).RunAsync(CancellationToken.None);

        Assert.Equal(0, result.SessionsAdded);
        Assert.Empty(dbContext.Sessions);
    }

    [Fact]
    public async Task UnknownDoneSession_IsNotIngested()
    {
        await using var dbContext = CreateContext();
        await SeedVecAndFeeConfigAsync(dbContext);
        var client = new FakeExamToolsClient();
        var done = PendingSession(id: "old-session");
        done.State = "done";
        client.Sessions.Add(done);

        var result = await CreateService(dbContext, client).RunAsync(CancellationToken.None);

        Assert.Equal(0, result.SessionsAdded);
        Assert.Empty(dbContext.Sessions);
    }

    [Fact]
    public async Task NewSession_WithoutFeeConfiguration_IsSkippedAndIngestsOnceConfigExists()
    {
        await using var dbContext = CreateContext();
        // Vec exists but has no fee configuration yet.
        dbContext.Vecs.Add(new Vec { Name = "ARRL" });
        await dbContext.SaveChangesAsync();
        var client = new FakeExamToolsClient();
        client.Sessions.Add(PendingSession());

        var result = await CreateService(dbContext, client).RunAsync(CancellationToken.None);

        Assert.Equal(1, result.SessionsSkippedNoConfig);
        Assert.Empty(dbContext.Sessions);

        var user = new User { Name = "System", Email = "system@localhost", Role = UserRole.Admin };
        dbContext.FeeConfigurations.Add(new FeeConfiguration
        {
            VecId = dbContext.Vecs.Single().Id,
            EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            FeeCollectionEnabled = true,
            ExamFeeAmount = 15m,
            CreatedByUser = user,
            CreatedUtc = Now
        });
        await dbContext.SaveChangesAsync();

        var retryResult = await CreateService(dbContext, client).RunAsync(CancellationToken.None);
        Assert.Equal(1, retryResult.SessionsAdded);
        Assert.Single(dbContext.Sessions);
    }

    [Fact]
    public async Task NewApplicants_AreInsertedWithMappedFields()
    {
        await using var dbContext = CreateContext();
        await SeedVecAndFeeConfigAsync(dbContext);
        var client = new FakeExamToolsClient();
        client.Sessions.Add(PendingSession(applicantCount: 2));
        client.ApplicantsBySession["session-1"] =
        [
            Applicant(),
            Applicant(id: "applicant-2", first: "Tomasina", last: "Susanna", email: "tomasina@example.com", frn: "0000000000")
        ];

        var result = await CreateService(dbContext, client).RunAsync(CancellationToken.None);

        Assert.Equal(2, result.CandidatesAdded);
        var candidates = dbContext.Candidates.OrderBy(c => c.ExamToolsApplicantId).ToList();

        Assert.Equal("Roana Glory", candidates[0].Name);
        Assert.Equal("roana@example.com", candidates[0].Email);
        Assert.Equal("0012345678", candidates[0].Frn);
        Assert.False(candidates[0].FrnMissingAtRegistration);
        Assert.False(candidates[0].HasFelonyDisclosure);
        Assert.Equal(new DateTime(2026, 7, 10, 2, 28, 2, DateTimeKind.Utc), candidates[0].DateRegisteredUtc);
        Assert.Equal(CandidateApplicationStatus.Unmatched, candidates[0].ApplicationStatus);

        // ExamTools' all-zeros FRN placeholder means "registered without an FRN".
        Assert.Null(candidates[1].Frn);
        Assert.True(candidates[1].FrnMissingAtRegistration);
    }

    [Fact]
    public async Task Repoll_DoesNotDuplicateCandidates_AndAppliesChangedEmail()
    {
        await using var dbContext = CreateContext();
        await SeedVecAndFeeConfigAsync(dbContext);
        var client = new FakeExamToolsClient();
        client.Sessions.Add(PendingSession(applicantCount: 1));
        client.ApplicantsBySession["session-1"] = [Applicant()];
        await CreateService(dbContext, client).RunAsync(CancellationToken.None);

        client.ApplicantsBySession["session-1"] = [Applicant(email: "new-address@example.com")];
        var result = await CreateService(dbContext, client).RunAsync(CancellationToken.None);

        Assert.Equal(0, result.CandidatesAdded);
        Assert.Equal(1, result.CandidatesUpdated);
        var candidate = Assert.Single(dbContext.Candidates);
        Assert.Equal("new-address@example.com", candidate.Email);
    }

    [Fact]
    public async Task PurgedOrTerminalCandidates_AreNotUpdated()
    {
        await using var dbContext = CreateContext();
        await SeedVecAndFeeConfigAsync(dbContext);
        var client = new FakeExamToolsClient();
        client.Sessions.Add(PendingSession(applicantCount: 2));
        client.ApplicantsBySession["session-1"] = [Applicant(), Applicant(id: "applicant-2", email: "second@example.com")];
        await CreateService(dbContext, client).RunAsync(CancellationToken.None);

        var purged = dbContext.Candidates.Single(c => c.ExamToolsApplicantId == "applicant-1");
        purged.Name = null;
        purged.Email = null;
        purged.PiiPurgedUtc = Now;
        var terminal = dbContext.Candidates.Single(c => c.ExamToolsApplicantId == "applicant-2");
        terminal.ApplicationStatus = CandidateApplicationStatus.Granted;
        await dbContext.SaveChangesAsync();

        client.ApplicantsBySession["session-1"] =
        [
            Applicant(email: "resurrected@example.com"),
            Applicant(id: "applicant-2", email: "changed@example.com")
        ];
        var result = await CreateService(dbContext, client).RunAsync(CancellationToken.None);

        Assert.Equal(0, result.CandidatesUpdated);
        Assert.Null(dbContext.Candidates.Single(c => c.ExamToolsApplicantId == "applicant-1").Email);
        Assert.Equal("second@example.com", dbContext.Candidates.Single(c => c.ExamToolsApplicantId == "applicant-2").Email);
    }

    [Fact]
    public async Task FrnPlaceholderInFeed_DoesNotOverwriteManuallyEnteredFrn()
    {
        await using var dbContext = CreateContext();
        await SeedVecAndFeeConfigAsync(dbContext);
        var client = new FakeExamToolsClient();
        client.Sessions.Add(PendingSession(applicantCount: 1));
        client.ApplicantsBySession["session-1"] = [Applicant(frn: "0000000000")];
        await CreateService(dbContext, client).RunAsync(CancellationToken.None);

        // Session Manager fills the FRN in manually later (spec allows testing without one initially).
        dbContext.Candidates.Single().Frn = "0099999999";
        await dbContext.SaveChangesAsync();

        await CreateService(dbContext, client).RunAsync(CancellationToken.None);

        Assert.Equal("0099999999", dbContext.Candidates.Single().Frn);
    }

    [Fact]
    public async Task Reschedule_WithNoBlockingCandidates_IsAppliedAutomatically()
    {
        await using var dbContext = CreateContext();
        await SeedVecAndFeeConfigAsync(dbContext);
        var client = new FakeExamToolsClient();
        client.Sessions.Add(PendingSession());
        await CreateService(dbContext, client).RunAsync(CancellationToken.None);

        var newStart = SessionStart.AddDays(7);
        client.Sessions[0] = PendingSession(date: newStart);
        var result = await CreateService(dbContext, client).RunAsync(CancellationToken.None);

        Assert.Equal(1, result.SessionsRescheduled);
        var session = Assert.Single(dbContext.Sessions);
        Assert.Equal(newStart, session.ScheduledStartUtc);
        Assert.False(session.RescheduleFlaggedForReview);
    }

    [Fact]
    public async Task Reschedule_WithOnlyTerminalCandidates_IsAppliedAutomatically()
    {
        await using var dbContext = CreateContext();
        await SeedVecAndFeeConfigAsync(dbContext);
        var client = new FakeExamToolsClient();
        client.Sessions.Add(PendingSession(applicantCount: 1));
        client.ApplicantsBySession["session-1"] = [Applicant()];
        await CreateService(dbContext, client).RunAsync(CancellationToken.None);

        // A withdrawn/no-show candidate is terminal and should not block an automatic reschedule.
        dbContext.Candidates.Single().ApplicationStatus = CandidateApplicationStatus.NotTested;
        await dbContext.SaveChangesAsync();

        var newStart = SessionStart.AddDays(7);
        client.Sessions[0] = PendingSession(date: newStart, applicantCount: 1);
        var result = await CreateService(dbContext, client).RunAsync(CancellationToken.None);

        Assert.Equal(1, result.SessionsRescheduled);
        Assert.Equal(newStart, dbContext.Sessions.Single().ScheduledStartUtc);
    }

    [Fact]
    public async Task Reschedule_WithRegisteredCandidates_FlagsOnceAndKeepsStoredTime()
    {
        await using var dbContext = CreateContext();
        await SeedVecAndFeeConfigAsync(dbContext);
        var client = new FakeExamToolsClient();
        client.Sessions.Add(PendingSession(applicantCount: 1));
        client.ApplicantsBySession["session-1"] = [Applicant()];
        await CreateService(dbContext, client).RunAsync(CancellationToken.None);

        var newStart = SessionStart.AddDays(7);
        client.Sessions[0] = PendingSession(date: newStart, applicantCount: 1);
        var result = await CreateService(dbContext, client).RunAsync(CancellationToken.None);

        Assert.Equal(1, result.SessionsFlaggedForReview);
        Assert.Equal(0, result.SessionsRescheduled);
        var session = Assert.Single(dbContext.Sessions.ToList());
        Assert.Equal(SessionStart, session.ScheduledStartUtc); // stored time untouched
        Assert.True(session.RescheduleFlaggedForReview);
        Assert.Equal(Now, session.RescheduleFlaggedUtc);
        var audit = Assert.Single(dbContext.AuditLogs);
        Assert.Null(audit.UserId);
        Assert.Equal("RescheduleFlaggedForReview", audit.Action);
        Assert.Equal(nameof(Session), audit.EntityType);

        // Same mismatch on the next poll must not re-flag or add another audit row.
        var repollResult = await CreateService(dbContext, client).RunAsync(CancellationToken.None);
        Assert.Equal(0, repollResult.SessionsFlaggedForReview);
        Assert.Single(dbContext.AuditLogs);
    }

    [Fact]
    public async Task SessionMissingFromFeed_IsMarkedCancelled()
    {
        await using var dbContext = CreateContext();
        await SeedVecAndFeeConfigAsync(dbContext);
        var client = new FakeExamToolsClient();
        client.Sessions.Add(PendingSession());
        await CreateService(dbContext, client).RunAsync(CancellationToken.None);

        client.Sessions.Clear();
        var result = await CreateService(dbContext, client).RunAsync(CancellationToken.None);

        Assert.Equal(1, result.SessionsCancelled);
        var session = Assert.Single(dbContext.Sessions);
        Assert.Equal(SessionStatus.Cancelled, session.Status);
        Assert.Equal(Now, session.CancelledUtc);

        // A second poll must not "re-cancel" (CancelledUtc stays put) or count it again.
        var repollResult = await CreateService(dbContext, client).RunAsync(CancellationToken.None);
        Assert.Equal(0, repollResult.SessionsCancelled);
    }

    [Fact]
    public async Task CompletedSessionMissingFromFeed_IsNotCancelled()
    {
        await using var dbContext = CreateContext();
        await SeedVecAndFeeConfigAsync(dbContext);
        var client = new FakeExamToolsClient();
        client.Sessions.Add(PendingSession());
        await CreateService(dbContext, client).RunAsync(CancellationToken.None);

        var session = dbContext.Sessions.Single();
        session.TestingCompletedUtc = Now;
        await dbContext.SaveChangesAsync();

        client.Sessions.Clear();
        var result = await CreateService(dbContext, client).RunAsync(CancellationToken.None);

        Assert.Equal(0, result.SessionsCancelled);
        Assert.Equal(SessionStatus.Active, dbContext.Sessions.Single().Status);
    }

    [Fact]
    public async Task ApplicantFetch_IsSkippedWhenFeedShowsZeroAndNoLocalCandidates()
    {
        await using var dbContext = CreateContext();
        await SeedVecAndFeeConfigAsync(dbContext);
        var client = new FakeExamToolsClient();
        client.Sessions.Add(PendingSession(applicantCount: 0));

        await CreateService(dbContext, client).RunAsync(CancellationToken.None);

        Assert.Empty(client.ApplicantFetches);
    }
}
