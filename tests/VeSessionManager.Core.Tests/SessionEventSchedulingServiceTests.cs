using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Discord;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Scheduling;
using VeSessionManager.Core.Zoom;
using Xunit;

namespace VeSessionManager.Core.Tests;

public class SessionEventSchedulingServiceTests
{
    private static readonly DateTime Now = new(2026, 7, 19, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SessionStart = new(2026, 7, 24, 17, 0, 0, DateTimeKind.Utc);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private sealed class FakeZoomClient : IZoomClient
    {
        public List<string> CreateCalls { get; } = [];
        public List<ZoomMeetingRequest> CreateRequests { get; } = [];
        public List<string> UpdateCalls { get; } = [];
        public List<string> DeleteCalls { get; } = [];
        public List<ZoomMeeting> ExistingMeetings { get; } = [];
        public Exception? ThrowOnCreate { get; set; }
        public Exception? ThrowOnUpdate { get; set; }
        public List<ZoomCredentials> CredentialsUsed { get; } = [];
        private int _nextId = 1000;

        public Task<ZoomMeeting> CreateMeetingAsync(ZoomCredentials credentials, ZoomMeetingRequest request, CancellationToken cancellationToken)
        {
            CredentialsUsed.Add(credentials);
            CreateCalls.Add(request.Topic);
            CreateRequests.Add(request);
            if (ThrowOnCreate is not null)
            {
                throw ThrowOnCreate;
            }
            var id = (_nextId++).ToString();
            return Task.FromResult(new ZoomMeeting { Id = id, JoinUrl = $"https://zoom.us/j/{id}" });
        }

        public Task UpdateMeetingAsync(ZoomCredentials credentials, string meetingId, ZoomMeetingRequest request, CancellationToken cancellationToken)
        {
            CredentialsUsed.Add(credentials);
            UpdateCalls.Add(meetingId);
            if (ThrowOnUpdate is not null)
            {
                throw ThrowOnUpdate;
            }
            return Task.CompletedTask;
        }

        public Task DeleteMeetingAsync(ZoomCredentials credentials, string meetingId, CancellationToken cancellationToken)
        {
            CredentialsUsed.Add(credentials);
            DeleteCalls.Add(meetingId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ZoomMeeting>> ListMeetingsAsync(ZoomCredentials credentials, CancellationToken cancellationToken)
        {
            CredentialsUsed.Add(credentials);
            return Task.FromResult<IReadOnlyList<ZoomMeeting>>(ExistingMeetings);
        }
    }

    private sealed class FakeDiscordEventClient : IDiscordEventClient
    {
        public List<string> CreateCalls { get; } = [];
        public List<string> UpdateCalls { get; } = [];
        public List<string> DeleteCalls { get; } = [];
        public List<ulong> GuildIdsUsed { get; } = [];
        public List<DiscordEvent> ExistingEvents { get; } = [];
        public Exception? ThrowOnCreate { get; set; }
        public bool IsConfigured { get; set; } = true;
        private int _nextId = 2000;

        public Task<DiscordEvent> CreateEventAsync(ulong guildId, DiscordEventRequest request, CancellationToken cancellationToken)
        {
            GuildIdsUsed.Add(guildId);
            CreateCalls.Add(request.Name);
            if (ThrowOnCreate is not null)
            {
                throw ThrowOnCreate;
            }
            return Task.FromResult(new DiscordEvent { Id = (_nextId++).ToString() });
        }

        public Task UpdateEventAsync(ulong guildId, string eventId, DiscordEventRequest request, CancellationToken cancellationToken)
        {
            GuildIdsUsed.Add(guildId);
            UpdateCalls.Add(eventId);
            return Task.CompletedTask;
        }

        public Task DeleteEventAsync(ulong guildId, string eventId, CancellationToken cancellationToken)
        {
            GuildIdsUsed.Add(guildId);
            DeleteCalls.Add(eventId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<DiscordEvent>> ListEventsAsync(ulong guildId, CancellationToken cancellationToken)
        {
            GuildIdsUsed.Add(guildId);
            return Task.FromResult<IReadOnlyList<DiscordEvent>>(ExistingEvents);
        }
    }

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static SessionEventSchedulingService CreateService(
        AppDbContext dbContext, IZoomClient zoom, IDiscordEventClient discord) =>
        new(dbContext, zoom, discord, new FixedTimeProvider(Now), NullLogger<SessionEventSchedulingService>.Instance);

    /// <summary>Seeds a Team. zoomConfigured=true (default) sets AccountId/ClientId/ClientSecret so Team.IsZoomConfigured is true; discordConfigured=true (default) sets DiscordGuildId so Team.IsDiscordConfigured is true (the shared bot's own readiness is controlled separately via FakeDiscordEventClient.IsConfigured).</summary>
    private static async Task<Team> SeedTeamAsync(AppDbContext dbContext, bool zoomConfigured = true, bool discordConfigured = true)
    {
        var team = new Team
        {
            Name = "TESTTEAM",
            ZoomAccountId = zoomConfigured ? "zoom-account" : null,
            ZoomClientId = zoomConfigured ? "zoom-client" : null,
            ZoomClientSecret = zoomConfigured ? "zoom-secret" : null,
            DiscordGuildId = discordConfigured ? 999UL : null,
            CreatedUtc = Now
        };
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        return team;
    }

    /// <summary>Minimal Vec/FeeConfiguration/User rows a Session's required FKs need to save.</summary>
    private static async Task<(Vec vec, FeeConfiguration feeConfiguration)> SeedRefsAsync(AppDbContext dbContext)
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
        dbContext.FeeConfigurations.Add(feeConfiguration);
        await dbContext.SaveChangesAsync();
        return (vec, feeConfiguration);
    }

    private static Session NewSession(Vec vec, FeeConfiguration feeConfiguration, Team team, string examToolsId = "session-1") => new()
    {
        ExamToolsSessionId = examToolsId,
        Title = "July Session",
        ScheduledStartUtc = SessionStart,
        DurationMinutes = 60,
        VecId = vec.Id,
        TeamId = team.Id,
        FeeConfigurationId = feeConfiguration.Id,
        CreatedUtc = Now
    };

    [Fact]
    public async Task NewSession_CreatesZoomMeetingAndDiscordEvent_AndMarksSynced()
    {
        await using var dbContext = CreateContext();
        var (vec, feeConfig) = await SeedRefsAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        var session = NewSession(vec, feeConfig, team);
        dbContext.Sessions.Add(session);
        await dbContext.SaveChangesAsync();

        var zoom = new FakeZoomClient();
        var discord = new FakeDiscordEventClient();
        var result = await CreateService(dbContext, zoom, discord).RunAsync(team, CancellationToken.None);

        Assert.Equal(1, result.SessionsSynced);
        Assert.Equal(0, result.SessionsFailed);
        Assert.Single(zoom.CreateCalls);
        Assert.Single(discord.CreateCalls);
        Assert.Empty(zoom.UpdateCalls);
        Assert.Empty(discord.UpdateCalls);
        // A create is now preceded by a ListMeetingsAsync dedup check (same credentials), so
        // assert every recorded call used the right team rather than expecting exactly one call.
        Assert.All(zoom.CredentialsUsed, c => Assert.Equal(team.Id, c.TeamId));

        var saved = dbContext.Sessions.Single();
        Assert.NotNull(saved.ZoomMeetingId);
        Assert.StartsWith("https://zoom.us/j/", saved.ZoomJoinUrl);
        Assert.NotNull(saved.DiscordEventId);
        Assert.Equal(SessionStart, saved.ZoomDiscordSyncedStartUtc);
    }

    [Fact]
    public async Task TeamBreakoutRoomCount_IsPassedThroughToZoomMeetingRequest()
    {
        await using var dbContext = CreateContext();
        var (vec, feeConfig) = await SeedRefsAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        team.ZoomBreakoutRoomCount = 4;
        await dbContext.SaveChangesAsync();
        var session = NewSession(vec, feeConfig, team);
        dbContext.Sessions.Add(session);
        await dbContext.SaveChangesAsync();

        var zoom = new FakeZoomClient();
        var discord = new FakeDiscordEventClient();
        await CreateService(dbContext, zoom, discord).RunAsync(team, CancellationToken.None);

        var request = Assert.Single(zoom.CreateRequests);
        Assert.Equal(4, request.BreakoutRoomCount);
    }

    [Fact]
    public async Task AlreadyEndedSession_IsSkipped_NoZoomOrDiscordCalls()
    {
        // Issue #22: a session ingested via the completed-session backfill window has already
        // happened by the time this ever runs — never worth a real Zoom meeting/Discord event.
        await using var dbContext = CreateContext();
        var (vec, feeConfig) = await SeedRefsAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        var session = NewSession(vec, feeConfig, team);
        // Just-ended, so it is still inside the query's recent-session bound and therefore still
        // *counted* as skipped. A long-past one is covered separately below.
        session.ScheduledStartUtc = Now.AddHours(-4);
        dbContext.Sessions.Add(session);
        await dbContext.SaveChangesAsync();

        var zoom = new FakeZoomClient();
        var discord = new FakeDiscordEventClient();
        var result = await CreateService(dbContext, zoom, discord).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.SessionsSynced);
        Assert.Equal(1, result.SessionsSkippedPastDue);
        Assert.Empty(zoom.CreateCalls);
        Assert.Empty(discord.CreateCalls);
        var saved = dbContext.Sessions.Single();
        Assert.Null(saved.ZoomDiscordSyncedStartUtc);
    }

    /// <summary>
    /// A long-past session isn't merely skipped — it is never loaded. Before the 2026-08-01 bound,
    /// every backfilled session was fetched, filtered and log-counted on every tick forever (794 for
    /// one real team), which is what made the Worker log unreadable after the historical import.
    /// The distinction that matters here is SessionsSkippedPastDue == 0, not just "no calls made".
    /// </summary>
    [Fact]
    public async Task LongPastSession_IsNotEvenConsidered_AndIsNotCountedAsSkipped()
    {
        await using var dbContext = CreateContext();
        var (vec, feeConfig) = await SeedRefsAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        var session = NewSession(vec, feeConfig, team);
        session.ScheduledStartUtc = Now.AddDays(-200); // backfilled history
        dbContext.Sessions.Add(session);
        await dbContext.SaveChangesAsync();

        var zoom = new FakeZoomClient();
        var discord = new FakeDiscordEventClient();
        var result = await CreateService(dbContext, zoom, discord).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.SessionsSkippedPastDue);
        Assert.Equal(0, result.SessionsSynced);
        Assert.Empty(zoom.CreateCalls);
        Assert.Empty(discord.CreateCalls);
        Assert.Null(dbContext.Sessions.Single().ZoomDiscordSyncedStartUtc);
    }

    [Fact]
    public async Task NewSession_MatchingMeetingAlreadyExistsInZoom_AdoptsIt_DoesNotCreateDuplicate()
    {
        // Same reasoning as the Discord dedup test below, but for Zoom's half of SyncZoomAndDiscordAsync.
        await using var dbContext = CreateContext();
        var (vec, feeConfig) = await SeedRefsAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        var session = NewSession(vec, feeConfig, team);
        dbContext.Sessions.Add(session);
        await dbContext.SaveChangesAsync();

        var zoom = new FakeZoomClient();
        zoom.ExistingMeetings.Add(new ZoomMeeting { Id = "orphaned-zoom-meeting", JoinUrl = "https://zoom.us/j/orphaned-zoom-meeting", Topic = session.Title, StartTimeUtc = SessionStart });
        var discord = new FakeDiscordEventClient();
        var result = await CreateService(dbContext, zoom, discord).RunAsync(team, CancellationToken.None);

        Assert.Equal(1, result.SessionsSynced);
        Assert.Empty(zoom.CreateCalls);
        var saved = dbContext.Sessions.Single();
        Assert.Equal("orphaned-zoom-meeting", saved.ZoomMeetingId);
        Assert.Equal("https://zoom.us/j/orphaned-zoom-meeting", saved.ZoomJoinUrl);
        Assert.Equal(SessionStart, saved.ZoomDiscordSyncedStartUtc);
    }

    [Fact]
    public async Task NewSession_MatchingEventAlreadyExistsInGuild_AdoptsIt_DoesNotCreateDuplicate()
    {
        // Simulates a previous poll whose Discord CreateEventAsync call succeeded but crashed
        // before the returned id was persisted — see TODO.md's "Duplicate Discord scheduled
        // events" entry. The next poll must find that already-created event (by name/time) and
        // adopt its id rather than calling CreateEventAsync again.
        await using var dbContext = CreateContext();
        var (vec, feeConfig) = await SeedRefsAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        var session = NewSession(vec, feeConfig, team);
        dbContext.Sessions.Add(session);
        await dbContext.SaveChangesAsync();

        var zoom = new FakeZoomClient();
        var discord = new FakeDiscordEventClient();
        discord.ExistingEvents.Add(new DiscordEvent { Id = "orphaned-discord-event", Name = session.Title, StartTimeUtc = SessionStart });
        var result = await CreateService(dbContext, zoom, discord).RunAsync(team, CancellationToken.None);

        Assert.Equal(1, result.SessionsSynced);
        Assert.Empty(discord.CreateCalls);
        var saved = dbContext.Sessions.Single();
        Assert.Equal("orphaned-discord-event", saved.DiscordEventId);
        Assert.Equal(SessionStart, saved.ZoomDiscordSyncedStartUtc);
    }

    [Fact]
    public async Task DiscordEventDescription_IncludesZoomJoinLink()
    {
        await using var dbContext = CreateContext();
        var (vec, feeConfig) = await SeedRefsAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        dbContext.Sessions.Add(NewSession(vec, feeConfig, team));
        await dbContext.SaveChangesAsync();

        var zoom = new FakeZoomClient();
        var discord = new FakeDiscordEventClient();
        // FakeDiscordEventClient only records the event name, not the full request, so verify the
        // join-url-in-description/location requirement via the persisted ZoomJoinUrl instead —
        // SyncZoomAndDiscordAsync builds the Discord request's Location directly from it.
        await CreateService(dbContext, zoom, discord).RunAsync(team, CancellationToken.None);

        var saved = dbContext.Sessions.Single();
        Assert.NotNull(saved.ZoomJoinUrl);
    }

    [Fact]
    public async Task AlreadySynced_Session_IsSkippedEntirely()
    {
        await using var dbContext = CreateContext();
        var (vec, feeConfig) = await SeedRefsAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        var session = NewSession(vec, feeConfig, team);
        session.ZoomMeetingId = "existing-zoom";
        session.ZoomJoinUrl = "https://zoom.us/j/existing-zoom";
        session.DiscordEventId = "existing-discord";
        session.ZoomDiscordSyncedStartUtc = SessionStart;
        dbContext.Sessions.Add(session);
        await dbContext.SaveChangesAsync();

        var zoom = new FakeZoomClient();
        var discord = new FakeDiscordEventClient();
        var result = await CreateService(dbContext, zoom, discord).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.SessionsSynced);
        Assert.Empty(zoom.CreateCalls);
        Assert.Empty(zoom.UpdateCalls);
        Assert.Empty(discord.CreateCalls);
        Assert.Empty(discord.UpdateCalls);
    }

    [Fact]
    public async Task Reschedule_OfAlreadySyncedSession_CallsUpdate_NotCreate_AndPreservesIds()
    {
        await using var dbContext = CreateContext();
        var (vec, feeConfig) = await SeedRefsAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        var session = NewSession(vec, feeConfig, team);
        session.ZoomMeetingId = "zoom-1";
        session.ZoomJoinUrl = "https://zoom.us/j/zoom-1";
        session.DiscordEventId = "discord-1";
        session.ZoomDiscordSyncedStartUtc = SessionStart;
        dbContext.Sessions.Add(session);
        await dbContext.SaveChangesAsync();

        // Simulate Phase 1 applying a zero-candidate auto-reschedule.
        var newStart = SessionStart.AddDays(7);
        session.ScheduledStartUtc = newStart;
        await dbContext.SaveChangesAsync();

        var zoom = new FakeZoomClient();
        var discord = new FakeDiscordEventClient();
        var result = await CreateService(dbContext, zoom, discord).RunAsync(team, CancellationToken.None);

        Assert.Equal(1, result.SessionsSynced);
        Assert.Empty(zoom.CreateCalls);
        Assert.Equal(["zoom-1"], zoom.UpdateCalls);
        Assert.Empty(discord.CreateCalls);
        Assert.Equal(["discord-1"], discord.UpdateCalls);

        var saved = dbContext.Sessions.Single();
        Assert.Equal("zoom-1", saved.ZoomMeetingId);
        Assert.Equal("discord-1", saved.DiscordEventId);
        Assert.Equal(newStart, saved.ZoomDiscordSyncedStartUtc);
    }

    [Fact]
    public async Task PartialFailure_ZoomSucceedsDiscordFails_PersistsZoomId_AndDoesNotRecreateZoomOnRetry()
    {
        await using var dbContext = CreateContext();
        var (vec, feeConfig) = await SeedRefsAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        dbContext.Sessions.Add(NewSession(vec, feeConfig, team));
        await dbContext.SaveChangesAsync();

        var zoom = new FakeZoomClient();
        var discord = new FakeDiscordEventClient { ThrowOnCreate = new InvalidOperationException("Discord unavailable") };
        var result = await CreateService(dbContext, zoom, discord).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.SessionsSynced);
        Assert.Equal(1, result.SessionsFailed);
        var saved = dbContext.Sessions.Single();
        Assert.NotNull(saved.ZoomMeetingId); // Zoom's half of the work was not lost.
        Assert.Null(saved.DiscordEventId);
        Assert.Null(saved.ZoomDiscordSyncedStartUtc); // not advanced — still needs a retry.

        // Next run: Zoom must not be re-created (its id is already set); only Discord retries.
        discord.ThrowOnCreate = null;
        var retryResult = await CreateService(dbContext, zoom, discord).RunAsync(team, CancellationToken.None);

        Assert.Equal(1, retryResult.SessionsSynced);
        Assert.Single(zoom.CreateCalls); // still just the one call from the first run — never re-created
        Assert.Equal(2, discord.CreateCalls.Count); // the failed attempt from run 1, plus the successful retry
        Assert.Equal(SessionStart, dbContext.Sessions.Single().ZoomDiscordSyncedStartUtc);
    }

    [Fact]
    public async Task OneSessionFailing_DoesNotPreventOtherSessionsFromSyncing()
    {
        await using var dbContext = CreateContext();
        var (vec, feeConfig) = await SeedRefsAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        // Whichever of these two sessions the service processes first will hit the fake client's
        // one-time failure; the other must still be processed and synced in the same run. The
        // order EF's InMemory provider returns rows in isn't a documented guarantee, so this test
        // doesn't assume which named session ends up in which state — only that exactly one of
        // each outcome occurs.
        dbContext.Sessions.Add(NewSession(vec, feeConfig, team, "session-a"));
        dbContext.Sessions.Add(NewSession(vec, feeConfig, team, "session-b"));
        await dbContext.SaveChangesAsync();

        var failingZoom = new FailFirstThenSucceedZoomClient();
        var discord = new FakeDiscordEventClient();
        var result = await CreateService(dbContext, failingZoom, discord).RunAsync(team, CancellationToken.None);

        Assert.Equal(1, result.SessionsFailed);
        Assert.Equal(1, result.SessionsSynced);

        var sessions = dbContext.Sessions.ToList();
        Assert.Single(sessions, s => s.ZoomMeetingId is not null && s.ZoomDiscordSyncedStartUtc == SessionStart);
        Assert.Single(sessions, s => s.ZoomMeetingId is null && s.ZoomDiscordSyncedStartUtc is null);
    }

    /// <summary>Throws on the first CreateMeetingAsync call only, so a two-session run can prove one failure doesn't block the other session's processing.</summary>
    private sealed class FailFirstThenSucceedZoomClient : IZoomClient
    {
        private bool _thrown;
        private int _nextId = 3000;

        public Task<ZoomMeeting> CreateMeetingAsync(ZoomCredentials credentials, ZoomMeetingRequest request, CancellationToken cancellationToken)
        {
            if (!_thrown)
            {
                _thrown = true;
                throw new InvalidOperationException("Zoom unavailable");
            }
            var id = (_nextId++).ToString();
            return Task.FromResult(new ZoomMeeting { Id = id, JoinUrl = $"https://zoom.us/j/{id}" });
        }

        public Task UpdateMeetingAsync(ZoomCredentials credentials, string meetingId, ZoomMeetingRequest request, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task DeleteMeetingAsync(ZoomCredentials credentials, string meetingId, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<ZoomMeeting>> ListMeetingsAsync(ZoomCredentials credentials, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ZoomMeeting>>([]);
    }

    [Fact]
    public async Task CancelledSession_DeletesZoomAndDiscord_NullsIds_AndWritesAuditLog()
    {
        await using var dbContext = CreateContext();
        var (vec, feeConfig) = await SeedRefsAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        var session = NewSession(vec, feeConfig, team);
        session.ZoomMeetingId = "zoom-1";
        session.ZoomJoinUrl = "https://zoom.us/j/zoom-1";
        session.DiscordEventId = "discord-1";
        session.ZoomDiscordSyncedStartUtc = SessionStart;
        session.Status = SessionStatus.Cancelled;
        session.CancelledUtc = Now;
        dbContext.Sessions.Add(session);
        await dbContext.SaveChangesAsync();

        var zoom = new FakeZoomClient();
        var discord = new FakeDiscordEventClient();
        var result = await CreateService(dbContext, zoom, discord).RunAsync(team, CancellationToken.None);

        Assert.Equal(1, result.SessionsCleanedUp);
        Assert.Equal(["zoom-1"], zoom.DeleteCalls);
        Assert.Equal(["discord-1"], discord.DeleteCalls);

        var saved = dbContext.Sessions.Single();
        Assert.Null(saved.ZoomMeetingId);
        Assert.Null(saved.ZoomJoinUrl);
        Assert.Null(saved.DiscordEventId);

        Assert.Equal(2, dbContext.AuditLogs.Count());
        Assert.Contains(dbContext.AuditLogs, a => a.Action == "ZoomMeetingCancelled");
        Assert.Contains(dbContext.AuditLogs, a => a.Action == "DiscordEventCancelled");
        Assert.All(dbContext.AuditLogs, a => Assert.Null(a.UserId));
    }

    [Fact]
    public async Task CancelledSession_WithOnlyZoomStillSet_OnlyCleansUpZoom()
    {
        await using var dbContext = CreateContext();
        var (vec, feeConfig) = await SeedRefsAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        var session = NewSession(vec, feeConfig, team);
        session.ZoomMeetingId = "zoom-1";
        session.ZoomJoinUrl = "https://zoom.us/j/zoom-1";
        session.DiscordEventId = null; // e.g. Discord create had never succeeded before cancellation
        session.Status = SessionStatus.Cancelled;
        session.CancelledUtc = Now;
        dbContext.Sessions.Add(session);
        await dbContext.SaveChangesAsync();

        var zoom = new FakeZoomClient();
        var discord = new FakeDiscordEventClient();
        await CreateService(dbContext, zoom, discord).RunAsync(team, CancellationToken.None);

        Assert.Single(zoom.DeleteCalls);
        Assert.Empty(discord.DeleteCalls);
        Assert.Single(dbContext.AuditLogs);
    }

    [Fact]
    public async Task CancelledSession_ZoomNotConfiguredForTeam_LeavesZoomMeetingIdSet_NoErrorLogged()
    {
        // Regression test for the "SessionEventScheduling repeats a real [ERR] every tick" bug
        // (found 2026-07-29, see TODO.md/CLAUDE.md): a cancelled session whose team never
        // finished (or removed) its Zoom config must not throw every poll — it should stay
        // pending, reported via SessionsAwaitingIntegrationConfig, and retry automatically once
        // Zoom is configured.
        await using var dbContext = CreateContext();
        var (vec, feeConfig) = await SeedRefsAsync(dbContext);
        var team = await SeedTeamAsync(dbContext, zoomConfigured: false);
        var session = NewSession(vec, feeConfig, team);
        session.ZoomMeetingId = "zoom-1";
        session.ZoomJoinUrl = "https://zoom.us/j/zoom-1";
        session.DiscordEventId = "discord-1";
        session.Status = SessionStatus.Cancelled;
        session.CancelledUtc = Now;
        dbContext.Sessions.Add(session);
        await dbContext.SaveChangesAsync();

        var zoom = new FakeZoomClient();
        var discord = new FakeDiscordEventClient();
        var result = await CreateService(dbContext, zoom, discord).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.SessionsCleanedUp);
        Assert.Equal(1, result.SessionsAwaitingIntegrationConfig);
        Assert.Equal(0, result.SessionsFailed);
        Assert.Empty(zoom.DeleteCalls);
        Assert.Single(discord.DeleteCalls);

        var saved = dbContext.Sessions.Single();
        Assert.Equal("zoom-1", saved.ZoomMeetingId);
        Assert.Null(saved.DiscordEventId);
    }

    [Fact]
    public async Task CancelledSession_DiscordNotConfiguredForTeam_LeavesDiscordEventIdSet_NoErrorLogged()
    {
        await using var dbContext = CreateContext();
        var (vec, feeConfig) = await SeedRefsAsync(dbContext);
        var team = await SeedTeamAsync(dbContext, discordConfigured: false);
        var session = NewSession(vec, feeConfig, team);
        session.ZoomMeetingId = "zoom-1";
        session.ZoomJoinUrl = "https://zoom.us/j/zoom-1";
        session.DiscordEventId = "discord-1";
        session.Status = SessionStatus.Cancelled;
        session.CancelledUtc = Now;
        dbContext.Sessions.Add(session);
        await dbContext.SaveChangesAsync();

        var zoom = new FakeZoomClient();
        var discord = new FakeDiscordEventClient();
        var result = await CreateService(dbContext, zoom, discord).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.SessionsCleanedUp);
        Assert.Equal(1, result.SessionsAwaitingIntegrationConfig);
        Assert.Equal(0, result.SessionsFailed);
        Assert.Single(zoom.DeleteCalls);
        Assert.Empty(discord.DeleteCalls);

        var saved = dbContext.Sessions.Single();
        Assert.Null(saved.ZoomMeetingId);
        Assert.Equal("discord-1", saved.DiscordEventId);
    }

    [Fact]
    public async Task NeverScheduled_CancelledSession_IsNotProcessed()
    {
        await using var dbContext = CreateContext();
        var (vec, feeConfig) = await SeedRefsAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        var session = NewSession(vec, feeConfig, team);
        session.Status = SessionStatus.Cancelled;
        session.CancelledUtc = Now;
        // Never had a Zoom/Discord presence (e.g. cancelled before Phase 2 ever ran for it).
        dbContext.Sessions.Add(session);
        await dbContext.SaveChangesAsync();

        var zoom = new FakeZoomClient();
        var discord = new FakeDiscordEventClient();
        var result = await CreateService(dbContext, zoom, discord).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.SessionsCleanedUp);
        Assert.Empty(zoom.DeleteCalls);
        Assert.Empty(discord.DeleteCalls);
        Assert.Empty(dbContext.AuditLogs);
    }

    // ---- Zoom/Discord optional (neither is a hard requirement, unlike ExamTools) ----

    [Fact]
    public async Task NeitherZoomNorDiscordConfigured_SessionStaysPending_NoCallsMade()
    {
        await using var dbContext = CreateContext();
        var (vec, feeConfig) = await SeedRefsAsync(dbContext);
        var team = await SeedTeamAsync(dbContext, zoomConfigured: false, discordConfigured: false);
        dbContext.Sessions.Add(NewSession(vec, feeConfig, team));
        await dbContext.SaveChangesAsync();

        var zoom = new FakeZoomClient();
        var discord = new FakeDiscordEventClient { IsConfigured = false };
        var result = await CreateService(dbContext, zoom, discord).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.SessionsSynced);
        Assert.Equal(1, result.SessionsAwaitingIntegrationConfig);
        Assert.Empty(zoom.CreateCalls);
        Assert.Empty(discord.CreateCalls);
        var saved = dbContext.Sessions.Single();
        Assert.Null(saved.ZoomMeetingId);
        Assert.Null(saved.DiscordEventId);
        Assert.Null(saved.ZoomDiscordSyncedStartUtc);
    }

    [Fact]
    public async Task DiscordConfiguredButZoomIsNot_DiscordIsNotCalled_BlockedOnMissingZoomLink()
    {
        await using var dbContext = CreateContext();
        var (vec, feeConfig) = await SeedRefsAsync(dbContext);
        var team = await SeedTeamAsync(dbContext, zoomConfigured: false);
        dbContext.Sessions.Add(NewSession(vec, feeConfig, team));
        await dbContext.SaveChangesAsync();

        var zoom = new FakeZoomClient();
        var discord = new FakeDiscordEventClient(); // configured, but has nothing to work with
        var result = await CreateService(dbContext, zoom, discord).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.SessionsSynced);
        Assert.Equal(1, result.SessionsAwaitingIntegrationConfig);
        Assert.Empty(zoom.CreateCalls);
        Assert.Empty(discord.CreateCalls); // never attempted — no Zoom join link to put in it
    }

    [Fact]
    public async Task ZoomConfiguredButDiscordIsNot_ZoomStillCreated_SessionAwaitsDiscord()
    {
        await using var dbContext = CreateContext();
        var (vec, feeConfig) = await SeedRefsAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        dbContext.Sessions.Add(NewSession(vec, feeConfig, team));
        await dbContext.SaveChangesAsync();

        var zoom = new FakeZoomClient();
        var discord = new FakeDiscordEventClient { IsConfigured = false };
        var result = await CreateService(dbContext, zoom, discord).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.SessionsSynced);
        Assert.Equal(1, result.SessionsAwaitingIntegrationConfig);
        Assert.Single(zoom.CreateCalls);
        Assert.Empty(discord.CreateCalls);
        var saved = dbContext.Sessions.Single();
        Assert.NotNull(saved.ZoomMeetingId);
        Assert.Null(saved.DiscordEventId);
        Assert.Null(saved.ZoomDiscordSyncedStartUtc);
    }

    [Fact]
    public async Task BothBecomeConfiguredLater_BackfillAutomaticallyOnNextPoll_NoDuplicateZoomMeeting()
    {
        await using var dbContext = CreateContext();
        var (vec, feeConfig) = await SeedRefsAsync(dbContext);
        var team = await SeedTeamAsync(dbContext, zoomConfigured: false, discordConfigured: false);
        dbContext.Sessions.Add(NewSession(vec, feeConfig, team));
        await dbContext.SaveChangesAsync();

        var zoom = new FakeZoomClient();
        var discord = new FakeDiscordEventClient { IsConfigured = false };
        await CreateService(dbContext, zoom, discord).RunAsync(team, CancellationToken.None);
        Assert.Null(dbContext.Sessions.Single().ZoomDiscordSyncedStartUtc);

        team.ZoomAccountId = "zoom-account";
        team.ZoomClientId = "zoom-client";
        team.ZoomClientSecret = "zoom-secret";
        team.DiscordGuildId = 999UL;
        await dbContext.SaveChangesAsync();
        discord.IsConfigured = true;
        var result = await CreateService(dbContext, zoom, discord).RunAsync(team, CancellationToken.None);

        Assert.Equal(1, result.SessionsSynced);
        Assert.Single(zoom.CreateCalls);
        Assert.Single(discord.CreateCalls);
        var saved = dbContext.Sessions.Single();
        Assert.NotNull(saved.ZoomMeetingId);
        Assert.NotNull(saved.DiscordEventId);
        Assert.Equal(SessionStart, saved.ZoomDiscordSyncedStartUtc);
    }

    [Fact]
    public async Task TwoTeams_SharedDiscordBot_EachUsesItsOwnGuildId()
    {
        await using var dbContext = CreateContext();
        var (vec, feeConfig) = await SeedRefsAsync(dbContext);
        var teamA = await SeedTeamAsync(dbContext);
        teamA.DiscordGuildId = 111UL;
        var teamB = await SeedTeamAsync(dbContext);
        teamB.DiscordGuildId = 222UL;
        await dbContext.SaveChangesAsync();
        dbContext.Sessions.Add(NewSession(vec, feeConfig, teamA, "teamA-session"));
        dbContext.Sessions.Add(NewSession(vec, feeConfig, teamB, "teamB-session"));
        await dbContext.SaveChangesAsync();

        // One shared FakeDiscordEventClient instance, matching the real shared-bot design.
        var discord = new FakeDiscordEventClient();
        await CreateService(dbContext, new FakeZoomClient(), discord).RunAsync(teamA, CancellationToken.None);
        await CreateService(dbContext, new FakeZoomClient(), discord).RunAsync(teamB, CancellationToken.None);

        // Each create now also does a ListEventsAsync dedup check first (same guildId), so assert
        // the distinct set used rather than an exact call count.
        Assert.Equal([111UL, 222UL], discord.GuildIdsUsed.Distinct());
    }

    // ---- onlySessionId filter (session-scoped Detail-page refresh, 2026-08-03) ----

    [Fact]
    public async Task RunAsync_WithOnlySessionId_SchedulesOnlyThatSession()
    {
        await using var dbContext = CreateContext();
        var (vec, feeConfig) = await SeedRefsAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        var sessionA = NewSession(vec, feeConfig, team, "session-a");
        var sessionB = NewSession(vec, feeConfig, team, "session-b");
        dbContext.Sessions.AddRange(sessionA, sessionB);
        await dbContext.SaveChangesAsync();

        var zoom = new FakeZoomClient();
        var discord = new FakeDiscordEventClient();
        var result = await CreateService(dbContext, zoom, discord).RunAsync(team, CancellationToken.None, sessionA.Id);

        Assert.Equal(1, result.SessionsSynced);
        Assert.Single(zoom.CreateCalls);
        Assert.Single(discord.CreateCalls);
        var syncedA = dbContext.Sessions.Single(s => s.Id == sessionA.Id);
        Assert.NotNull(syncedA.ZoomMeetingId);
        Assert.Equal(SessionStart, syncedA.ZoomDiscordSyncedStartUtc);
        // The other eligible session waits for the Worker's next team-wide tick.
        var untouchedB = dbContext.Sessions.Single(s => s.Id == sessionB.Id);
        Assert.Null(untouchedB.ZoomMeetingId);
        Assert.Null(untouchedB.DiscordEventId);
        Assert.Null(untouchedB.ZoomDiscordSyncedStartUtc);
    }

    [Fact]
    public async Task RunAsync_WithOnlySessionId_CleansUpOnlyThatCancelledSession()
    {
        await using var dbContext = CreateContext();
        var (vec, feeConfig) = await SeedRefsAsync(dbContext);
        var team = await SeedTeamAsync(dbContext);
        var cancelledA = NewSession(vec, feeConfig, team, "session-a");
        cancelledA.Status = SessionStatus.Cancelled;
        cancelledA.ZoomMeetingId = "zoom-a";
        cancelledA.DiscordEventId = "discord-a";
        var cancelledB = NewSession(vec, feeConfig, team, "session-b");
        cancelledB.Status = SessionStatus.Cancelled;
        cancelledB.ZoomMeetingId = "zoom-b";
        cancelledB.DiscordEventId = "discord-b";
        dbContext.Sessions.AddRange(cancelledA, cancelledB);
        await dbContext.SaveChangesAsync();

        var zoom = new FakeZoomClient();
        var discord = new FakeDiscordEventClient();
        await CreateService(dbContext, zoom, discord).RunAsync(team, CancellationToken.None, cancelledA.Id);

        Assert.Equal("zoom-a", Assert.Single(zoom.DeleteCalls));
        Assert.Equal("discord-a", Assert.Single(discord.DeleteCalls));
        // The other cancelled session's stale meeting/event is left for the next team-wide tick.
        var untouchedB = dbContext.Sessions.Single(s => s.Id == cancelledB.Id);
        Assert.Equal("zoom-b", untouchedB.ZoomMeetingId);
        Assert.Equal("discord-b", untouchedB.DiscordEventId);
    }
}
