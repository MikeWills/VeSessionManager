using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Ingestion;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Issue #73. The behaviour that matters here is mostly about *not* crying wolf: a fresh deployment
/// and a dead Worker look superficially similar (nothing has been polled) but warrant completely
/// different messages, and a TeamAdmin looking at a brand-new team must not be told the Worker is
/// down because that one team has never run.
/// </summary>
public class IngestionStatusServiceTests
{
    private static readonly DateTime Now = new(2026, 7, 31, 18, 0, 0, DateTimeKind.Utc);
    private const int IntervalMinutes = 60;

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static IngestionStatusService CreateService(AppDbContext dbContext) =>
        new(dbContext, new IngestionScheduleService(), new FixedTimeProvider(Now));

    private static async Task SeedSettingsAsync(AppDbContext dbContext, int intervalMinutes = IntervalMinutes)
    {
        dbContext.SystemSettings.Add(new SystemSettings
        {
            Id = SystemSettingsService.SingletonId,
            UlsWatcherIntervalHours = 12,
            UlsWatcherStartHourEt = 8,
            SessionIngestionIntervalMinutes = intervalMinutes
        });
        await dbContext.SaveChangesAsync();
    }

    private static async Task<Team> SeedTeamAsync(AppDbContext dbContext, string name, DateTime? lastRunUtc)
    {
        var team = new Team
        {
            Name = name,
            ExamToolsTeamCode = name,
            ExamToolsUsername = "u",
            ExamToolsPassword = "p",
            CreatedUtc = Now,
            LastIngestionRunUtc = lastRunUtc
        };
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        return team;
    }

    [Fact]
    public async Task RecentlyPolledTeam_IsHealthy_AndReportsNextDue()
    {
        await using var dbContext = CreateContext();
        await SeedSettingsAsync(dbContext);
        await SeedTeamAsync(dbContext, "HRCC", Now.AddMinutes(-20));

        var report = await CreateService(dbContext).GetAsync(null, CancellationToken.None);

        Assert.Equal(IngestionHealthState.Healthy, report.Health);
        Assert.False(report.NeedsAttention);
        var row = Assert.Single(report.Teams);
        Assert.False(row.IsDueNow);
        Assert.Equal(Now.AddMinutes(40), row.NextDueUtc);
    }

    [Fact]
    public async Task NeverPolledTeam_ReportsNeverPolled_NotStale()
    {
        // Distinct states on purpose: "you haven't started the Worker yet" and "the Worker died"
        // need different messages, and a fresh install must not show a red alarm on first load.
        await using var dbContext = CreateContext();
        await SeedSettingsAsync(dbContext);
        await SeedTeamAsync(dbContext, "WX0MIK", lastRunUtc: null);

        var report = await CreateService(dbContext).GetAsync(null, CancellationToken.None);

        Assert.Equal(IngestionHealthState.NeverPolled, report.Health);
        Assert.True(report.NeedsAttention);
        var row = Assert.Single(report.Teams);
        Assert.True(row.IsDueNow);
        Assert.Null(row.NextDueUtc); // nothing to count down to — "due now" is the whole answer
    }

    [Fact]
    public async Task NoTeamsAtAll_IsNotAWarning()
    {
        await using var dbContext = CreateContext();
        await SeedSettingsAsync(dbContext);

        var report = await CreateService(dbContext).GetAsync(null, CancellationToken.None);

        Assert.Equal(IngestionHealthState.NoTeams, report.Health);
        Assert.False(report.NeedsAttention);
        Assert.Empty(report.Teams);
    }

    [Fact]
    public async Task NothingPolledForTwoIntervals_IsStale()
    {
        await using var dbContext = CreateContext();
        await SeedSettingsAsync(dbContext);
        await SeedTeamAsync(dbContext, "HRCC", Now.AddMinutes(-(IntervalMinutes * 2) - 1));

        var report = await CreateService(dbContext).GetAsync(null, CancellationToken.None);

        Assert.Equal(IngestionHealthState.Stale, report.Health);
        Assert.True(report.NeedsAttention);
    }

    [Fact]
    public async Task JustOverOneInterval_IsNotYetStale()
    {
        // One missed interval is ordinary jitter — the Worker's own 5-minute tick doesn't align with
        // the hourly per-team gate, so the real gap between polls routinely runs a little long.
        // Warning at 1x would fire during normal operation and train everyone to ignore the banner.
        await using var dbContext = CreateContext();
        await SeedSettingsAsync(dbContext);
        await SeedTeamAsync(dbContext, "HRCC", Now.AddMinutes(-IntervalMinutes - 5));

        var report = await CreateService(dbContext).GetAsync(null, CancellationToken.None);

        Assert.Equal(IngestionHealthState.Healthy, report.Health);
    }

    [Fact]
    public async Task HealthIsDeploymentWide_EvenWhenTheReportIsScopedToOneNewTeam()
    {
        // The regression this guards: scoping health to the requested team would tell a TeamAdmin
        // who just created a team that the Worker is down, when it is polling another team happily.
        await using var dbContext = CreateContext();
        await SeedSettingsAsync(dbContext);
        await SeedTeamAsync(dbContext, "HRCC", Now.AddMinutes(-5));
        var newTeam = await SeedTeamAsync(dbContext, "WX0MIK", lastRunUtc: null);

        var report = await CreateService(dbContext).GetAsync([newTeam.Id], CancellationToken.None);

        Assert.Equal(IngestionHealthState.Healthy, report.Health);
        Assert.False(report.NeedsAttention);
        // ...while the scoped row still honestly reports that this particular team is due.
        var row = Assert.Single(report.Teams);
        Assert.Equal("WX0MIK", row.TeamName);
        Assert.True(row.IsDueNow);
    }

    [Fact]
    public async Task MissingSystemSettingsRow_FallsBackToTheSeededDefault_WithoutWritingOne()
    {
        // A page render must never have a side effect, so this path deliberately avoids
        // SystemSettingsService.GetAsync's get-or-create. Asserting no row was created is the point.
        await using var dbContext = CreateContext();
        await SeedTeamAsync(dbContext, "HRCC", Now.AddMinutes(-10));

        var report = await CreateService(dbContext).GetAsync(null, CancellationToken.None);

        Assert.Equal(SystemSettingsService.DefaultSessionIngestionIntervalMinutes, report.IntervalMinutes);
        Assert.Empty(dbContext.SystemSettings);
    }

    [Fact]
    public async Task StaleThresholdFollowsTheConfiguredInterval()
    {
        // A deployment that polls every 5 minutes should not have to wait two hours to be told the
        // Worker is down — the threshold is a multiple of the admin's own setting, not a constant.
        await using var dbContext = CreateContext();
        await SeedSettingsAsync(dbContext, intervalMinutes: 5);
        await SeedTeamAsync(dbContext, "HRCC", Now.AddMinutes(-11));

        var report = await CreateService(dbContext).GetAsync(null, CancellationToken.None);

        Assert.Equal(TimeSpan.FromMinutes(10), report.StaleAfter);
        Assert.Equal(IngestionHealthState.Stale, report.Health);
    }
}
