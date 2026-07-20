using VeSessionManager.Core.FccUls;
using VeSessionManager.Core.Jobs;

namespace VeSessionManager.Worker;

/// <summary>
/// Phase 5's daily job: FCC publishes each day's amateur application/license transaction files
/// ~5am ET Tue-Sat (see docs/fcc-uls-watcher.md). Runs on a 24-hour PeriodicTimer starting from
/// whenever the Worker process starts, same idiom as DayBeforeReminderJob — not pinned to a
/// specific wall-clock time. A day with no file published yet (before ~5am ET, or a genuine
/// maintenance-window gap) is a normal, silently-skipped result, not a failure; FccWeeklyCatchupJob
/// exists specifically to cover any day missed this way.
/// </summary>
public class FccDailyWatcherJob(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalHours = configuration.GetValue("Jobs:FccDailyWatcherIntervalHours", 24);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(intervalHours));

        do
        {
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
}
