using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.FccUls;
using VeSessionManager.Core.Jobs;

namespace VeSessionManager.Worker;

/// <summary>
/// Phase 5's weekly catch-up: re-scans FCC's full "complete" amateur application/license files
/// (a_amat.zip / l_amat.zip) against every non-terminal candidate, covering any day
/// FccDailyWatcherJob missed (e.g. a maintenance-window gap in the daily files). Ticks on the same
/// 24-hour PeriodicTimer idiom as every other job here, but only actually runs the scan on the
/// configured weekday (default Monday, per the spec) — every other day's tick is a no-op, same
/// "extra tick costs nothing" spirit as DayBeforeReminderJob's send-once tracking.
/// </summary>
public class FccWeeklyCatchupJob(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    TimeProvider timeProvider) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Phase 9c: SystemSettings (DB, admin-editable) is authoritative as of the Worker's next
        // restart after an edit — read once here, not re-checked mid-run. Falls back to the
        // appsettings.json values only if the row is somehow missing.
        var (intervalHours, targetDay) = await GetWeeklyCatchupSettingsAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(intervalHours));

        do
        {
            if (timeProvider.GetUtcNow().UtcDateTime.DayOfWeek != targetDay)
            {
                continue;
            }

            using var scope = scopeFactory.CreateScope();
            var jobRunHistoryLogger = scope.ServiceProvider.GetRequiredService<JobRunHistoryLogger>();
            var watcherService = scope.ServiceProvider.GetRequiredService<FccUlsWatcherService>();

            await jobRunHistoryLogger.RunAsync(
                "FccWeeklyCatchup",
                watcherService.RunWeeklyCatchupAsync,
                null,
                stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task<(int IntervalHours, DayOfWeek TargetDay)> GetWeeklyCatchupSettingsAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var settings = await dbContext.SystemSettings.FirstOrDefaultAsync(s => s.Id == SystemSettingsService.SingletonId, cancellationToken);
        if (settings is not null)
        {
            return (settings.FccWeeklyCatchupIntervalHours, settings.FccWeeklyCatchupDayOfWeek);
        }

        return (
            configuration.GetValue("Jobs:FccWeeklyCatchupIntervalHours", 24),
            configuration.GetValue("Jobs:FccWeeklyCatchupDayOfWeek", DayOfWeek.Monday));
    }
}
