using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.ExamResults;
using VeSessionManager.Core.ExamTools;
using VeSessionManager.Core.Ingestion;
using VeSessionManager.Core.Jobs;
using VeSessionManager.Core.VolunteerExaminers;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Issue #67 part 2. The interesting behavior is mostly about restraint: a year of backdated data
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

        /// <summary>Every applicant-roster call. This is the PII-bearing endpoint, so a test can assert a re-run never touches it.</summary>
        public List<string> ApplicantRosterCalls { get; } = [];

        public Task<IReadOnlyList<ExamToolsApplicant>> GetSessionApplicantsAsync(ExamToolsCredentials credentials, string examToolsSessionId, CancellationToken cancellationToken)
        {
            ApplicantRosterCalls.Add(examToolsSessionId);
            return Task.FromResult<IReadOnlyList<ExamToolsApplicant>>(
                Applicants.TryGetValue(examToolsSessionId, out var list) ? list : []);
        }

        public Task<IReadOnlyList<ExamToolsVe>> GetSessionVeRosterAsync(ExamToolsCredentials credentials, string examToolsSessionId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExamToolsVe>>([]);

        /// <summary>Graded elements per ExamTools applicant id. Absent means "no detail" — the pre-2026-08-15 behavior, where this always returned null.</summary>
        public Dictionary<string, ExamToolsApplicantDetail> ApplicantDetails { get; } = [];

        /// <summary>Every applicant-detail call made, so a test can assert that a resolved session costs none.</summary>
        public List<string> ApplicantDetailCalls { get; } = [];

        /// <summary>Applicant ids that should throw, for the "one bad session must not abandon the import" case.</summary>
        public HashSet<string> ThrowOnApplicantDetail { get; } = [];

        // Not served by this fake: the VEC archive is only reached from the ARRL submission
        // path (#197), which none of these tests exercise.
        public Task<VecArchiveDownload> DownloadVecArchiveAsync(ExamToolsCredentials credentials, string examToolsSessionId, string vecCode, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ExamToolsApplicantDetail?> GetApplicantDetailAsync(ExamToolsCredentials credentials, string examToolsSessionId, string applicantId, CancellationToken cancellationToken)
        {
            ApplicantDetailCalls.Add(applicantId);
            if (ThrowOnApplicantDetail.Contains(applicantId))
            {
                throw new HttpRequestException($"Simulated ExamTools failure for applicant {applicantId}.");
            }

            return Task.FromResult(ApplicantDetails.GetValueOrDefault(applicantId));
        }
    }

    private static ExamToolsApplicantDetail Graded(params (int Element, bool Passed)[] elements) => new()
    {
        Exams = [.. elements.Select(e => new ExamToolsExamResult { Element = e.Element, Graded = true, Passed = e.Passed })]
    };

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
        var examResults = new ExamResultSyncService(
            dbContext, client, timeProvider, Options.Create(new ExamToolsOptions()),
            NullLogger<ExamResultSyncService>.Instance);
        return new HistoricalImportService(dbContext, ingestion, veSync, examResults, jobRunHistoryLogger, timeProvider,
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

    // ---- Exam results (2026-08-15) ----
    //
    // Imports before this fetched no results at all: ExamResultSyncService's routine sweep only
    // looks at sessions started within its 14-day window, and every imported session is already
    // outside it. A year of history therefore landed with every candidate untested and unclassed,
    // permanently — 1,699 of 2,130 candidates on this deployment.

    [Fact]
    public async Task Import_RecordsExamResultsAndLicenseClasses_ForSessionsFarOutsideTheRoutineWindow()
    {
        await using var dbContext = CreateContext();
        var team = await SeedAsync(dbContext);
        var client = new FakeExamToolsClient();
        // Two and a half years before Now — nothing the routine sweep would ever look at again.
        client.ClosedSessions.Add(DoneSession("old", new DateTime(2024, 2, 14, 17, 0, 0, DateTimeKind.Utc)));
        client.Applicants["old"] =
        [
            new ExamToolsApplicant { Id = "a1", Firstname = "Roana", Lastname = "Glory", Email = "r@example.com", Created = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc) },
            new ExamToolsApplicant { Id = "a2", Firstname = "Dell", Lastname = "Ridge", Email = "d@example.com", Created = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc) }
        ];
        // Passed Element 2 only: walked in unlicensed, walked out a Technician.
        client.ApplicantDetails["a1"] = Graded((2, true));
        // Passed Element 4 only: already held General coming in, walked out Extra.
        client.ApplicantDetails["a2"] = Graded((4, true));

        var service = CreateService(dbContext, client);
        await service.QueueAsync(team.Id, new DateOnly(2024, 2, 1), new DateOnly(2024, 2, 29), 1, CancellationToken.None);
        await service.RunNextPendingAsync(CancellationToken.None);

        var candidates = await dbContext.Candidates.OrderBy(c => c.ExamToolsApplicantId).ToListAsync();
        Assert.Equal(2, candidates.Count);
        Assert.All(candidates, c => Assert.True(c.Tested));
        Assert.Equal(LicenseClass.Technician, candidates[0].NewLicenseClass);
        Assert.Equal(LicenseClass.Extra, candidates[1].NewLicenseClass);
        Assert.Equal(LicenseClass.General, candidates[1].InitialLicenseClass);

        var request = await dbContext.HistoricalImportRequests.SingleAsync();
        Assert.Equal(2, request.ResultsRecorded);
    }

    /// <summary>
    /// The import must bring back results and license classes and <b>nothing else</b> — no name, no
    /// email, no FRN. Mike's constraint, 2026-08-15, and it matters most for a candidate whose PII
    /// has already been purged: re-running a range to collect results must not quietly refill it.
    ///
    /// <para><b>What actually enforces this is the session-level skip, not the per-candidate purge
    /// guard.</b> Worth stating because the guard is the obvious answer and it is the wrong one: on a
    /// re-run <c>ImportHistoricalRangeAsync</c> sees the session is already stored and never calls
    /// the applicant roster at all, so the candidate-update block the guard sits in is unreachable.
    /// This was checked by deleting the guard — every test here still passed. The assertion on
    /// <c>ApplicantRosterCalls</c> is therefore the load-bearing one; the field assertions below it
    /// are the backstop for the other direction, ExamResultSyncService itself starting to write
    /// PII.</para>
    ///
    /// <para>The applicant-<i>detail</i> endpoint does return a fuller PII payload over the wire, and
    /// the result sync does call it. Nothing from it is stored: that service only ever writes Tested,
    /// ApplicationStatus, the two result stamps and the two license-class fields.</para>
    /// </summary>
    [Fact]
    public async Task Import_DoesNotRestorePiiOnAPurgedCandidate_WhileStillRecordingTheirResult()
    {
        await using var dbContext = CreateContext();
        var team = await SeedAsync(dbContext);
        var client = new FakeExamToolsClient();
        client.ClosedSessions.Add(DoneSession("old", new DateTime(2024, 2, 14, 17, 0, 0, DateTimeKind.Utc)));
        client.Applicants["old"] =
        [
            new ExamToolsApplicant { Id = "a1", Firstname = "Roana", Lastname = "Glory", Email = "r@example.com", Frn = "0012345678", Created = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc) }
        ];
        client.ApplicantDetails["a1"] = Graded((2, true));

        var service = CreateService(dbContext, client);
        await service.QueueAsync(team.Id, new DateOnly(2024, 2, 1), new DateOnly(2024, 2, 29), 1, CancellationToken.None);
        await service.RunNextPendingAsync(CancellationToken.None);

        // Purge the candidate, then re-run the very same range — the real backfill scenario.
        var candidate = await dbContext.Candidates.SingleAsync();
        candidate.Tested = false;
        candidate.NewLicenseClass = null;
        candidate.InitialLicenseClass = null;
        CandidatePiiFields.Clear(candidate, Now);
        await dbContext.SaveChangesAsync();

        client.ApplicantRosterCalls.Clear();
        await service.QueueAsync(team.Id, new DateOnly(2024, 2, 1), new DateOnly(2024, 2, 29), 1, CancellationToken.None);
        await service.RunNextPendingAsync(CancellationToken.None);

        // The load-bearing assertion: the PII-bearing roster endpoint was never called on the re-run,
        // because the session was already stored. No PII was fetched, so none could be written.
        Assert.Empty(client.ApplicantRosterCalls);

        var after = await dbContext.Candidates.SingleAsync();
        // The result still came back — that path is per-candidate and does run...
        Assert.True(after.Tested);
        Assert.Equal(LicenseClass.Technician, after.NewLicenseClass);
        // ...and the PII stayed gone.
        Assert.NotNull(after.PiiPurgedUtc);
        Assert.Null(after.Name);
        Assert.Null(after.Email);
    }

    [Fact]
    public async Task Import_MakesNoApplicantDetailCalls_ForASessionWhoseCandidatesAreAlreadyResolved()
    {
        await using var dbContext = CreateContext();
        var team = await SeedAsync(dbContext);
        var client = new FakeExamToolsClient();
        client.ClosedSessions.Add(DoneSession("old", new DateTime(2024, 2, 14, 17, 0, 0, DateTimeKind.Utc)));
        client.Applicants["old"] =
        [
            new ExamToolsApplicant { Id = "a1", Firstname = "Roana", Lastname = "Glory", Created = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc) }
        ];
        client.ApplicantDetails["a1"] = Graded((2, true));

        var service = CreateService(dbContext, client);
        await service.QueueAsync(team.Id, new DateOnly(2024, 2, 1), new DateOnly(2024, 2, 29), 1, CancellationToken.None);
        await service.RunNextPendingAsync(CancellationToken.None);
        Assert.Single(client.ApplicantDetailCalls);
        client.ApplicantDetailCalls.Clear();

        // Everything is resolved now, so a second pass over the same range must cost nothing. This is
        // what keeps re-running a range cheap, and what the session-level pre-filter is for.
        await service.QueueAsync(team.Id, new DateOnly(2024, 2, 1), new DateOnly(2024, 2, 29), 1, CancellationToken.None);
        await service.RunNextPendingAsync(CancellationToken.None);

        Assert.Empty(client.ApplicantDetailCalls);
    }

    /// <summary>
    /// A single unreachable applicant record must not abandon a multi-month import and lose every
    /// later chunk. The failure is logged and skipped; re-running the range retries it, and by then
    /// everything already resolved is free.
    /// </summary>
    [Fact]
    public async Task Import_OneFailingApplicantDetail_DoesNotAbandonTheRestOfTheImport()
    {
        await using var dbContext = CreateContext();
        var team = await SeedAsync(dbContext);
        var client = new FakeExamToolsClient();
        client.ClosedSessions.Add(DoneSession("jan", new DateTime(2024, 1, 14, 17, 0, 0, DateTimeKind.Utc)));
        client.ClosedSessions.Add(DoneSession("feb", new DateTime(2024, 2, 14, 17, 0, 0, DateTimeKind.Utc)));
        client.Applicants["jan"] = [new ExamToolsApplicant { Id = "bad", Firstname = "Bad", Lastname = "Row", Created = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) }];
        client.Applicants["feb"] = [new ExamToolsApplicant { Id = "good", Firstname = "Good", Lastname = "Row", Created = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc) }];
        client.ThrowOnApplicantDetail.Add("bad");
        client.ApplicantDetails["good"] = Graded((2, true));

        var service = CreateService(dbContext, client);
        await service.QueueAsync(team.Id, new DateOnly(2024, 1, 1), new DateOnly(2024, 2, 29), 1, CancellationToken.None);
        await service.RunNextPendingAsync(CancellationToken.None);

        var request = await dbContext.HistoricalImportRequests.SingleAsync();
        Assert.Equal(HistoricalImportStatus.Completed, request.Status);
        Assert.Equal(2, request.ChunksCompleted);
        // February's candidate still got their result, despite January's failure.
        Assert.Equal(1, request.ResultsRecorded);
        Assert.True((await dbContext.Candidates.SingleAsync(c => c.ExamToolsApplicantId == "good")).Tested);
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
