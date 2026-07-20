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

    /// <summary>Stores sessions/applicants per TeamId so multi-team tests can prove one team's poll never sees another's data — mirrors the real API, which is scoped by the credentials.TeamCode passed in.</summary>
    private sealed class FakeExamToolsClient : IExamToolsClient
    {
        public Dictionary<int, List<ExamToolsSession>> SessionsByTeam { get; } = [];
        public Dictionary<int, Dictionary<string, List<ExamToolsApplicant>>> ApplicantsByTeam { get; } = [];
        public List<string> ApplicantFetches { get; } = [];
        public List<ExamToolsCredentials> CredentialsUsed { get; } = [];

        public List<ExamToolsSession> SessionsFor(int teamId) =>
            SessionsByTeam.TryGetValue(teamId, out var list) ? list : SessionsByTeam[teamId] = [];

        public Dictionary<string, List<ExamToolsApplicant>> ApplicantsFor(int teamId) =>
            ApplicantsByTeam.TryGetValue(teamId, out var dict) ? dict : ApplicantsByTeam[teamId] = [];

        public Task<IReadOnlyList<ExamToolsSession>> GetTeamSessionsAsync(ExamToolsCredentials credentials, CancellationToken cancellationToken)
        {
            CredentialsUsed.Add(credentials);
            return Task.FromResult<IReadOnlyList<ExamToolsSession>>(SessionsFor(credentials.TeamId));
        }

        public Task<IReadOnlyList<ExamToolsApplicant>> GetSessionApplicantsAsync(ExamToolsCredentials credentials, string examToolsSessionId, CancellationToken cancellationToken)
        {
            ApplicantFetches.Add(examToolsSessionId);
            var applicants = ApplicantsFor(credentials.TeamId);
            return Task.FromResult<IReadOnlyList<ExamToolsApplicant>>(
                applicants.TryGetValue(examToolsSessionId, out var list) ? list : []);
        }
    }

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    /// <summary>Seeds a fully-configured Team (IsExamToolsConfigured = true by default).</summary>
    private static async Task<Team> SeedTeamAsync(AppDbContext dbContext, string teamCode = "TESTTEAM")
    {
        var team = new Team
        {
            Name = teamCode,
            ExamToolsTeamCode = teamCode,
            ExamToolsUsername = "testuser",
            ExamToolsPassword = "testpass",
            CreatedUtc = Now
        };
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        return team;
    }

    /// <summary>Seeds the Vec/User/FeeConfiguration rows ingestion depends on (mirrors DevDataSeeder). Vec is shared/global, not Team-scoped — see docs/multi-team.md.</summary>
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
        var team = await SeedTeamAsync(dbContext);
        var client = new FakeExamToolsClient();
        client.SessionsFor(team.Id).Add(PendingSession());
        var sut = CreateService(dbContext, client);

        var result = await sut.RunAsync(team, CancellationToken.None);

        Assert.Equal(1, result.SessionsAdded);
        var session = Assert.Single(dbContext.Sessions);
        Assert.Equal("session-1", session.ExamToolsSessionId);
        Assert.Equal("July Session", session.Title);
        Assert.Equal(SessionStart, session.ScheduledStartUtc);
        Assert.Equal(SessionStatus.Active, session.Status);
        Assert.Equal(Now, session.CreatedUtc);
        Assert.NotEqual(0, session.VecId);
        Assert.Equal(team.Id, session.TeamId);
        Assert.NotEqual(0, session.FeeConfigurationId);

        var repollResult = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);
        Assert.Equal(0, repollResult.SessionsAdded);
        Assert.Single(dbContext.Sessions);
    }

    [Fact]
    public async Task StalePendingSessionInThePast_IsNotIngested()
    {
        await using var dbContext = CreateContext();
        await SeedVecAndFeeConfigAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        var client = new FakeExamToolsClient();
        // Observed on the real dev feed: sessions from years ago still in state "pend".
        client.SessionsFor(team.Id).Add(PendingSession(id: "stale-session", date: Now.AddYears(-2)));

        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.SessionsAdded);
        Assert.Empty(dbContext.Sessions);
    }

    [Fact]
    public async Task UnknownDoneSession_IsNotIngested()
    {
        await using var dbContext = CreateContext();
        await SeedVecAndFeeConfigAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        var client = new FakeExamToolsClient();
        var done = PendingSession(id: "old-session");
        done.State = "done";
        client.SessionsFor(team.Id).Add(done);

        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.SessionsAdded);
        Assert.Empty(dbContext.Sessions);
    }

    [Fact]
    public async Task NewSession_WithoutFeeConfiguration_IsSkippedAndIngestsOnceConfigExists()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        // Vec exists but has no fee configuration yet.
        dbContext.Vecs.Add(new Vec { Name = "ARRL" });
        await dbContext.SaveChangesAsync();
        var client = new FakeExamToolsClient();
        client.SessionsFor(team.Id).Add(PendingSession());

        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

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

        var retryResult = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);
        Assert.Equal(1, retryResult.SessionsAdded);
        Assert.Single(dbContext.Sessions);
    }

    [Fact]
    public async Task NewApplicants_AreInsertedWithMappedFields()
    {
        await using var dbContext = CreateContext();
        await SeedVecAndFeeConfigAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        var client = new FakeExamToolsClient();
        client.SessionsFor(team.Id).Add(PendingSession(applicantCount: 2));
        client.ApplicantsFor(team.Id)["session-1"] =
        [
            Applicant(),
            Applicant(id: "applicant-2", first: "Tomasina", last: "Susanna", email: "tomasina@example.com", frn: "0000000000")
        ];

        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

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
        var team = await SeedTeamAsync(dbContext);
        var client = new FakeExamToolsClient();
        client.SessionsFor(team.Id).Add(PendingSession(applicantCount: 1));
        client.ApplicantsFor(team.Id)["session-1"] = [Applicant()];
        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        client.ApplicantsFor(team.Id)["session-1"] = [Applicant(email: "new-address@example.com")];
        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

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
        var team = await SeedTeamAsync(dbContext);
        var client = new FakeExamToolsClient();
        client.SessionsFor(team.Id).Add(PendingSession(applicantCount: 2));
        client.ApplicantsFor(team.Id)["session-1"] = [Applicant(), Applicant(id: "applicant-2", email: "second@example.com")];
        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        var purged = dbContext.Candidates.Single(c => c.ExamToolsApplicantId == "applicant-1");
        purged.Name = null;
        purged.Email = null;
        purged.PiiPurgedUtc = Now;
        var terminal = dbContext.Candidates.Single(c => c.ExamToolsApplicantId == "applicant-2");
        terminal.ApplicationStatus = CandidateApplicationStatus.Granted;
        await dbContext.SaveChangesAsync();

        client.ApplicantsFor(team.Id)["session-1"] =
        [
            Applicant(email: "resurrected@example.com"),
            Applicant(id: "applicant-2", email: "changed@example.com")
        ];
        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.CandidatesUpdated);
        Assert.Null(dbContext.Candidates.Single(c => c.ExamToolsApplicantId == "applicant-1").Email);
        Assert.Equal("second@example.com", dbContext.Candidates.Single(c => c.ExamToolsApplicantId == "applicant-2").Email);
    }

    [Fact]
    public async Task FrnPlaceholderInFeed_DoesNotOverwriteManuallyEnteredFrn()
    {
        await using var dbContext = CreateContext();
        await SeedVecAndFeeConfigAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        var client = new FakeExamToolsClient();
        client.SessionsFor(team.Id).Add(PendingSession(applicantCount: 1));
        client.ApplicantsFor(team.Id)["session-1"] = [Applicant(frn: "0000000000")];
        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        // Session Manager fills the FRN in manually later (spec allows testing without one initially).
        dbContext.Candidates.Single().Frn = "0099999999";
        await dbContext.SaveChangesAsync();

        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Equal("0099999999", dbContext.Candidates.Single().Frn);
    }

    [Fact]
    public async Task Reschedule_WithNoBlockingCandidates_IsAppliedAutomatically()
    {
        await using var dbContext = CreateContext();
        await SeedVecAndFeeConfigAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        var client = new FakeExamToolsClient();
        client.SessionsFor(team.Id).Add(PendingSession());
        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        var newStart = SessionStart.AddDays(7);
        client.SessionsFor(team.Id)[0] = PendingSession(date: newStart);
        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

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
        var team = await SeedTeamAsync(dbContext);
        var client = new FakeExamToolsClient();
        client.SessionsFor(team.Id).Add(PendingSession(applicantCount: 1));
        client.ApplicantsFor(team.Id)["session-1"] = [Applicant()];
        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        // A withdrawn/no-show candidate is terminal and should not block an automatic reschedule.
        dbContext.Candidates.Single().ApplicationStatus = CandidateApplicationStatus.NotTested;
        await dbContext.SaveChangesAsync();

        var newStart = SessionStart.AddDays(7);
        client.SessionsFor(team.Id)[0] = PendingSession(date: newStart, applicantCount: 1);
        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Equal(1, result.SessionsRescheduled);
        Assert.Equal(newStart, dbContext.Sessions.Single().ScheduledStartUtc);
    }

    [Fact]
    public async Task Reschedule_WithRegisteredCandidates_FlagsOnceAndKeepsStoredTime()
    {
        await using var dbContext = CreateContext();
        await SeedVecAndFeeConfigAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        var client = new FakeExamToolsClient();
        client.SessionsFor(team.Id).Add(PendingSession(applicantCount: 1));
        client.ApplicantsFor(team.Id)["session-1"] = [Applicant()];
        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        var newStart = SessionStart.AddDays(7);
        client.SessionsFor(team.Id)[0] = PendingSession(date: newStart, applicantCount: 1);
        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

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
        var repollResult = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);
        Assert.Equal(0, repollResult.SessionsFlaggedForReview);
        Assert.Single(dbContext.AuditLogs);
    }

    [Fact]
    public async Task SessionMissingFromFeed_IsMarkedCancelled()
    {
        await using var dbContext = CreateContext();
        await SeedVecAndFeeConfigAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        var client = new FakeExamToolsClient();
        client.SessionsFor(team.Id).Add(PendingSession());
        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        client.SessionsFor(team.Id).Clear();
        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Equal(1, result.SessionsCancelled);
        var session = Assert.Single(dbContext.Sessions);
        Assert.Equal(SessionStatus.Cancelled, session.Status);
        Assert.Equal(Now, session.CancelledUtc);

        // A second poll must not "re-cancel" (CancelledUtc stays put) or count it again.
        var repollResult = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);
        Assert.Equal(0, repollResult.SessionsCancelled);
    }

    [Fact]
    public async Task CompletedSessionMissingFromFeed_IsNotCancelled()
    {
        await using var dbContext = CreateContext();
        await SeedVecAndFeeConfigAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        var client = new FakeExamToolsClient();
        client.SessionsFor(team.Id).Add(PendingSession());
        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        var session = dbContext.Sessions.Single();
        session.TestingCompletedUtc = Now;
        await dbContext.SaveChangesAsync();

        client.SessionsFor(team.Id).Clear();
        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.SessionsCancelled);
        Assert.Equal(SessionStatus.Active, dbContext.Sessions.Single().Status);
    }

    [Fact]
    public async Task ApplicantFetch_IsSkippedWhenFeedShowsZeroAndNoLocalCandidates()
    {
        await using var dbContext = CreateContext();
        await SeedVecAndFeeConfigAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        var client = new FakeExamToolsClient();
        client.SessionsFor(team.Id).Add(PendingSession(applicantCount: 0));

        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Empty(client.ApplicantFetches);
    }

    // ---- Multi-team ----

    [Fact]
    public async Task TwoTeams_ShareTheGlobalVec_ButSessionsGetDistinctTeamId()
    {
        await using var dbContext = CreateContext();
        await SeedVecAndFeeConfigAsync(dbContext); // one shared "ARRL" Vec/FeeConfiguration
        var teamA = await SeedTeamAsync(dbContext, "TEAMA");
        var teamB = await SeedTeamAsync(dbContext, "TEAMB");
        var client = new FakeExamToolsClient();
        client.SessionsFor(teamA.Id).Add(PendingSession(id: "teamA-session"));
        client.SessionsFor(teamB.Id).Add(PendingSession(id: "teamB-session"));

        await CreateService(dbContext, client).RunAsync(teamA, CancellationToken.None);
        await CreateService(dbContext, client).RunAsync(teamB, CancellationToken.None);

        Assert.Equal(2, dbContext.Sessions.Count());
        var sessionA = dbContext.Sessions.Single(s => s.ExamToolsSessionId == "teamA-session");
        var sessionB = dbContext.Sessions.Single(s => s.ExamToolsSessionId == "teamB-session");
        Assert.Equal(teamA.Id, sessionA.TeamId);
        Assert.Equal(teamB.Id, sessionB.TeamId);
        // Both resolved to the same shared Vec — VECs are global, not per-team (see docs/multi-team.md).
        Assert.Equal(sessionA.VecId, sessionB.VecId);
        var vec = Assert.Single(dbContext.Vecs);
        Assert.Equal(vec.Id, sessionA.VecId);
    }

    [Fact]
    public async Task TeamBIngestion_NeverCancelsTeamAsStillActiveSessions()
    {
        // A naive "load every Session, diff against this team's feed" would wrongly see Team A's
        // session as "disappeared" from Team B's feed and cancel it. RunAsync scopes its local
        // session lookup to the team being ingested to prevent exactly this.
        await using var dbContext = CreateContext();
        await SeedVecAndFeeConfigAsync(dbContext);
        var teamA = await SeedTeamAsync(dbContext, "TEAMA");
        var teamB = await SeedTeamAsync(dbContext, "TEAMB");
        var client = new FakeExamToolsClient();
        client.SessionsFor(teamA.Id).Add(PendingSession(id: "teamA-session"));
        await CreateService(dbContext, client).RunAsync(teamA, CancellationToken.None);

        // Team B has never seen "teamA-session" and has no sessions of its own this poll.
        var result = await CreateService(dbContext, client).RunAsync(teamB, CancellationToken.None);

        Assert.Equal(0, result.SessionsCancelled);
        Assert.Equal(SessionStatus.Active, dbContext.Sessions.Single(s => s.ExamToolsSessionId == "teamA-session").Status);
    }

    [Fact]
    public async Task UnconfiguredTeam_SkipsIngestion_NeverCallsClient()
    {
        await using var dbContext = CreateContext();
        var team = new Team { Name = "Unconfigured Team", CreatedUtc = Now }; // no ExamTools credentials set
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        var client = new FakeExamToolsClient();

        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.SessionsAdded);
        Assert.Empty(client.CredentialsUsed);
        Assert.Empty(dbContext.Sessions);
    }
}
