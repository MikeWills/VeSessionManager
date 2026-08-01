using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Ingestion;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Issue #77's debounce for the team-level "Refresh now". Reads the JobRunHistory rows
/// ManualCandidateRefreshService already writes rather than adding a column — so these tests are
/// really about it keying on the right rows: this team's, and manual runs only.
/// </summary>
public class TeamRefreshThrottleTests
{
    private static readonly DateTime Now = new(2026, 7, 31, 18, 0, 0, DateTimeKind.Utc);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static TeamRefreshThrottle CreateThrottle(AppDbContext dbContext) =>
        new(dbContext, new FixedTimeProvider(Now));

    private static async Task AddRunAsync(AppDbContext dbContext, int teamId, string jobName, DateTime startedUtc)
    {
        dbContext.JobRunHistories.Add(new JobRunHistory
        {
            JobName = jobName,
            TeamId = teamId,
            StartedUtc = startedUtc,
            CompletedUtc = startedUtc,
            Success = true
        });
        await dbContext.SaveChangesAsync();
    }

    [Fact]
    public async Task NoPriorManualRun_IsAllowed()
    {
        await using var dbContext = CreateContext();

        Assert.Null(await CreateThrottle(dbContext).SecondsUntilAllowedAsync(1, CancellationToken.None));
    }

    [Fact]
    public async Task ManualRunJustNow_IsBlocked_WithSecondsRemaining()
    {
        await using var dbContext = CreateContext();
        await AddRunAsync(dbContext, teamId: 1, "ManualSessionIngestion", Now.AddSeconds(-10));

        var blockedFor = await CreateThrottle(dbContext).SecondsUntilAllowedAsync(1, CancellationToken.None);

        Assert.Equal(50, blockedFor);
    }

    [Fact]
    public async Task ManualRunOlderThanTheDebounce_IsAllowedAgain()
    {
        await using var dbContext = CreateContext();
        await AddRunAsync(dbContext, teamId: 1, "ManualSessionIngestion", Now - TeamRefreshThrottle.Debounce);

        Assert.Null(await CreateThrottle(dbContext).SecondsUntilAllowedAsync(1, CancellationToken.None));
    }

    [Fact]
    public async Task AnotherTeamsRecentRefresh_DoesNotBlockThisTeam()
    {
        await using var dbContext = CreateContext();
        await AddRunAsync(dbContext, teamId: 2, "ManualSessionIngestion", Now.AddSeconds(-5));

        Assert.Null(await CreateThrottle(dbContext).SecondsUntilAllowedAsync(1, CancellationToken.None));
    }

    [Fact]
    public async Task TheBackgroundJobsOwnRun_DoesNotBlockAManualRefresh()
    {
        // The scheduled pipeline logs "SessionIngestion"; only the "Manual"-prefixed rows count.
        // Keying on the wrong name would make Refresh now unusable for an hour after every poll —
        // which is exactly the wait this button exists to skip.
        await using var dbContext = CreateContext();
        await AddRunAsync(dbContext, teamId: 1, "SessionIngestion", Now.AddSeconds(-5));

        Assert.Null(await CreateThrottle(dbContext).SecondsUntilAllowedAsync(1, CancellationToken.None));
    }

    [Fact]
    public async Task TheMostRecentManualRunWins_NotTheOldest()
    {
        await using var dbContext = CreateContext();
        await AddRunAsync(dbContext, teamId: 1, "ManualSessionIngestion", Now.AddHours(-3));
        await AddRunAsync(dbContext, teamId: 1, "ManualSessionIngestion", Now.AddSeconds(-1));

        Assert.Equal(59, await CreateThrottle(dbContext).SecondsUntilAllowedAsync(1, CancellationToken.None));
    }
}
