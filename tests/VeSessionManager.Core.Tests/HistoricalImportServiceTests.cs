using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.ExamTools;
using VeSessionManager.Core.Ingestion;
using VeSessionManager.Core.VolunteerExaminers;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Issue #67 part 2. The interesting behaviour is mostly about restraint: a year of backdated data
/// must not trigger the live-session side effects, must not be mistaken for a cancellation sweep,
/// and must be safe to re-run.
/// </summary>
public class HistoricalImportServiceTests
{
    private static readonly DateTime Now = new(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);

        // Task.Delay(…, TimeProvider, …) would otherwise really wait out the inter-chunk pause in
        // every test that runs a multi-month import.
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            base.CreateTimer(callback, state, TimeSpan.Zero, period);
    }

    private class FakeExamToolsClient : IExamToolsClient
    {
        public List<ExamToolsSession> ClosedSessions { get; } = [];
        public Dictionary<string, List<ExamToolsApplicant>> Applicants { get; } = [];
        public List<(DateOnly Start, DateOnly End)> ClosedFeedCalls { get; } = [];

        public Task<IReadOnlyList<ExamToolsSession>> GetTeamSessionsAsync(ExamToolsCredentials credentials, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExamToolsSession>>([]);

        public virtual Task<IReadOnlyList<ExamToolsSession>> GetTeamClosedSessionsAsync(ExamToolsCredentials credentials, DateOnly startDateUtc, DateOnly endDateUtc, CancellationToken cancellationToken)
        {
            ClosedFeedCalls.Add((startDateUtc, endDateUtc));
            var inRange = ClosedSessions
                .Where(s => DateOnly.FromDateTime(s.Date) >= startDateUtc && DateOnly.FromDateTime(s.Date) <= endDateUtc)
                .ToList();
            return Task.FromResult<IReadOnlyList<ExamToolsSession>>(inRange);
        }

        public Task<IReadOnlyList<ExamToolsApplicant>> GetSessionApplicantsAsync(ExamToolsCredentials credentials, string examToolsSessionId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExamToolsApplicant>>(
                Applicants.TryGetValue(examToolsSessionId, out var list) ? list : []);

        public Task<IReadOnlyList<ExamToolsVe>> GetSessionVeRosterAsync(ExamToolsCredentials credentials, string examToolsSessionId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExamToolsVe>>([]);

        public Task<ExamToolsApplicantDetail?> GetApplicantDetailAsync(ExamToolsCredentials credentials, string examToolsSessionId, string applicantId, CancellationToken cancellationToken) =>
            Task.FromResult<ExamToolsApplicantDetail?>(null);
    }

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static HistoricalImportService CreateService(AppDbContext dbContext, FakeExamToolsClient client)
    {
        var timeProvider = new FixedTimeProvider(Now);
        var ingestion = new SessionIngestionService(
            dbContext, client, timeProvider, Options.Create(new ExamToolsOptions()),
            NullLogger<SessionIngestionService>.Instance);
        var veSync = new VolunteerExaminerSyncService(
            dbContext, client, Options.Create(new ExamToolsOptions()), timeProvider,
            NullLogger<VolunteerExaminerSyncService>.Instance);
        return new HistoricalImportService(dbContext, ingestion, veSync, timeProvider,
            NullLogger<HistoricalImportService>.Instance);
    }

    private static ExamToolsSession DoneSession(string id, DateTime date) => new()
    {
        Id = id,
        Date = date,
        Vec = "arrl",
        State = "done",
        ApplicantCount = 0,
        SessionDef = new ExamToolsSessionDef { Summary = "Historical Session", ExtId = "AD2GX" }
    };

    private static async Task<Team> SeedAsync(AppDbContext dbContext)
    {
        var vec = new Vec { Name = "ARRL" };
        var user = new User { Name = "System", Email = "system@localhost", Role = UserRole.SystemAdmin };
        dbContext.FeeConfigurations.Add(new FeeConfiguration
        {
            Vec = vec,
            EffectiveDate = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            FeeCollectionEnabled = true,
            ExamFeeAmount = 15m,
            RetainedAmount = 7m,
            CreatedByUser = user,
            CreatedUtc = Now
        });
        var team = new Team
        {
            Name = "HRCC",
            ExamToolsTeamCode = "HRCC",
            ExamToolsUsername = "u",
            ExamToolsPassword = "p",
            CreatedUtc = Now
        };
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        return team;
    }

    // ---- Chunking ----

    [Fact]
    public void RangeIsSplitIntoCalendarMonths()
    {
        var chunks = HistoricalImportService.Chunks(new DateOnly(2026, 1, 15), new DateOnly(2026, 3, 10)).ToList();

        Assert.Equal(3, chunks.Count);
        Assert.Equal((new DateOnly(2026, 1, 15), new DateOnly(2026, 1, 31)), chunks[0]);
        Assert.Equal((new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28)), chunks[1]);
        Assert.Equal((new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 10)), chunks[2]);
    }

    [Fact]
    public void SingleDayRange_IsOneChunk()
    {
        var chunks = HistoricalImportService.Chunks(new DateOnly(2026, 5, 4), new DateOnly(2026, 5, 4)).ToList();

        Assert.Equal((new DateOnly(2026, 5, 4), new DateOnly(2026, 5, 4)), Assert.Single(chunks));
    }

    // ---- Queueing ----

    [Fact]
    public async Task QueueAsync_RejectsAnInvertedRange()
    {
        await using var dbContext = CreateContext();
        var team = await SeedAsync(dbContext);

        var result = await CreateService(dbContext, new FakeExamToolsClient())
            .QueueAsync(team.Id, new DateOnly(2026, 6, 1), new DateOnly(2026, 5, 1), 1, CancellationToken.None);

        Assert.Equal(HistoricalImportQueueResult.InvalidRange, result);
        Assert.Empty(dbContext.HistoricalImportRequests);
    }

    [Fact]
    public async Task QueueAsync_RejectsARangeStartingInTheFuture()
    {
        await using var dbContext = CreateContext();
        var team = await SeedAsync(dbContext);

        var result = await CreateService(dbContext, new FakeExamToolsClient())
            .QueueAsync(team.Id, new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 1), 1, CancellationToken.None);

        Assert.Equal(HistoricalImportQueueResult.InvalidRange, result);
    }

    [Fact]
    public async Task QueueAsync_RefusesASecondImportWhileOneIsInFlight()
    {
        await using var dbContext = CreateContext();
        var team = await SeedAsync(dbContext);
        var service = CreateService(dbContext, new FakeExamToolsClient());
        await service.QueueAsync(team.Id, new DateOnly(2026, 1, 1), new DateOnly(2026, 2, 1), 1, CancellationToken.None);

        var second = await service.QueueAsync(team.Id, new DateOnly(2026, 3, 1), new DateOnly(2026, 4, 1), 1, CancellationToken.None);

        Assert.Equal(HistoricalImportQueueResult.AlreadyRunning, second);
        Assert.Single(dbContext.HistoricalImportRequests);
    }

    [Fact]
    public async Task QueueAsync_RecordsChunkCountUpFront_SoProgressHasADenominator()
    {
        await using var dbContext = CreateContext();
        var team = await SeedAsync(dbContext);

        await CreateService(dbContext, new FakeExamToolsClient())
            .QueueAsync(team.Id, new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 31), 1, CancellationToken.None);

        var request = dbContext.HistoricalImportRequests.Single();
        Assert.Equal(3, request.ChunksTotal);
        Assert.Equal(HistoricalImportStatus.Pending, request.Status);
    }

    // ---- Running ----

    [Fact]
    public async Task RunNextPending_ImportsSessionsAndCandidatesAcrossEveryChunk()
    {
        await using var dbContext = CreateContext();
        var team = await SeedAsync(dbContext);
        var client = new FakeExamToolsClient();
        client.ClosedSessions.Add(DoneSession("jan", new DateTime(2026, 1, 10, 17, 0, 0, DateTimeKind.Utc)));
        client.ClosedSessions.Add(DoneSession("mar", new DateTime(2026, 3, 20, 17, 0, 0, DateTimeKind.Utc)));
        client.Applicants["jan"] = [new ExamToolsApplicant
        {
            Id = "a1", Firstname = "Roana", Lastname = "Glory", Email = "r@example.com",
            Frn = "0012345678", Created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        }];

        var service = CreateService(dbContext, client);
        await service.QueueAsync(team.Id, new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 31), 1, CancellationToken.None);

        var ranSomething = await service.RunNextPendingAsync(CancellationToken.None);

        Assert.True(ranSomething);
        var request = dbContext.HistoricalImportRequests.Single();
        Assert.Equal(HistoricalImportStatus.Completed, request.Status);
        Assert.Equal(3, request.ChunksCompleted);
        Assert.Equal(2, request.SessionsImported);
        Assert.Equal(1, request.CandidatesImported);
        Assert.Equal(2, dbContext.Sessions.Count());
        Assert.Equal(3, client.ClosedFeedCalls.Count); // one call per month, not one for the year
    }

    [Fact]
    public async Task ImportedSessionsAreStampedClosed_SoTheRoutineSweepLeavesThemAlone()
    {
        await using var dbContext = CreateContext();
        var team = await SeedAsync(dbContext);
        var client = new FakeExamToolsClient();
        client.ClosedSessions.Add(DoneSession("jan", new DateTime(2026, 1, 10, 17, 0, 0, DateTimeKind.Utc)));

        var service = CreateService(dbContext, client);
        await service.QueueAsync(team.Id, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), 1, CancellationToken.None);
        await service.RunNextPendingAsync(CancellationToken.None);

        Assert.Equal(Now, dbContext.Sessions.Single().ExamToolsClosedUtc);
    }

    [Fact]
    public async Task ReRunningAnAlreadyImportedRange_AddsNothing()
    {
        // Ingestion is scan-based and idempotent, but issue #67 asked for that to be an explicit
        // test rather than an assumption — a duplicate-session bug here would be invisible until
        // someone read the stats page.
        await using var dbContext = CreateContext();
        var team = await SeedAsync(dbContext);
        var client = new FakeExamToolsClient();
        client.ClosedSessions.Add(DoneSession("jan", new DateTime(2026, 1, 10, 17, 0, 0, DateTimeKind.Utc)));
        client.Applicants["jan"] = [new ExamToolsApplicant
        {
            Id = "a1", Firstname = "Roana", Lastname = "Glory", Email = "r@example.com",
            Frn = "0012345678", Created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        }];

        var service = CreateService(dbContext, client);
        await service.QueueAsync(team.Id, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), 1, CancellationToken.None);
        await service.RunNextPendingAsync(CancellationToken.None);

        await service.QueueAsync(team.Id, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), 1, CancellationToken.None);
        await service.RunNextPendingAsync(CancellationToken.None);

        Assert.Single(dbContext.Sessions);
        Assert.Single(dbContext.Candidates);
        Assert.Equal(0, dbContext.HistoricalImportRequests.OrderByDescending(r => r.Id).First().SessionsImported);
    }

    [Fact]
    public async Task ImportNeverCancelsSessionsOutsideTheImportedRange()
    {
        // The single most dangerous way this could have been built: reusing
        // SessionIngestionService.RunAsync, whose "vanished from the feed means cancelled" pass
        // would see a team's entire live schedule as absent, because a date-ranged feed excludes it
        // by construction.
        await using var dbContext = CreateContext();
        var team = await SeedAsync(dbContext);
        var client = new FakeExamToolsClient();

        var upcoming = new Session
        {
            ExamToolsSessionId = "future-session",
            Title = "Upcoming",
            ScheduledStartUtc = Now.AddDays(14),
            DurationMinutes = 60,
            VecId = dbContext.Vecs.Single().Id,
            TeamId = team.Id,
            FeeConfigurationId = dbContext.FeeConfigurations.Single().Id,
            CreatedUtc = Now
        };
        dbContext.Sessions.Add(upcoming);
        await dbContext.SaveChangesAsync();

        client.ClosedSessions.Add(DoneSession("jan", new DateTime(2026, 1, 10, 17, 0, 0, DateTimeKind.Utc)));
        var service = CreateService(dbContext, client);
        await service.QueueAsync(team.Id, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), 1, CancellationToken.None);
        await service.RunNextPendingAsync(CancellationToken.None);

        var stillUpcoming = dbContext.Sessions.Single(s => s.ExamToolsSessionId == "future-session");
        Assert.Equal(SessionStatus.Active, stillUpcoming.Status);
        Assert.Null(stillUpcoming.CancelledUtc);
    }

    [Fact]
    public async Task ImportLeavesAnAlreadyStoredSessionUntouched_EvenIfTheFeedDisagrees()
    {
        // No reschedule handling and no ExtId backfill on the import path: it only ever creates what
        // is missing. A historical feed disagreeing about a stored session's time is not a
        // reschedule to act on months later.
        await using var dbContext = CreateContext();
        var team = await SeedAsync(dbContext);
        var originalStart = new DateTime(2026, 1, 10, 17, 0, 0, DateTimeKind.Utc);
        dbContext.Sessions.Add(new Session
        {
            ExamToolsSessionId = "jan",
            Title = "Already stored",
            ScheduledStartUtc = originalStart,
            DurationMinutes = 60,
            VecId = dbContext.Vecs.Single().Id,
            TeamId = team.Id,
            FeeConfigurationId = dbContext.FeeConfigurations.Single().Id,
            CreatedUtc = Now
        });
        await dbContext.SaveChangesAsync();

        var client = new FakeExamToolsClient();
        client.ClosedSessions.Add(DoneSession("jan", originalStart.AddDays(2)));

        var service = CreateService(dbContext, client);
        await service.QueueAsync(team.Id, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), 1, CancellationToken.None);
        await service.RunNextPendingAsync(CancellationToken.None);

        var session = dbContext.Sessions.Single();
        Assert.Equal(originalStart, session.ScheduledStartUtc);
        Assert.False(session.RescheduleFlaggedForReview);
        Assert.Null(session.ExtId);
    }

    [Fact]
    public async Task RunNextPending_WithNothingQueued_DoesNothing()
    {
        await using var dbContext = CreateContext();
        await SeedAsync(dbContext);

        Assert.False(await CreateService(dbContext, new FakeExamToolsClient()).RunNextPendingAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AFailedChunk_MarksTheRequestFailed_AndKeepsWhatAlreadyLanded()
    {
        await using var dbContext = CreateContext();
        var team = await SeedAsync(dbContext);
        var client = new ThrowOnSecondChunkClient();
        client.ClosedSessions.Add(DoneSession("jan", new DateTime(2026, 1, 10, 17, 0, 0, DateTimeKind.Utc)));

        var service = CreateService(dbContext, client);
        await service.QueueAsync(team.Id, new DateOnly(2026, 1, 1), new DateOnly(2026, 2, 28), 1, CancellationToken.None);
        await service.RunNextPendingAsync(CancellationToken.None);

        var request = dbContext.HistoricalImportRequests.Single();
        Assert.Equal(HistoricalImportStatus.Failed, request.Status);
        Assert.Equal(1, request.ChunksCompleted);
        Assert.NotNull(request.ErrorMessage);
        // January's session survives — a later chunk failing must not discard earlier work.
        Assert.Single(dbContext.Sessions);
    }

    private sealed class ThrowOnSecondChunkClient : FakeExamToolsClient
    {
        private int calls;

        public override Task<IReadOnlyList<ExamToolsSession>> GetTeamClosedSessionsAsync(ExamToolsCredentials credentials, DateOnly startDateUtc, DateOnly endDateUtc, CancellationToken cancellationToken)
        {
            if (++calls > 1)
            {
                throw new HttpRequestException("Response status code does not indicate success: 500.");
            }

            return base.GetTeamClosedSessionsAsync(credentials, startDateUtc, endDateUtc, cancellationToken);
        }
    }
}
