using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Jobs;
using VeSessionManager.Core.Uls;

namespace VeSessionManager.Worker;

/// <summary>
/// Checks ExamTools' ULS mirror for license grants twice a day — 08:00 and 20:00 ET by default
/// (SystemSettings.UlsWatcherStartHourEt/UlsWatcherIntervalHours).
///
/// <para>Replaced FccDailyWatcherJob + FccWeeklyCatchupJob on 2026-07-31 (see docs/uls-watcher.md).
/// The scheduling machinery is deliberately unchanged from FccDailyWatcherJob — it ticks hourly and
/// asks "has today's most recent due slot already run successfully?" via JobRunHistory, so a Worker
/// that comes up at 08:47 still catches the missed 08:00 slot on its first tick, and later ticks in
/// the same window are skipped. What went away is the *reason* the old job needed a
/// weekly-catchup sibling: an FCC day-name file was a one-shot window that could be missed
/// permanently, whereas a ULS lookup returns current state on every call, so a missed tick costs at
/// most one slot's latency and self-heals. Extra ticks are free — the service only ever touches
/// non-terminal candidates.</para>
///
/// <para>Wall-clock ET rather than "whenever the Worker started" is kept because FCC's own issuance
/// runs at 02:00 ET, so a morning slot lands after that day's grants exist.</para>
/// </summary>
public class UlsWatcherJob(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<UlsWatcherJob> logger) : BackgroundService
{
    /// <summary>
    /// Shared schedule definition — the admin Job Schedule page reports this job's cadence from the
    /// same descriptor, including the same config keys and defaults, so the two cannot disagree.
    /// </summary>
    private static readonly JobScheduleDescriptor UlsDescriptor = JobSchedules.For(JobSchedules.UlsWatcher);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));

        do
        {
            await JobTick.GuardedAsync(logger, "UlsWatcher", () => RunTickAsync(stoppingToken));
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task<(int IntervalHours, int StartHourEt)> GetSettingsAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var settings = await dbContext.SystemSettings.FirstOrDefaultAsync(s => s.Id == SystemSettingsService.SingletonId, cancellationToken);
        if (settings is not null)
        {
            return (JobSchedules.IntervalOrDefault(settings.UlsWatcherIntervalHours, UlsDescriptor.DefaultIntervalHours!.Value),
                    JobSchedules.StartHourOrDefault(settings.UlsWatcherStartHourEt, UlsDescriptor.StartHourEt!.Value));
        }

        return (
            configuration.GetValue(UlsDescriptor.IntervalConfigKey!, UlsDescriptor.DefaultIntervalHours!.Value),
            configuration.GetValue("Jobs:UlsWatcherStartHourEt", UlsDescriptor.StartHourEt!.Value));
    }

    /// <summary>
    /// One iteration of this job's work, separated from the timer loop so it can be driven directly
    /// by a test (issue #325). The loop above is three lines of framework usage; every bug this job
    /// has had lived in here.
    /// </summary>
    internal async Task RunTickAsync(CancellationToken stoppingToken)
    {
        var (intervalHours, startHourEt) = await GetSettingsAsync(stoppingToken);

        var nowEt = DailySlotSchedule.NowEastern(timeProvider);
        var dueSlotUtc = DailySlotSchedule.LatestDueSlotUtc(nowEt, startHourEt, intervalHours);

        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var alreadyRanThisSlot = await dbContext.JobRunHistories.AnyAsync(
            h => h.JobName == "UlsWatcher" && h.Success && h.StartedUtc >= dueSlotUtc, stoppingToken);
        if (alreadyRanThisSlot)
        {
            // `return` (not `continue`) — this is the guarded tick body; returning ends this
            // tick and the do-while waits for the next hourly one, same as before.
            return;
        }

        var jobRunHistoryLogger = scope.ServiceProvider.GetRequiredService<JobRunHistoryLogger>();
        var watcherService = scope.ServiceProvider.GetRequiredService<UlsWatcherService>();

        await jobRunHistoryLogger.RunAsync(
            "UlsWatcher",
            watcherService.RunAsync,
            null,
            stoppingToken);
    }

}
