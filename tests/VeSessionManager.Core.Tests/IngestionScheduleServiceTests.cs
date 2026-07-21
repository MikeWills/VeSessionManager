using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Ingestion;
using Xunit;

namespace VeSessionManager.Core.Tests;

public class IngestionScheduleServiceTests
{
    private static readonly DateTime Now = new(2026, 7, 21, 12, 0, 0, DateTimeKind.Utc);
    private const int NormalIntervalMinutes = 60;

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static IngestionScheduleService CreateService(AppDbContext dbContext) =>
        new(dbContext, new FixedTimeProvider(Now));

    private static async Task<Team> SeedTeamAsync(AppDbContext dbContext, DateTime? lastIngestionRunUtc)
    {
        var team = new Team { Name = "TESTTEAM", CreatedUtc = Now, LastIngestionRunUtc = lastIngestionRunUtc };
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        return team;
    }

    /// <summary>Seeds a Session for the given team with the given start time/duration/status.</summary>
    private static async Task SeedSessionAsync(
        AppDbContext dbContext, Team team, DateTime scheduledStartUtc, int durationMinutes = 60,
        SessionStatus status = SessionStatus.Active, DateTime? testingCompletedUtc = null)
    {
        var vec = new Vec { Name = "ARRL" };
        var user = new User { Name = "System", Email = "system@localhost", Role = UserRole.SystemAdmin };
        var feeConfiguration = new FeeConfiguration
        {
            Vec = vec, EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            FeeCollectionEnabled = true, ExamFeeAmount = 15m, CreatedByUser = user, CreatedUtc = Now
        };
        dbContext.Sessions.Add(new Session
        {
            ExamToolsSessionId = "session-1", Title = "Test Session", ScheduledStartUtc = scheduledStartUtc,
            DurationMinutes = durationMinutes, Vec = vec, Team = team, FeeConfiguration = feeConfiguration,
            Status = status, TestingCompletedUtc = testingCompletedUtc, CreatedUtc = Now
        });
        await dbContext.SaveChangesAsync();
    }

    [Fact]
    public async Task NeverRunBefore_IsDue_RegardlessOfInterval()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, lastIngestionRunUtc: null);

        var due = await CreateService(dbContext).IsDueAsync(team, NormalIntervalMinutes, CancellationToken.None);

        Assert.True(due);
    }

    [Fact]
    public async Task NoImminentSession_LongPastNormalInterval_IsDue()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, lastIngestionRunUtc: Now.AddHours(-3));

        var due = await CreateService(dbContext).IsDueAsync(team, NormalIntervalMinutes, CancellationToken.None);

        Assert.True(due);
    }

    [Fact]
    public async Task NoImminentSession_JustUnderNormalInterval_IsNotDue()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, lastIngestionRunUtc: Now.AddMinutes(-59));

        var due = await CreateService(dbContext).IsDueAsync(team, NormalIntervalMinutes, CancellationToken.None);

        Assert.False(due);
    }

    [Fact]
    public async Task NoImminentSession_ExactlyAtNormalInterval_IsDue()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, lastIngestionRunUtc: Now.AddMinutes(-60));

        var due = await CreateService(dbContext).IsDueAsync(team, NormalIntervalMinutes, CancellationToken.None);

        Assert.True(due);
    }

    [Fact]
    public async Task SessionStartingIn59Minutes_Surges_IsDueEvenThoughNormalIntervalNotElapsed()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, lastIngestionRunUtc: Now.AddMinutes(-1));
        await SeedSessionAsync(dbContext, team, scheduledStartUtc: Now.AddMinutes(59));

        var due = await CreateService(dbContext).IsDueAsync(team, NormalIntervalMinutes, CancellationToken.None);

        Assert.True(due);
    }

    [Fact]
    public async Task SessionStartingExactly60MinutesOut_Surges_InclusiveBoundary()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, lastIngestionRunUtc: Now.AddMinutes(-1));
        await SeedSessionAsync(dbContext, team, scheduledStartUtc: Now.AddMinutes(60));

        var due = await CreateService(dbContext).IsDueAsync(team, NormalIntervalMinutes, CancellationToken.None);

        Assert.True(due);
    }

    [Fact]
    public async Task SessionStartingIn61Minutes_DoesNotSurge_FallsBackToNormalInterval()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, lastIngestionRunUtc: Now.AddMinutes(-1));
        await SeedSessionAsync(dbContext, team, scheduledStartUtc: Now.AddMinutes(61));

        var due = await CreateService(dbContext).IsDueAsync(team, NormalIntervalMinutes, CancellationToken.None);

        Assert.False(due);
    }

    [Fact]
    public async Task SessionInProgress_MidDuration_Surges()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, lastIngestionRunUtc: Now.AddMinutes(-1));
        // Started 30 minutes ago, 60-minute duration -> ends in 30 minutes, still in progress now.
        await SeedSessionAsync(dbContext, team, scheduledStartUtc: Now.AddMinutes(-30), durationMinutes: 60);

        var due = await CreateService(dbContext).IsDueAsync(team, NormalIntervalMinutes, CancellationToken.None);

        Assert.True(due);
    }

    [Fact]
    public async Task SessionEnded_PastStartPlusDuration_DoesNotSurge_FallsBackToNormalInterval()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, lastIngestionRunUtc: Now.AddMinutes(-1));
        // Started 90 minutes ago, 60-minute duration -> ended 30 minutes ago.
        await SeedSessionAsync(dbContext, team, scheduledStartUtc: Now.AddMinutes(-90), durationMinutes: 60);

        var due = await CreateService(dbContext).IsDueAsync(team, NormalIntervalMinutes, CancellationToken.None);

        Assert.False(due);
    }

    [Fact]
    public async Task CancelledSessionWithinWindow_DoesNotSurge()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, lastIngestionRunUtc: Now.AddMinutes(-1));
        await SeedSessionAsync(dbContext, team, scheduledStartUtc: Now.AddMinutes(30), status: SessionStatus.Cancelled);

        var due = await CreateService(dbContext).IsDueAsync(team, NormalIntervalMinutes, CancellationToken.None);

        Assert.False(due);
    }

    [Fact]
    public async Task CompletedSessionWithinWindow_DoesNotSurge()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, lastIngestionRunUtc: Now.AddMinutes(-1));
        await SeedSessionAsync(dbContext, team, scheduledStartUtc: Now.AddMinutes(30), testingCompletedUtc: Now.AddMinutes(-5));

        var due = await CreateService(dbContext).IsDueAsync(team, NormalIntervalMinutes, CancellationToken.None);

        Assert.False(due);
    }

    [Fact]
    public async Task ImminentSessionOnAnotherTeam_DoesNotCauseThisTeamToSurge()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, lastIngestionRunUtc: Now.AddMinutes(-1));
        var otherTeam = new Team { Name = "OTHERTEAM", CreatedUtc = Now };
        dbContext.Teams.Add(otherTeam);
        await dbContext.SaveChangesAsync();
        await SeedSessionAsync(dbContext, otherTeam, scheduledStartUtc: Now.AddMinutes(5));

        var due = await CreateService(dbContext).IsDueAsync(team, NormalIntervalMinutes, CancellationToken.None);

        Assert.False(due);
    }
}
