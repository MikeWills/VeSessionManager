using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Jobs;
using VeSessionManager.Core.Uls;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// The admin Job Schedule page's arithmetic. Worth testing rather than eyeballing because a schedule
/// screen is believed: a wrong "next run" is not obviously wrong the way a wrong list is, and someone
/// waiting on it has no reason to doubt it.
/// </summary>
public class JobScheduleServiceTests
{
    // 14:00 UTC on 2026-08-06 is 10:00 ET (EDT, UTC-4) — after the 08:00 ULS slot and after
    // LicenseWatch's 06:00 one, so both jobs' current slots are in the past.
    private static readonly DateTime Now = new(2026, 8, 6, 14, 0, 0, DateTimeKind.Utc);

    /// <summary>Matching the pattern the other test classes here use, rather than pulling in a package for it.</summary>
    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static JobScheduleService CreateService(AppDbContext dbContext, Dictionary<string, string?>? config = null) =>
        new(dbContext,
            new ConfigurationBuilder().AddInMemoryCollection(config ?? []).Build(),
            new FixedTimeProvider(Now));

    private static void SeedRun(AppDbContext dbContext, string jobName, DateTime startedUtc, bool success = true) =>
        dbContext.JobRunHistories.Add(new JobRunHistory
        {
            JobName = jobName,
            StartedUtc = startedUtc,
            CompletedUtc = startedUtc.AddMinutes(1),
            Success = success
        });

    private static async Task<JobScheduleStatus> StatusForAsync(AppDbContext dbContext, string jobName, Dictionary<string, string?>? config = null)
    {
        await dbContext.SaveChangesAsync();
        var statuses = await CreateService(dbContext, config).GetStatusesAsync(CancellationToken.None);
        return statuses.Single(s => s.Descriptor.JobName == jobName);
    }

    /// <summary>Every job the Worker runs must appear, or the page silently under-reports.</summary>
    [Fact]
    public async Task EveryRegisteredJob_IsReported_EvenWithNoHistoryAtAll()
    {
        await using var dbContext = CreateContext();

        var statuses = await CreateService(dbContext).GetStatusesAsync(CancellationToken.None);

        Assert.Equal(JobSchedules.All.Count, statuses.Count);
        Assert.Equal(
            JobSchedules.All.Select(d => d.JobName).OrderBy(n => n),
            statuses.Select(s => s.Descriptor.JobName).OrderBy(n => n));
    }

    // ---- Anchored jobs ---------------------------------------------------------------------------

    /// <summary>
    /// A slot that has already run reports the *following* slot. 08:00/12h means the next one after
    /// 10:00 ET is 20:00 ET the same day.
    /// </summary>
    [Fact]
    public async Task AnchoredJob_WhoseSlotAlreadyRan_ReportsTheNextSlot()
    {
        await using var dbContext = CreateContext();
        SeedRun(dbContext, JobSchedules.UlsWatcher, Now.AddHours(-1)); // 09:00 ET, after the 08:00 slot

        var status = await StatusForAsync(dbContext, JobSchedules.UlsWatcher);

        Assert.Equal(NextRunConfidence.Scheduled, status.Confidence);
        var nextEt = TimeZoneInfo.ConvertTimeFromUtc(status.NextRunUtc!.Value, UlsSchedule.EasternTimeZone);
        Assert.Equal(20, nextEt.Hour);
        Assert.Equal(6, nextEt.Day);
    }

    /// <summary>
    /// The slot is in the past and nothing has run it, so the job is behind — it catches up on its
    /// next hourly tick. Reporting the *following* slot here would be wrong by a whole interval and
    /// would hide that the job is late.
    /// </summary>
    [Fact]
    public async Task AnchoredJob_WhoseSlotHasNotRun_IsDueNow()
    {
        await using var dbContext = CreateContext();
        SeedRun(dbContext, JobSchedules.UlsWatcher, Now.AddDays(-2));

        var status = await StatusForAsync(dbContext, JobSchedules.UlsWatcher);

        Assert.Equal(NextRunConfidence.DueNow, status.Confidence);
        Assert.Null(status.NextRunUtc);
    }

    /// <summary>
    /// A failed run does not satisfy the slot — the job retries it, exactly as the Worker's own
    /// `Success` filter does. Treating any run as satisfying it would report a job as on schedule
    /// while it failed every attempt.
    /// </summary>
    [Fact]
    public async Task AnchoredJob_WhoseOnlyRunFailed_IsStillDueNow()
    {
        await using var dbContext = CreateContext();
        SeedRun(dbContext, JobSchedules.UlsWatcher, Now.AddHours(-1), success: false);

        var status = await StatusForAsync(dbContext, JobSchedules.UlsWatcher);

        Assert.Equal(NextRunConfidence.DueNow, status.Confidence);
        Assert.False(status.LastRunSucceeded);
        Assert.NotNull(status.LastRunUtc); // still shown as "when it last tried"
    }

    /// <summary>The SystemSettings row overrides configuration, matching UlsWatcherJob's own resolution order.</summary>
    [Fact]
    public async Task AnchoredJob_PrefersSystemSettings_OverConfiguration()
    {
        await using var dbContext = CreateContext();
        dbContext.SystemSettings.Add(new SystemSettings { UlsWatcherStartHourEt = 5, UlsWatcherIntervalHours = 24 });
        SeedRun(dbContext, JobSchedules.UlsWatcher, Now.AddHours(-1));

        var status = await StatusForAsync(dbContext, JobSchedules.UlsWatcher,
            new Dictionary<string, string?> { ["Jobs:UlsWatcherIntervalHours"] = "12" });

        var nextEt = TimeZoneInfo.ConvertTimeFromUtc(status.NextRunUtc!.Value, UlsSchedule.EasternTimeZone);
        Assert.Equal(5, nextEt.Hour);   // settings' hour, not the configured 08:00
        Assert.Equal(7, nextEt.Day);    // 24h apart, so tomorrow — not 12h
        Assert.Contains("5:00 AM Eastern", status.CadenceSummary);
    }

    // ---- Interval jobs ---------------------------------------------------------------------------

    [Fact]
    public async Task IntervalJob_ReportsLastRunPlusInterval_AsAnEstimate()
    {
        await using var dbContext = CreateContext();
        var lastRun = Now.AddHours(-2);
        SeedRun(dbContext, JobSchedules.PaymentReminder, lastRun);

        var status = await StatusForAsync(dbContext, JobSchedules.PaymentReminder);

        Assert.Equal(NextRunConfidence.Estimated, status.Confidence);
        Assert.Equal(lastRun.AddHours(24), status.NextRunUtc);
    }

    /// <summary>
    /// Never run means there is genuinely nothing to count from. Inventing "now + interval" would be
    /// fabricating a time — the honest answer is that it is unknown.
    /// </summary>
    [Fact]
    public async Task IntervalJob_NeverRun_ReportsUnknown_RatherThanGuessing()
    {
        await using var dbContext = CreateContext();

        var status = await StatusForAsync(dbContext, JobSchedules.PaymentReminder);

        Assert.Equal(NextRunConfidence.Unknown, status.Confidence);
        Assert.Null(status.NextRunUtc);
        Assert.Null(status.LastRunUtc);
    }

    /// <summary>
    /// An overdue estimate is left in the past rather than rolled forward: it is the visible symptom
    /// of a Worker restart (which resets the timer's cycle) or of the Worker being down.
    /// </summary>
    [Fact]
    public async Task IntervalJob_LongOverdue_KeepsThePastEstimate()
    {
        await using var dbContext = CreateContext();
        SeedRun(dbContext, JobSchedules.PiiPurge, Now.AddDays(-5));

        var status = await StatusForAsync(dbContext, JobSchedules.PiiPurge);

        Assert.True(status.NextRunUtc < Now);
    }

    [Fact]
    public async Task IntervalJob_HonoursAConfiguredInterval()
    {
        await using var dbContext = CreateContext();
        var lastRun = Now.AddMinutes(-1);
        SeedRun(dbContext, JobSchedules.SessionIngestion, lastRun);

        var status = await StatusForAsync(dbContext, JobSchedules.SessionIngestion,
            new Dictionary<string, string?> { ["Jobs:SessionIngestionIntervalSeconds"] = "600" });

        Assert.Equal(lastRun.AddMinutes(10), status.NextRunUtc);
        Assert.Equal("Every 10 minutes", status.CadenceSummary);
    }

    /// <summary>
    /// Manual runs are logged under a "Manual"-prefixed name by TeamPipeline. Counting one would
    /// report the job as freshly run when its timer never fired, and push the next-run estimate out.
    /// </summary>
    [Fact]
    public async Task ManualRuns_AreIgnored_TheySayNothingAboutTheSchedule()
    {
        await using var dbContext = CreateContext();
        SeedRun(dbContext, "Manual" + JobSchedules.SessionIngestion, Now.AddMinutes(-1));

        var status = await StatusForAsync(dbContext, JobSchedules.SessionIngestion);

        Assert.Equal(NextRunConfidence.Unknown, status.Confidence);
        Assert.Null(status.LastRunUtc);
    }

    // ---- The registry itself ---------------------------------------------------------------------

    /// <summary>
    /// Job names are the join key back to JobRunHistory, so a duplicate would make one job's row
    /// report another's timing.
    /// </summary>
    [Fact]
    public void RegisteredJobNames_AreUnique()
    {
        var names = JobSchedules.All.Select(d => d.JobName).ToList();

        Assert.Equal(names.Count, names.Distinct().Count());
    }

    /// <summary>Each shape needs the fields its own arithmetic reads, or the page throws at render time.</summary>
    [Fact]
    public void EveryDescriptor_CarriesWhatItsCadenceNeeds()
    {
        foreach (var descriptor in JobSchedules.All)
        {
            if (descriptor.Kind == JobCadenceKind.AnchoredToEasternHour)
            {
                Assert.True(descriptor.StartHourEt is >= 0 and < 24, $"{descriptor.JobName} has no valid anchor hour");
                Assert.NotNull(descriptor.DefaultIntervalHours);
            }
            else
            {
                Assert.True(
                    descriptor.DefaultIntervalHours is not null || descriptor.DefaultIntervalSeconds is not null,
                    $"{descriptor.JobName} has no fallback interval");
            }
        }
    }
}
