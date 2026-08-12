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

    /// <summary>
    /// The report is built from a projection now, not from Team entities, so IsExamToolsConfigured
    /// is recomputed from projected columns rather than read off the entity. These pin the two
    /// together — the whole point of projecting was to avoid decrypting ExamToolsPassword, and the
    /// easy way to get that wrong is to change what "configured" means in the process.
    /// </summary>
    [Theory]
    [InlineData("code", "user", "pw", true)]
    [InlineData(null, "user", "pw", false)]
    [InlineData("code", null, "pw", false)]
    [InlineData("code", "user", null, false)]
    [InlineData("", "user", "pw", false)]
    [InlineData("   ", "user", "pw", false)]   // whitespace-only, a plaintext column
    [InlineData("code", "   ", "pw", false)]   // whitespace-only, a plaintext column
    public async Task ProjectedExamToolsConfigured_MatchesTheEntityRule(
        string? teamCode, string? username, string? password, bool expected)
    {
        await using var dbContext = CreateContext();
        await SeedSettingsAsync(dbContext);
        var team = new Team
        {
            Name = "T",
            ExamToolsTeamCode = teamCode,
            ExamToolsUsername = username,
            ExamToolsPassword = password,
            CreatedUtc = Now,
            LastIngestionRunUtc = Now
        };
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();

        var report = await CreateService(dbContext).GetAsync(null, CancellationToken.None);

        var row = Assert.Single(report.Teams);
        Assert.Equal(expected, row.IsExamToolsConfigured);
        // And it agrees with the entity property it mirrors, not merely with the expectation above.
        Assert.Equal(team.IsExamToolsConfigured, row.IsExamToolsConfigured);
    }

    /// <summary>
    /// A whitespace-only password used to be "the one case the projection cannot see", and this test
    /// asserted the divergence on purpose: the presence test ran in SQL against a stored value that
    /// is ciphertext under real encryption, and ciphertext is never whitespace.
    ///
    /// <para><b>#279 removed the limitation rather than working around it.</b> The same reasoning that
    /// hid whitespace also hid a cleared password — <c>Protect("")</c> is non-empty ciphertext too —
    /// and that one was not harmless: the page reported a team as configured and due while the job
    /// correctly skipped it, with nothing logged either side. The presence test decrypts and runs in
    /// memory now, so there is no case the projection cannot see, and this asserts the absence of the
    /// divergence it used to document.</para>
    /// </summary>
    [Fact]
    public async Task WhitespaceOnlyPassword_NowAgreesWithTheEntity()
    {
        await using var dbContext = CreateContext();
        await SeedSettingsAsync(dbContext);
        var team = new Team
        {
            Name = "T",
            ExamToolsTeamCode = "code",
            ExamToolsUsername = "user",
            ExamToolsPassword = "   ",
            CreatedUtc = Now,
            LastIngestionRunUtc = Now
        };
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();

        var report = await CreateService(dbContext).GetAsync(null, CancellationToken.None);

        Assert.False(Assert.Single(report.Teams).IsExamToolsConfigured);
        Assert.False(team.IsExamToolsConfigured);
    }
}
