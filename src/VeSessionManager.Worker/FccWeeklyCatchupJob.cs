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
/// 24-hour PeriodicTimer idiom as every other job here.
///
/// **Retry-on-failure (2026-07-30):** originally ran only on the exact configured weekday (default
/// Monday) with no retry — a single failed attempt (found live: a `403 Forbidden` from
/// `data.fcc.gov`'s `complete/` folder, apparently a transient FCC-side blip since the identical
/// request succeeds when retried) meant the *entire weekly safety net* went dark for a full week,
/// silently, with nothing in the log to flag it. Same catch-up idiom FccDailyWatcherJob already
/// uses: "has this week's due slot already succeeded?" (via JobRunHistory) rather than "is today
/// exactly the target day?" — a failed Monday run now retries on every later tick within the same
/// week (still just once per `intervalHours`, not hammering FCC's ~190MB files repeatedly) until it
/// succeeds, then goes quiet until next week's slot comes due.
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
            var dueSlotUtc = LatestDueSlotUtc(timeProvider.GetUtcNow().UtcDateTime, targetDay);

            using var scope = scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var alreadySucceededThisSlot = await dbContext.JobRunHistories.AnyAsync(
                h => h.JobName == "FccWeeklyCatchup" && h.Success && h.StartedUtc >= dueSlotUtc, stoppingToken);
            if (alreadySucceededThisSlot)
            {
                continue;
            }

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

    /// <summary>The most recent midnight-UTC occurrence of targetDay that isn't in the future — this week's slot once it arrives, still last week's slot on every earlier day.</summary>
    internal static DateTime LatestDueSlotUtc(DateTime nowUtc, DayOfWeek targetDay)
    {
        var daysSinceTarget = ((int)nowUtc.DayOfWeek - (int)targetDay + 7) % 7;
        return nowUtc.Date.AddDays(-daysSinceTarget);
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
