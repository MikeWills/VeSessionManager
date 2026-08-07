using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.ExamTools;
using VeSessionManager.Core.Ingestion;
using VeSessionManager.Core.Jobs;
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
        // Real logger, not a stub: the import records its VE roster step as its own JobRunHistory
        // run, and that row is what makes "did the import fetch rosters?" answerable on the dashboard.
        var jobRunHistoryLogger = new JobRunHistoryLogger(dbContext, NullLogger<JobRunHistoryLogger>.Instance);
        return new HistoricalImportService(dbContext, ingestion, veSync, jobRunHistoryLogger, timeProvider,
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

    // ---- Reclaiming a request abandoned by a Worker restart (T11, 2026-08-03) ----

    /// <summary>Seeds a request already in Running — the state a Worker restart mid-import leaves behind.</summary>
    private static async Task<HistoricalImportRequest> SeedRunningRequestAsync(
        AppDbContext dbContext, Team team, DateOnly startDate, DateOnly endDate, DateTime startedUtc, int chunksCompleted = 0)
    {
        var request = new HistoricalImportRequest
        {
            TeamId = team.Id,
            StartDate = startDate,
            EndDate = endDate,
            Status = HistoricalImportStatus.Running,
            RequestedByUserId = 1,
            RequestedUtc = startedUtc,
            StartedUtc = startedUtc,
            ChunksTotal = HistoricalImportService.CountChunks(startDate, endDate),
            ChunksCompleted = chunksCompleted
        };
        dbContext.HistoricalImportRequests.Add(request);
        await dbContext.SaveChangesAsync();
        return request;
    }

    [Fact]
    public async Task RunNextPending_RequestLeftRunningPastTheStaleThreshold_IsReclaimedAndCompleted()
    {
        // Without this, a Worker restart mid-import left the row Running forever: only Pending rows
        // were selected, and QueueAsync's one-at-a-time guard counts Running, so the team could
        // never queue another import without someone hand-editing the database.
        await using var dbContext = CreateContext();
        var team = await SeedAsync(dbContext);
        var abandonedAt = Now - HistoricalImportService.StaleRunningThreshold - TimeSpan.FromMinutes(1);
        var request = await SeedRunningRequestAsync(dbContext, team, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), abandonedAt);

        var ranSomething = await CreateService(dbContext, new FakeExamToolsClient()).RunNextPendingAsync(CancellationToken.None);

        Assert.True(ranSomething);
        var reclaimed = await dbContext.HistoricalImportRequests.SingleAsync(r => r.Id == request.Id);
        Assert.Equal(HistoricalImportStatus.Completed, reclaimed.Status);
    }

    [Fact]
    public async Task HasPending_RequestLeftRunningPastTheStaleThreshold_IsVisibleToTheQueuePeek()
    {
        // Must use the same eligibility rule as RunNextPendingAsync, or the Worker's cheap peek would
        // skip the tick and the reclaimable request would never be looked at.
        await using var dbContext = CreateContext();
        var team = await SeedAsync(dbContext);
        await SeedRunningRequestAsync(dbContext, team, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31),
            Now - HistoricalImportService.StaleRunningThreshold - TimeSpan.FromMinutes(1));

        Assert.True(await CreateService(dbContext, new FakeExamToolsClient()).HasPendingAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RunNextPending_RequestRunningInsideTheStaleThreshold_IsLeftAlone()
    {
        // A genuinely in-progress import must not be picked up a second time.
        await using var dbContext = CreateContext();
        var team = await SeedAsync(dbContext);
        var client = new FakeExamToolsClient();
        await SeedRunningRequestAsync(dbContext, team, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31),
            Now - HistoricalImportService.StaleRunningThreshold + TimeSpan.FromMinutes(1));

        var ranSomething = await CreateService(dbContext, client).RunNextPendingAsync(CancellationToken.None);

        Assert.False(ranSomething);
        Assert.Empty(client.ClosedFeedCalls);
        Assert.Equal(HistoricalImportStatus.Running, dbContext.HistoricalImportRequests.Single().Status);
    }

    [Fact]
    public async Task HasPending_RequestRunningInsideTheStaleThreshold_IsNotVisibleToTheQueuePeek()
    {
        await using var dbContext = CreateContext();
        var team = await SeedAsync(dbContext);
        await SeedRunningRequestAsync(dbContext, team, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31),
            Now - HistoricalImportService.StaleRunningThreshold + TimeSpan.FromMinutes(1));

        Assert.False(await CreateService(dbContext, new FakeExamToolsClient()).HasPendingAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RunNextPending_ReclaimedRequest_ResumesAtTheChunkAfterTheLastCompletedOne()
    {
        // Skip(ChunksCompleted): the counter is incremented only after a chunk's import returns, so
        // the first chunk not skipped is exactly the interrupted one. Re-walking from the start
        // would re-fetch every earlier month from ExamTools for nothing.
        await using var dbContext = CreateContext();
        var team = await SeedAsync(dbContext);
        var client = new FakeExamToolsClient();
        await SeedRunningRequestAsync(dbContext, team, new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 31),
            Now - HistoricalImportService.StaleRunningThreshold - TimeSpan.FromMinutes(1), chunksCompleted: 1);

        await CreateService(dbContext, client).RunNextPendingAsync(CancellationToken.None);

        Assert.Equal(
            [(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28)), (new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31))],
            client.ClosedFeedCalls);
    }

    [Fact]
    public async Task RunNextPending_ReclaimedRequest_ChunkCounterEndsAtTheTotal_NotPastIt()
    {
        // Re-walking the whole range would climb past ChunksTotal and render "4/3" on the admin page.
        await using var dbContext = CreateContext();
        var team = await SeedAsync(dbContext);
        await SeedRunningRequestAsync(dbContext, team, new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 31),
            Now - HistoricalImportService.StaleRunningThreshold - TimeSpan.FromMinutes(1), chunksCompleted: 1);

        await CreateService(dbContext, new FakeExamToolsClient()).RunNextPendingAsync(CancellationToken.None);

        var request = dbContext.HistoricalImportRequests.Single();
        Assert.Equal(3, request.ChunksTotal);
        Assert.Equal(3, request.ChunksCompleted);
    }

    [Fact]
    public async Task RunNextPending_RunningRequestWithNoStartedUtc_IsNeverReclaimed()
    {
        // Null StartedUtc gives no evidence of age, so the "is it stale?" test cannot be made — the
        // eligibility predicate requires StartedUtc != null on purpose.
        await using var dbContext = CreateContext();
        var team = await SeedAsync(dbContext);
        var request = await SeedRunningRequestAsync(dbContext, team, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), Now.AddDays(-10));
        request.StartedUtc = null;
        await dbContext.SaveChangesAsync();
        var service = CreateService(dbContext, new FakeExamToolsClient());

        Assert.False(await service.RunNextPendingAsync(CancellationToken.None));
        Assert.False(await service.HasPendingAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RunNextPending_APendingRequestIsStillPreferredByRequestedDate_OverAnOlderReclaimable()
    {
        // Reclaiming widens the candidate set but must not change the ordering rule.
        await using var dbContext = CreateContext();
        var team = await SeedAsync(dbContext);
        await SeedRunningRequestAsync(dbContext, team, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), Now.AddDays(-1));
        dbContext.HistoricalImportRequests.Add(new HistoricalImportRequest
        {
            TeamId = team.Id,
            StartDate = new DateOnly(2026, 5, 1),
            EndDate = new DateOnly(2026, 5, 31),
            Status = HistoricalImportStatus.Pending,
            RequestedByUserId = 1,
            RequestedUtc = Now.AddDays(-2), // older than the abandoned one
            ChunksTotal = 1
        });
        await dbContext.SaveChangesAsync();

        var client = new FakeExamToolsClient();
        await CreateService(dbContext, client).RunNextPendingAsync(CancellationToken.None);

        Assert.Equal([(new DateOnly(2026, 5, 1), new DateOnly(2026, 5, 31))], client.ClosedFeedCalls);
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
