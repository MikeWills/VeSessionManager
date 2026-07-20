using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VeSessionManager.Core.Data;
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
}
