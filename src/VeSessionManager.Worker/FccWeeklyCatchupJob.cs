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
        var intervalHours = configuration.GetValue("Jobs:FccWeeklyCatchupIntervalHours", 24);
        var targetDay = configuration.GetValue("Jobs:FccWeeklyCatchupDayOfWeek", DayOfWeek.Monday);
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
}
