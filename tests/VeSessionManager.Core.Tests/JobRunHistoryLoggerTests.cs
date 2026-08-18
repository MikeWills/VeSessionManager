using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Jobs;
using Xunit;

namespace VeSessionManager.Core.Tests;

public class JobRunHistoryLoggerTests
{
    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    /// <summary>
    /// A Worker restart cancels every in-flight job. Recording those as ordinary failures put a red
    /// row on the ops dashboard for every restart, which is how people learn to ignore red rows.
    /// </summary>
    [Fact]
    public async Task RunAsync_WhenCancelledByHostShutdown_RecordsShutdownRatherThanAnException()
    {
        await using var dbContext = CreateContext();
        var sut = new JobRunHistoryLogger(dbContext, NullLogger<JobRunHistoryLogger>.Instance);
        using var cts = new CancellationTokenSource();

        await sut.RunAsync("TestJob", ct =>
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }, null, cts.Token);

        var history = Assert.Single(dbContext.JobRunHistories);
        Assert.False(history.Success);
        Assert.Equal("Cancelled by host shutdown.", history.ErrorMessage);
    }

    /// <summary>
    /// <b>#413.</b> <c>DbUpdateException</c>'s own message is "An error occurred while saving the entity
    /// changes. See the inner exception for details." — an instruction to read something the row did
    /// not contain. Reconciliation failed three times for one team with exactly that and the cause can
    /// no longer be identified, which is the whole argument for this.
    /// </summary>
    [Fact]
    public async Task RunAsync_WhenTheFailureHidesInAnInnerException_RecordsTheWholeChain()
    {
        await using var dbContext = CreateContext();
        var sut = new JobRunHistoryLogger(dbContext, NullLogger<JobRunHistoryLogger>.Instance);

        await sut.RunAsync("TestJob",
            _ => throw new InvalidOperationException(
                "An error occurred while saving the entity changes. See the inner exception for details.",
                new ArgumentException("UNIQUE constraint failed: ReconciliationFindings.TeamId")),
            null, CancellationToken.None);

        var history = Assert.Single(dbContext.JobRunHistories);
        Assert.False(history.Success);
        Assert.Contains("An error occurred while saving the entity changes", history.ErrorMessage);
        Assert.Contains("ArgumentException: UNIQUE constraint failed: ReconciliationFindings.TeamId", history.ErrorMessage);
    }

    /// <summary>A chain long or verbose enough to bloat the dashboard is cut, not stored whole.</summary>
    [Fact]
    public async Task RunAsync_WithAnEnormousMessage_TruncatesRatherThanStoringItAll()
    {
        await using var dbContext = CreateContext();
        var sut = new JobRunHistoryLogger(dbContext, NullLogger<JobRunHistoryLogger>.Instance);

        await sut.RunAsync("TestJob", _ => throw new InvalidOperationException(new string('x', 5000)),
            null, CancellationToken.None);

        var history = Assert.Single(dbContext.JobRunHistories);
        Assert.True(history.ErrorMessage!.Length <= JobRunHistoryLogger.MaxErrorMessageLength,
            $"was {history.ErrorMessage.Length}");
        Assert.EndsWith("...", history.ErrorMessage);
    }

    /// <summary>
    /// The guard above keys off the token, not the exception type — an OperationCanceledException
    /// thrown by the job's own logic while nobody asked for cancellation is a real fault and must
    /// still be recorded as one.
    /// </summary>
    [Fact]
    public async Task RunAsync_WhenJobThrowsCancellationWithoutShutdown_StillRecordsAFailure()
    {
        await using var dbContext = CreateContext();
        var sut = new JobRunHistoryLogger(dbContext, NullLogger<JobRunHistoryLogger>.Instance);

        await sut.RunAsync("TestJob", _ => throw new OperationCanceledException("an inner timeout"),
            null, CancellationToken.None);

        var history = Assert.Single(dbContext.JobRunHistories);
        Assert.False(history.Success);
        Assert.Equal("an inner timeout", history.ErrorMessage);
    }

    [Fact]
    public async Task RunAsync_OnSuccess_RecordsSuccessfulRun()
    {
        await using var dbContext = CreateContext();
        var sut = new JobRunHistoryLogger(dbContext, NullLogger<JobRunHistoryLogger>.Instance);

        await sut.RunAsync("TestJob", _ => Task.CompletedTask, null, CancellationToken.None);

        var history = Assert.Single(dbContext.JobRunHistories);
        Assert.Equal("TestJob", history.JobName);
        Assert.True(history.Success);
        Assert.NotNull(history.CompletedUtc);
        Assert.Null(history.ErrorMessage);
        Assert.Null(history.TeamId);
        Assert.True(history.CompletedUtc >= history.StartedUtc);
    }

    [Fact]
    public async Task RunAsync_WhenJobThrows_RecordsFailureAndDoesNotRethrow()
    {
        await using var dbContext = CreateContext();
        var sut = new JobRunHistoryLogger(dbContext, NullLogger<JobRunHistoryLogger>.Instance);

        await sut.RunAsync(
            "FailingJob",
            _ => throw new InvalidOperationException("boom"),
            null,
            CancellationToken.None);

        var history = Assert.Single(dbContext.JobRunHistories);
        Assert.Equal("FailingJob", history.JobName);
        Assert.False(history.Success);
        Assert.Equal("boom", history.ErrorMessage);
        Assert.NotNull(history.CompletedUtc);
    }

    [Fact]
    public async Task RunAsync_WithTeamId_RecordsItOnTheHistoryRow()
    {
        await using var dbContext = CreateContext();
        var sut = new JobRunHistoryLogger(dbContext, NullLogger<JobRunHistoryLogger>.Instance);

        await sut.RunAsync("PerTeamJob", _ => Task.CompletedTask, 42, CancellationToken.None);

        var history = Assert.Single(dbContext.JobRunHistories);
        Assert.Equal(42, history.TeamId);
    }

    /// <summary>
    /// A cancelled job body goes through the same catch-all as any other failure — RunAsync must not
    /// let it escape into ExecuteAsync, where BackgroundServiceExceptionBehavior.StopHost is waiting.
    /// (CancellationToken.None here: the *token* isn't cancelled, so the bookkeeping save still works.)
    /// </summary>
    [Fact]
    public async Task RunAsync_WhenJobThrowsOperationCanceled_CompletesTheHistoryRowWithoutRethrowing()
    {
        await using var dbContext = CreateContext();
        var sut = new JobRunHistoryLogger(dbContext, NullLogger<JobRunHistoryLogger>.Instance);

        await sut.RunAsync(
            "CancelledJob",
            _ => throw new OperationCanceledException("shutting down"),
            null,
            CancellationToken.None);

        var history = Assert.Single(dbContext.JobRunHistories);
        Assert.False(history.Success);
        Assert.Equal("shutting down", history.ErrorMessage);
        Assert.NotNull(history.CompletedUtc);
    }

    // ---- Poisoned change tracker (real SQLite: InMemory enforces no constraint that could poison it) ----

    private static async Task<(SqliteConnection Connection, AppDbContext Context)> CreateSqliteContextAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var context = new AppDbContext(options);
        await context.Database.EnsureCreatedAsync();
        return (connection, context);
    }

    /// <summary>
    /// The failure mode TryCompleteHistoryAsync exists for: this logger shares its scoped DbContext
    /// with the job's own services, so a job that dies partway through a save leaves the offending
    /// entity still tracked. The finally-block save would then retry that same entity, throw the same
    /// error, and escape RunAsync entirely — one team's bad row becoming a full Worker outage. Needs
    /// a provider that actually rejects the row, so: real SQLite, and an FK to a candidate that
    /// doesn't exist.
    /// </summary>
    [Fact]
    public async Task RunAsync_WhenJobLeavesAnUnsaveableEntityTracked_StillRecordsFailureWithoutRethrowing()
    {
        var (connection, dbContext) = await CreateSqliteContextAsync();
        await using var _ = connection;
        await using var __ = dbContext;
        var sut = new JobRunHistoryLogger(dbContext, NullLogger<JobRunHistoryLogger>.Instance);

        await sut.RunAsync(
            "PoisonedJob",
            async _ =>
            {
                dbContext.Payments.Add(new Payment
                {
                    CandidateId = 999_999, // no such candidate — FK violation on save
                    Reason = PaymentReason.InitialExam,
                    Amount = 15m,
                    Status = PaymentStatus.Unpaid,
                    CreatedUtc = DateTime.UtcNow
                });
                await Assert.ThrowsAsync<DbUpdateException>(() => dbContext.SaveChangesAsync());
                throw new InvalidOperationException("job gave up after its save failed");
            },
            null,
            CancellationToken.None);

        dbContext.ChangeTracker.Clear();
        var history = Assert.Single(await dbContext.JobRunHistories.ToListAsync());
        Assert.False(history.Success);
        Assert.Equal("job gave up after its save failed", history.ErrorMessage);
        Assert.NotNull(history.CompletedUtc); // the completion half actually persisted
        Assert.Empty(await dbContext.Payments.ToListAsync()); // the poisoned row never got written
    }

    // ---- Result summary (2026-08-05) ------------------------------------------------------------
    // Success/ErrorMessage alone made three very different outcomes identical on the ops dashboard:
    // sent five, sent none because nothing qualified, and sent none because every attempt failed.
    // The third is the dangerous one, and it looked green.

    private sealed record FakeResult(int Sent, int Failed)
    {
        public override string ToString() => $"sent {Sent}, failed {Failed}";
    }

    private sealed class FakeJobService
    {
        public Task<FakeResult> RunAsync(CancellationToken cancellationToken) => Task.FromResult(new FakeResult(3, 0));
    }

    [Fact]
    public async Task RunAsync_WithAResult_RecordsTheJobsOwnSummary()
    {
        await using var dbContext = CreateContext();
        var sut = new JobRunHistoryLogger(dbContext, NullLogger<JobRunHistoryLogger>.Instance);

        await sut.RunAsync("TestJob", _ => Task.FromResult(new FakeResult(0, 1)), 1, CancellationToken.None);

        var history = Assert.Single(dbContext.JobRunHistories);
        Assert.True(history.Success);
        Assert.Equal("sent 0, failed 1", history.ResultSummary);
    }

    /// <summary>
    /// The binding this whole feature rests on, tested rather than assumed.
    ///
    /// <para>Every real call site passes a <b>method group</b> (<c>purgeService.RunAsync</c>), not a
    /// lambda. A method returning <c>Task&lt;T&gt;</c> converts to BOTH <c>Func&lt;CT, Task&gt;</c>
    /// and <c>Func&lt;CT, Task&lt;T&gt;&gt;</c>, so if overload resolution picked the void overload
    /// every summary would silently stay null: it would still compile, the test above would still
    /// pass, and the dashboard would be exactly as uninformative as before.</para>
    /// </summary>
    [Fact]
    public async Task RunAsync_MethodGroupCallSite_BindsToTheResultOverload()
    {
        await using var dbContext = CreateContext();
        var sut = new JobRunHistoryLogger(dbContext, NullLogger<JobRunHistoryLogger>.Instance);
        var service = new FakeJobService();

        // Deliberately written the way the real jobs write it.
        await sut.RunAsync("TestJob", service.RunAsync, 1, CancellationToken.None);

        var history = Assert.Single(dbContext.JobRunHistories);
        Assert.Equal("sent 3, failed 0", history.ResultSummary);
    }

    [Fact]
    public async Task RunAsync_WithoutAResult_LeavesTheSummaryNull()
    {
        await using var dbContext = CreateContext();
        var sut = new JobRunHistoryLogger(dbContext, NullLogger<JobRunHistoryLogger>.Instance);

        await sut.RunAsync("TestJob", _ => Task.CompletedTask, null, CancellationToken.None);

        Assert.Null(Assert.Single(dbContext.JobRunHistories).ResultSummary);
    }

    [Fact]
    public async Task RunAsync_OverlongSummary_IsTruncated()
    {
        await using var dbContext = CreateContext();
        var sut = new JobRunHistoryLogger(dbContext, NullLogger<JobRunHistoryLogger>.Instance);

        await sut.RunAsync("TestJob", _ => Task.FromResult(new string('x', 5000)), 1, CancellationToken.None);

        Assert.Equal(500, Assert.Single(dbContext.JobRunHistories).ResultSummary!.Length);
    }
}
