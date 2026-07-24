using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.FccUls;
using VeSessionManager.Core.Jobs;

namespace VeSessionManager.Worker;

/// <summary>
/// Phase 5's daily job: FCC publishes each day's amateur application/license transaction files
/// ~5am ET Tue-Sat (see docs/fcc-uls-watcher.md). Unlike every other job in this codebase, this one
/// is pinned to wall-clock time in US Eastern rather than "whenever the Worker process starts" —
/// found live (2026-07-23) that a Worker-start-relative 24h timer can tick before that day's file
/// is published, and because each day-name file (a_am_wed.zip etc.) is a fixed URL only holding
/// that one day's transactions, a missed tick isn't revisited until the same day-of-week rolls
/// around again a full week later. FccWeeklyCatchupJob's "complete" snapshot was found NOT to be a
/// reliable same-week backstop for this — a real filing was confirmed still absent from that
/// snapshot a full day after appearing in the daily file. So this job instead ticks hourly and
/// actually runs FccUlsWatcherService.RunDailyAsync only when the current Eastern hour matches
/// SystemSettings.FccDailyWatcherStartHourEt (default 8) plus every FccDailyWatcherIntervalHours
/// (default 12) after that — i.e. 8am and 8pm ET by default, giving same-day retries instead of a
/// week's wait. Extra ticks that don't match are free: FccUlsWatcherService only ever touches
/// non-terminal candidates, so re-running with the same file's data is a no-op, same "extra tick
/// costs nothing" idempotency every job here relies on.
/// </summary>
public class FccDailyWatcherJob(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    TimeProvider timeProvider) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));

        do
        {
            // Phase 9c: SystemSettings (DB, admin-editable) is authoritative as of the Worker's next
            // restart after an edit. Re-read every tick (unlike other jobs' one-time read) since this
            // job now ticks hourly regardless and needs the current start-hour/interval to decide
            // whether *this* tick is one it should act on.
            var (intervalHours, startHourEt) = await GetDailyWatcherSettingsAsync(stoppingToken);

            var nowEt = TimeZoneInfo.ConvertTimeFromUtc(timeProvider.GetUtcNow().UtcDateTime, FccUlsSchedule.EasternTimeZone);
            var hoursSinceStart = ((nowEt.Hour - startHourEt) % intervalHours + intervalHours) % intervalHours;
            if (hoursSinceStart != 0)
            {
                continue;
            }

            using var scope = scopeFactory.CreateScope();
            var jobRunHistoryLogger = scope.ServiceProvider.GetRequiredService<JobRunHistoryLogger>();
            var watcherService = scope.ServiceProvider.GetRequiredService<FccUlsWatcherService>();

            await jobRunHistoryLogger.RunAsync(
                "FccDailyWatcher",
                watcherService.RunDailyAsync,
                null,
                stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task<(int IntervalHours, int StartHourEt)> GetDailyWatcherSettingsAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var settings = await dbContext.SystemSettings.FirstOrDefaultAsync(s => s.Id == SystemSettingsService.SingletonId, cancellationToken);
        if (settings is not null)
        {
            return (settings.FccDailyWatcherIntervalHours, settings.FccDailyWatcherStartHourEt);
        }

        return (
            configuration.GetValue("Jobs:FccDailyWatcherIntervalHours", 12),
            configuration.GetValue("Jobs:FccDailyWatcherStartHourEt", 8));
    }
}
