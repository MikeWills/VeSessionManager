using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Data;
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
        // Phase 9c: SystemSettings (DB, admin-editable) is authoritative as of the Worker's next
        // restart after an edit — read once here, not re-checked mid-run. Falls back to the
        // appsettings.json value only if the row is somehow missing (should always exist, seeded by
        // the Phase9cSystemSettings migration).
        var intervalHours = await GetDailyWatcherIntervalHoursAsync(stoppingToken);
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

    private async Task<int> GetDailyWatcherIntervalHoursAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var settings = await dbContext.SystemSettings.FirstOrDefaultAsync(s => s.Id == SystemSettingsService.SingletonId, cancellationToken);
        return settings?.FccDailyWatcherIntervalHours ?? configuration.GetValue("Jobs:FccDailyWatcherIntervalHours", 24);
    }
}
