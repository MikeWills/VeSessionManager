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
}
