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
/// runs FccUlsWatcherService.RunDailyAsync once per scheduled slot — 8am and 8pm ET by default
/// (SystemSettings.FccDailyWatcherStartHourEt/FccDailyWatcherIntervalHours) — giving same-day
/// retries instead of a week's wait.
///
/// **Catch-up, not exact-instant (2026-07-28):** the original version only fired when the current
/// Eastern hour matched a slot exactly, so a Worker that was down/restarting right at 8am ET would
/// silently wait a full 12h for the 8pm slot instead of catching up right away. It now instead asks
/// "has today's most recent due slot already run successfully?" on every hourly tick (via
/// JobRunHistory) — a Worker that comes up at 8:47am still catches the missed 8am slot on its very
/// first tick, and a slot that already ran this Worker session is skipped on every later tick within
/// the same window (extra ticks are still free either way — FccUlsWatcherService only ever touches
/// non-terminal candidates, so re-running with the same file's data is a no-op).
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
            var dueSlotUtc = LatestDueSlotUtc(nowEt, startHourEt, intervalHours);

            using var scope = scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var alreadyRanThisSlot = await dbContext.JobRunHistories.AnyAsync(
                h => h.JobName == "FccDailyWatcher" && h.Success && h.StartedUtc >= dueSlotUtc, stoppingToken);
            if (alreadyRanThisSlot)
            {
                continue;
            }

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

    /// <summary>
    /// The most recent scheduled slot (in UTC) that is due as of nowEt — the latest hour matching
    /// (hour - startHourEt) % intervalHours == 0 that isn't in the future. Rolls back across a
    /// calendar-day boundary when needed (e.g. at 3am ET with an 8am/8pm schedule, the due slot is
    /// yesterday's 8pm one) rather than reporting nothing due — a Worker that was down through both
    /// today's slots so far still catches the most recent one on its first tick.
    /// </summary>
    internal static DateTime LatestDueSlotUtc(DateTime nowEt, int startHourEt, int intervalHours)
    {
        var hoursSinceStart = ((nowEt.Hour - startHourEt) % intervalHours + intervalHours) % intervalHours;
        var slotEt = new DateTime(nowEt.Year, nowEt.Month, nowEt.Day, nowEt.Hour, 0, 0, DateTimeKind.Unspecified)
            .AddHours(-hoursSinceStart);
        return TimeZoneInfo.ConvertTimeToUtc(slotEt, FccUlsSchedule.EasternTimeZone);
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
