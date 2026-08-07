using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.ExamTools;
using VeSessionManager.Core.VolunteerExaminers;
using Xunit;

namespace VeSessionManager.Core.Tests;

public class VolunteerExaminerSyncServiceTests
{
    private static readonly DateTime Now = new(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FakeExamToolsClient : IExamToolsClient
    {
        public Dictionary<(int TeamId, string SessionId), List<ExamToolsVe>> RostersByTeamAndSession { get; } = [];
        public HashSet<string> FailingSessionIds { get; } = [];
        public List<string> RosterFetches { get; } = [];
        public List<ExamToolsCredentials> CredentialsUsed { get; } = [];

        public void SetRoster(int teamId, string sessionId, params ExamToolsVe[] ves) =>
            RostersByTeamAndSession[(teamId, sessionId)] = [.. ves];

        public Task<IReadOnlyList<ExamToolsSession>> GetTeamSessionsAsync(ExamToolsCredentials credentials, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by VolunteerExaminerSyncService — it reads sessions from the local DB, not the feed.");

        public Task<IReadOnlyList<ExamToolsSession>> GetTeamClosedSessionsAsync(ExamToolsCredentials credentials, DateOnly startDateUtc, DateOnly endDateUtc, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by VolunteerExaminerSyncService — it reads sessions from the local DB, not the feed.");

        public Task<IReadOnlyList<ExamToolsApplicant>> GetSessionApplicantsAsync(ExamToolsCredentials credentials, string examToolsSessionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by VolunteerExaminerSyncService.");

        public Task<IReadOnlyList<ExamToolsVe>> GetSessionVeRosterAsync(ExamToolsCredentials credentials, string examToolsSessionId, CancellationToken cancellationToken)
        {
            CredentialsUsed.Add(credentials);
            RosterFetches.Add(examToolsSessionId);
            if (FailingSessionIds.Contains(examToolsSessionId))
            {
                throw new InvalidOperationException($"Simulated ExamTools failure for session {examToolsSessionId}");
            }

            return Task.FromResult<IReadOnlyList<ExamToolsVe>>(
                RostersByTeamAndSession.TryGetValue((credentials.TeamId, examToolsSessionId), out var list) ? list : []);
        }

        public Task<ExamToolsApplicantDetail?> GetApplicantDetailAsync(ExamToolsCredentials credentials, string examToolsSessionId, string applicantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by VolunteerExaminerSyncService.");
    }

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static async Task<Team> SeedTeamAsync(AppDbContext dbContext, string teamCode = "TESTTEAM", bool examToolsConfigured = true)
    {
        var team = new Team
        {
            Name = teamCode,
            ExamToolsTeamCode = examToolsConfigured ? teamCode : null,
            ExamToolsUsername = examToolsConfigured ? "testuser" : null,
            ExamToolsPassword = examToolsConfigured ? "testpass" : null,
            CreatedUtc = Now
        };
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        return team;
    }

    private static async Task<Session> SeedSessionAsync(AppDbContext dbContext, Team team, string examToolsSessionId = "session-1", SessionStatus status = SessionStatus.Active, DateTime? scheduledStartUtc = null)
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
            ExamToolsSessionId = examToolsSessionId,
            Title = "Test Session",
            ScheduledStartUtc = scheduledStartUtc ?? new DateTime(2026, 7, 24, 17, 0, 0, DateTimeKind.Utc),
            Team = team,
            Vec = vec,
            FeeConfiguration = feeConfiguration,
            Status = status,
            CreatedUtc = Now
        };
        dbContext.Sessions.Add(session);
        await dbContext.SaveChangesAsync();
        return session;
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private static VolunteerExaminerSyncService CreateService(AppDbContext dbContext, FakeExamToolsClient client) =>
        new(dbContext, client, Options.Create(new ExamToolsOptions()), new FixedTimeProvider(Now), NullLogger<VolunteerExaminerSyncService>.Instance);

    [Fact]
    public async Task NewVe_IsCreated_AndLinkedToSession()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, team);
        var client = new FakeExamToolsClient();
        client.SetRoster(team.Id, session.ExamToolsSessionId, new ExamToolsVe { Call = "n2spg", Name = "Test VE" });

        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Equal(1, result.VolunteerExaminersAdded);
        Assert.Equal(1, result.LinksAdded);
        var ve = Assert.Single(dbContext.VolunteerExaminers);
        Assert.Equal("N2SPG", ve.CallSign); // normalized upper-invariant
        Assert.Equal("Test VE", ve.Name);
        // The person carries no team; the membership does.
        var membership = Assert.Single(dbContext.VeTeamMemberships);
        Assert.Equal(team.Id, membership.TeamId);
        Assert.Equal(ve.Id, membership.VolunteerExaminerId);
        Assert.True(membership.IsActive);
        var link = Assert.Single(dbContext.SessionVolunteerExaminers);
        Assert.Equal(session.Id, link.SessionId);
        Assert.Equal(ve.Id, link.VolunteerExaminerId);
    }

    [Fact]
    public async Task ExistingVeMatchedByCallSign_IsReused_NotDuplicated()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var sessionA = await SeedSessionAsync(dbContext, team, "session-a");
        var sessionB = await SeedSessionAsync(dbContext, team, "session-b");
        var client = new FakeExamToolsClient();
        client.SetRoster(team.Id, sessionA.ExamToolsSessionId, new ExamToolsVe { Call = "N2SPG", Name = "Test VE" });
        client.SetRoster(team.Id, sessionB.ExamToolsSessionId, new ExamToolsVe { Call = "N2SPG", Name = "Test VE" });

        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Equal(1, result.VolunteerExaminersAdded); // one VE row, even though referenced by two sessions in the same run
        Assert.Equal(2, result.LinksAdded);
        Assert.Single(dbContext.VolunteerExaminers);
        Assert.Equal(2, dbContext.SessionVolunteerExaminers.Count());
    }

    /// <summary>
    /// Inverted by issue #142, and this is the trap it exists to hold shut. The sync used to
    /// re-apply ExamTools' name on every poll, justified in a comment saying nothing in the app
    /// could edit it — true at the time. #142 gives admins and the VEs themselves an edit screen, so
    /// the same code would now silently undo their corrections within the hour. ExamTools seeds the
    /// name once and owns nothing about the person afterwards.
    /// </summary>
    [Fact]
    public async Task VeNameChangedUpstream_DoesNotOverwriteTheStoredName()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, team);
        var client = new FakeExamToolsClient();
        client.SetRoster(team.Id, session.ExamToolsSessionId, new ExamToolsVe { Call = "N2SPG", Name = "Old Name" });
        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        client.SetRoster(team.Id, session.ExamToolsSessionId, new ExamToolsVe { Call = "N2SPG", Name = "New Name" });
        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.VolunteerExaminersAdded);
        Assert.Equal(0, result.LinksAdded); // already linked from the first run
        Assert.Equal("Old Name", dbContext.VolunteerExaminers.Single().Name);
    }

    [Fact]
    public async Task VeRemovedFromRoster_LinkIsRemoved_ButVolunteerExaminerRowStays()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, team);
        var client = new FakeExamToolsClient();
        client.SetRoster(team.Id, session.ExamToolsSessionId,
            new ExamToolsVe { Call = "N2SPG", Name = "Stays" },
            new ExamToolsVe { Call = "NP2UU", Name = "Removed" });
        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);
        Assert.Equal(2, dbContext.SessionVolunteerExaminers.Count());

        client.SetRoster(team.Id, session.ExamToolsSessionId, new ExamToolsVe { Call = "N2SPG", Name = "Stays" });
        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Equal(1, result.LinksRemoved);
        var link = Assert.Single(dbContext.SessionVolunteerExaminers);
        Assert.Equal("N2SPG", link.VolunteerExaminer.CallSign);
        // The VE record itself isn't deleted — just unlinked from this session.
        Assert.Equal(2, dbContext.VolunteerExaminers.Count());
    }

    [Fact]
    public async Task CancelledSession_IsNeverResynced()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, team, status: SessionStatus.Cancelled);
        var client = new FakeExamToolsClient();
        client.SetRoster(team.Id, session.ExamToolsSessionId, new ExamToolsVe { Call = "N2SPG", Name = "Test VE" });

        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.LinksAdded);
        Assert.Empty(client.RosterFetches);
        Assert.Empty(dbContext.VolunteerExaminers);
    }

    [Fact]
    public async Task UnconfiguredTeam_SkipsSync_NeverCallsClient()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, examToolsConfigured: false);
        await SeedSessionAsync(dbContext, team);
        var client = new FakeExamToolsClient();

        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.LinksAdded);
        Assert.Empty(client.CredentialsUsed);
    }

    [Fact]
    public async Task RosterEntryMissingCallSign_IsSkipped()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, team);
        var client = new FakeExamToolsClient();
        client.SetRoster(team.Id, session.ExamToolsSessionId, new ExamToolsVe { Call = "", Name = "No Callsign" });

        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.LinksAdded);
        Assert.Empty(dbContext.VolunteerExaminers);
    }

    /// <summary>
    /// Inverted by issue #142. This used to assert two rows, one per team — which is exactly the
    /// duplication that made contact details, tags and accreditations impossible to hold: the same
    /// human existed twice with nothing linking the two halves. One person, two memberships now.
    /// </summary>
    [Fact]
    public async Task TwoTeams_SameCallSign_AreOnePersonWithTwoMemberships()
    {
        await using var dbContext = CreateContext();
        var teamA = await SeedTeamAsync(dbContext, "TEAMA");
        var teamB = await SeedTeamAsync(dbContext, "TEAMB");
        var sessionA = await SeedSessionAsync(dbContext, teamA, "session-a");
        var sessionB = await SeedSessionAsync(dbContext, teamB, "session-b");
        var client = new FakeExamToolsClient();
        client.SetRoster(teamA.Id, sessionA.ExamToolsSessionId, new ExamToolsVe { Call = "N2SPG", Name = "Team A's VE" });
        client.SetRoster(teamB.Id, sessionB.ExamToolsSessionId, new ExamToolsVe { Call = "N2SPG", Name = "Team B's VE" });

        await CreateService(dbContext, client).RunAsync(teamA, CancellationToken.None);
        await CreateService(dbContext, client).RunAsync(teamB, CancellationToken.None);

        var ve = Assert.Single(dbContext.VolunteerExaminers);
        Assert.Equal(2, dbContext.VeTeamMemberships.Count());
        Assert.Contains(dbContext.VeTeamMemberships, m => m.TeamId == teamA.Id && m.VolunteerExaminerId == ve.Id);
        Assert.Contains(dbContext.VeTeamMemberships, m => m.TeamId == teamB.Id && m.VolunteerExaminerId == ve.Id);

        // Team A saw them first, so Team A's spelling of the name is the one that seeded the row —
        // and Team B's roster does NOT overwrite it. Name is app-owned after creation.
        Assert.Equal("Team A's VE", ve.Name);
    }

    [Fact]
    public async Task OneSessionFailing_DoesNotBlockOtherSessionsInSameRun_AndSavesEachIndependently()
    {
        // Same reasoning as every other scan-based service's per-item try/catch + save: one
        // session's ExamTools call throwing must not skip every later session in this team's list,
        // nor discard reconciliation already done for an earlier one.
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var sessionA = await SeedSessionAsync(dbContext, team, "session-a");
        var sessionB = await SeedSessionAsync(dbContext, team, "session-b");
        var client = new FakeExamToolsClient();
        client.SetRoster(team.Id, sessionA.ExamToolsSessionId, new ExamToolsVe { Call = "N2SPG", Name = "Team A's VE" });
        client.FailingSessionIds.Add(sessionA.ExamToolsSessionId);
        client.SetRoster(team.Id, sessionB.ExamToolsSessionId, new ExamToolsVe { Call = "NP2UU", Name = "Team B's VE" });

        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        // sessionA's failure doesn't throw out of RunAsync, and sessionB is still processed.
        Assert.Equal(1, result.LinksAdded);
        var ve = Assert.Single(dbContext.VolunteerExaminers);
        Assert.Equal("NP2UU", ve.CallSign);
        var link = Assert.Single(dbContext.SessionVolunteerExaminers);
        Assert.Equal(sessionB.Id, link.SessionId);
    }

    [Fact]
    public async Task Repoll_WithNoChanges_IsIdempotent()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, team);
        var client = new FakeExamToolsClient();
        client.SetRoster(team.Id, session.ExamToolsSessionId, new ExamToolsVe { Call = "N2SPG", Name = "Test VE" });
        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.VolunteerExaminersAdded);
        Assert.Equal(0, result.VolunteerExaminersUpdated);
        Assert.Equal(0, result.LinksAdded);
        Assert.Equal(0, result.LinksRemoved);
        Assert.Single(dbContext.VolunteerExaminers);
        Assert.Single(dbContext.SessionVolunteerExaminers);
    }

    /// <summary>
    /// The whole point of the final-poll rule, in one test. The app polls hourly, so a VE recorded at
    /// ExamTools between the last mid-session poll and the close was never seen — and since the
    /// manual "+ Add VE" was removed (2026-08-07) ExamTools is the only route in. One more fetch
    /// after the session is finished captures it; the fetch after that never happens.
    /// </summary>
    [Fact]
    public async Task ClosedSession_IsPolledExactlyOnceMore_AndPicksUpALateVe()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, team);   // still in the future: not finished
        var client = new FakeExamToolsClient();
        client.SetRoster(team.Id, session.ExamToolsSessionId, new ExamToolsVe { Call = "N2SPG", Name = "Test VE" });

        // Mid-session poll: roster stored, but nothing is final yet.
        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);
        Assert.Single(client.RosterFetches);
        Assert.Null((await dbContext.Sessions.SingleAsync()).VeRosterFinalSyncedUtc);

        // The session closes, and only then does a second VE appear on ExamTools' roster.
        session.ExamToolsClosedUtc = Now;
        await dbContext.SaveChangesAsync();
        client.SetRoster(team.Id, session.ExamToolsSessionId,
            new ExamToolsVe { Call = "N2SPG", Name = "Test VE" },
            new ExamToolsVe { Call = "KA0MVW", Name = "Late VE" });

        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Equal(2, client.RosterFetches.Count);
        Assert.Equal(1, result.LinksAdded);
        Assert.Equal(1, result.SessionsFinalised);
        Assert.Equal(2, dbContext.SessionVolunteerExaminers.Count());
        Assert.Equal(Now, (await dbContext.Sessions.SingleAsync()).VeRosterFinalSyncedUtc);

        // ...and that is the last word on this session.
        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);
        Assert.Equal(2, client.RosterFetches.Count);
    }

    /// <summary>
    /// A fetch that throws must not stamp the session — otherwise one transient ExamTools error on
    /// the single final poll would freeze that roster permanently.
    /// </summary>
    [Fact]
    public async Task FinalPollThatFails_DoesNotFinalise_AndIsRetried()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, team, scheduledStartUtc: Now.AddDays(-1));
        session.ExamToolsClosedUtc = Now;
        await dbContext.SaveChangesAsync();

        var client = new FakeExamToolsClient();
        client.FailingSessionIds.Add(session.ExamToolsSessionId);
        client.SetRoster(team.Id, session.ExamToolsSessionId, new ExamToolsVe { Call = "N2SPG", Name = "Test VE" });

        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);
        Assert.Null((await dbContext.Sessions.SingleAsync()).VeRosterFinalSyncedUtc);

        // ExamTools recovers; the retry lands and finalises.
        client.FailingSessionIds.Clear();
        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Equal(2, client.RosterFetches.Count);
        Assert.Equal(1, result.LinksAdded);
        Assert.Equal(Now, (await dbContext.Sessions.SingleAsync()).VeRosterFinalSyncedUtc);
    }

    [Fact]
    public async Task SessionAManagerMarkedCompleted_IsPolledOnceMore_ThenSettles()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, team);
        var client = new FakeExamToolsClient();
        client.SetRoster(team.Id, session.ExamToolsSessionId, new ExamToolsVe { Call = "N2SPG", Name = "Test VE" });
        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        session.TestingCompletedUtc = Now;
        await dbContext.SaveChangesAsync();
        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);
        Assert.Equal(2, client.RosterFetches.Count);

        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);
        Assert.Equal(2, client.RosterFetches.Count);
    }

    [Fact]
    public async Task SessionWithNeitherClosedStamp_IsRetiredByHasEnded_AfterItsFinalPoll()
    {
        // The backstop case: sessions ingested before ExamToolsClosedUtc existed carry neither
        // stamp and never will, so HasEnded is the only thing that can make them finished.
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, team);
        var client = new FakeExamToolsClient();
        client.SetRoster(team.Id, session.ExamToolsSessionId, new ExamToolsVe { Call = "N2SPG", Name = "Test VE" });

        // First sync happens while the session is still in the future, and stores the roster.
        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);
        Assert.Single(client.RosterFetches);

        // Once it has ended it takes its one final poll, and nothing after that.
        var afterSession = new FixedTimeProvider(session.ScheduledStartUtc.AddDays(1));
        VolunteerExaminerSyncService LaterService() => new(
            dbContext, client, Options.Create(new ExamToolsOptions()), afterSession,
            NullLogger<VolunteerExaminerSyncService>.Instance);

        await LaterService().RunAsync(team, CancellationToken.None);
        Assert.Equal(2, client.RosterFetches.Count);

        await LaterService().RunAsync(team, CancellationToken.None);
        Assert.Equal(2, client.RosterFetches.Count);
    }

    [Fact]
    public async Task FinishedSessionThatWasNeverFinalised_IsStillRetried()
    {
        // A session already past when this app first saw it has no stamp, so its final poll is still
        // owed — that is what stops a session which appeared and closed inside one polling interval
        // from losing its roster permanently.
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, team);
        var client = new FakeExamToolsClient();
        client.SetRoster(team.Id, session.ExamToolsSessionId, new ExamToolsVe { Call = "N2SPG", Name = "Test VE" });

        var afterSession = new FixedTimeProvider(session.ScheduledStartUtc.AddDays(30));
        var service = new VolunteerExaminerSyncService(
            dbContext, client, Options.Create(new ExamToolsOptions()), afterSession,
            NullLogger<VolunteerExaminerSyncService>.Instance);
        var result = await service.RunAsync(team, CancellationToken.None);

        Assert.Single(client.RosterFetches);
        Assert.Equal(1, result.LinksAdded);
    }

    /// <summary>
    /// A finished session whose roster ExamTools cannot serve must eventually stop being retried.
    /// Real case (2026-08-01): session 819 / 6567ff0cfb29450af7ba19da, a 2023 session pulled in by
    /// the historical import, returned HTTP 500 for its roster on every attempt — so it never got a
    /// roster, never settled, and logged a failed API call every hour indefinitely.
    /// </summary>
    [Fact]
    public async Task FinishedSessionOlderThanTheRetryWindow_WithNoRoster_IsNotRePolled()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedSessionAsync(dbContext, team, scheduledStartUtc: Now - VolunteerExaminerSyncService.RosterRetryWindow.Add(TimeSpan.FromDays(1)));
        var client = new FakeExamToolsClient();

        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Empty(client.RosterFetches);
    }

    /// <summary>
    /// The other half: a recently-finished session with no roster must still be retried, or a
    /// session that appeared and closed inside one polling interval loses its roster permanently.
    /// That is the behaviour the 2026-07-31 fix deliberately preserved.
    /// </summary>
    [Fact]
    public async Task FinishedSessionInsideTheRetryWindow_WithNoRoster_IsStillRePolled()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedSessionAsync(dbContext, team, scheduledStartUtc: Now.AddDays(-2));
        var client = new FakeExamToolsClient();

        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Single(client.RosterFetches);
    }

    /// <summary>
    /// The onlySessionId filter (session-scoped Detail-page refresh, 2026-08-03): with two eligible
    /// sessions, only the named session's roster is fetched and linked; the other waits for the
    /// Worker's next team-wide tick.
    /// </summary>
    [Fact]
    public async Task RunAsync_WithOnlySessionId_SyncsOnlyThatSessionsRoster()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var sessionA = await SeedSessionAsync(dbContext, team, "session-a");
        var sessionB = await SeedSessionAsync(dbContext, team, "session-b");
        var client = new FakeExamToolsClient();
        client.SetRoster(team.Id, sessionA.ExamToolsSessionId, new ExamToolsVe { Call = "N2SPG", Name = "Session A's VE" });
        client.SetRoster(team.Id, sessionB.ExamToolsSessionId, new ExamToolsVe { Call = "NP2UU", Name = "Session B's VE" });

        var result = await CreateService(dbContext, client).RunAsync(team, CancellationToken.None, sessionA.Id);

        Assert.Equal("session-a", Assert.Single(client.RosterFetches));
        Assert.Equal(1, result.LinksAdded);
        var ve = Assert.Single(dbContext.VolunteerExaminers);
        Assert.Equal("N2SPG", ve.CallSign);
        var link = Assert.Single(dbContext.SessionVolunteerExaminers);
        Assert.Equal(sessionA.Id, link.SessionId);
    }

    // ---- The historical import's escape hatch (2026-08-07) --------------------------------------

    /// <summary>
    /// <b>The bug this fixes.</b> Every session a historical import creates is older than
    /// RosterRetryWindow by definition, so the settle rule removed all of them before a single roster
    /// was fetched — the import's own VE step was a guaranteed no-op for exactly the data it had just
    /// imported. Reported live: "I just did a history load but it didn't load the VEs."
    /// </summary>
    [Fact]
    public async Task OldFinishedSession_WithIgnoreRetryWindow_IsStillPolledForItsRoster()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedSessionAsync(dbContext, team, scheduledStartUtc: Now - VolunteerExaminerSyncService.RosterRetryWindow.Add(TimeSpan.FromDays(365)));
        var client = new FakeExamToolsClient();

        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None, onlySessionId: null, ignoreRetryWindow: true);

        Assert.Single(client.RosterFetches);
    }

    /// <summary>
    /// The escape hatch does not disable the other half of the settle rule: a session that has
    /// already taken its final post-close poll is still skipped, so re-running an import does not
    /// re-fetch what it already has.
    /// </summary>
    [Fact]
    public async Task OldFinishedSession_AlreadyFinalised_IsStillSkipped_EvenWithIgnoreRetryWindow()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, team, scheduledStartUtc: Now - VolunteerExaminerSyncService.RosterRetryWindow.Add(TimeSpan.FromDays(365)));
        var ve = new VolunteerExaminer { Name = "Existing VE", CallSign = "N0CALL" };
        dbContext.SessionVolunteerExaminers.Add(new SessionVolunteerExaminer { Session = session, VolunteerExaminer = ve });
        session.VeRosterFinalSyncedUtc = Now.AddDays(-1);
        await dbContext.SaveChangesAsync();
        var client = new FakeExamToolsClient();

        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None, onlySessionId: null, ignoreRetryWindow: true);

        Assert.Empty(client.RosterFetches);
    }

    /// <summary>
    /// And the routine path is unchanged — the window still protects the hourly sync from re-polling
    /// a 2023 session whose roster ExamTools will never serve.
    /// </summary>
    [Fact]
    public async Task RoutinePath_StillHonoursTheRetryWindow()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedSessionAsync(dbContext, team, scheduledStartUtc: Now - VolunteerExaminerSyncService.RosterRetryWindow.Add(TimeSpan.FromDays(1)));
        var client = new FakeExamToolsClient();

        await CreateService(dbContext, client).RunAsync(team, CancellationToken.None);

        Assert.Empty(client.RosterFetches);
    }
}
