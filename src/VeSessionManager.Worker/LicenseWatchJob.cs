using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Jobs;
using VeSessionManager.Core.Uls;

namespace VeSessionManager.Worker;

/// <summary>
/// Refreshes watched licences from ExamTools' ULS mirror — see docs/renewal-monitor.md.
///
/// <para><b>Anchored to 06:00 ET, once a day</b> (2026-08-06). It previously ticked every four hours
/// from Worker start, which meant its check times drifted with every restart: nobody could say when
/// the next check was without knowing when the service last came up. A renewal granted at FCC's
/// 02:00 ET run was still invisible that morning purely because the row had last been checked at
/// 21:27 the night before.</para>
///
/// <para>The data changes once a night, so polling more often than that buys nothing. One anchored
/// run is both more predictable and *fewer* calls against a third party's undocumented mirror than
/// the four-a-day it replaces. 06:00 ET sits after FCC's run and before anyone opens the page.</para>
///
/// <para>The hour is a constant rather than a SystemSettings row, unlike UlsWatcherJob's. That job
/// is tuned per deployment because it drives candidate grant detection during live sessions; this
/// one has a single job to do and no reason to differ between environments. Promoting it to a
/// setting is a small change if that ever stops being true.</para>
/// </summary>
public class LicenseWatchJob(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<LicenseWatchJob> logger) : BackgroundService
{
    /// <summary>
    /// Anchor hour and interval both come from the shared registry — the admin Job Schedule page
    /// reports this job's timing from the same two values, so they cannot drift apart.
    /// </summary>
    private const int StartHourEt = JobSchedules.LicenseWatchStartHourEt;

    private const int IntervalHours = 24;

    /// <summary>
    /// How often the *slot check* runs, not how often licences are refreshed. Hourly so a Worker that
    /// boots after 06:00 picks up the missed slot within the hour rather than waiting until tomorrow.
    /// </summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TickInterval);

        do
        {
            // Required, not optional: without it a transient "database is locked" from the shared
            // SQLite file would stop the entire Worker, not just this job. See JobTick.
            await JobTick.GuardedAsync(logger, "LicenseWatch", async () =>
            {
                var nowEt = DailySlotSchedule.NowEastern(timeProvider);
                var dueSlotUtc = DailySlotSchedule.LatestDueSlotUtc(nowEt, StartHourEt, IntervalHours);

                using var scope = scopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                // This is what makes the anchor survive restarts and outages: a Worker that boots at
                // 08:47 finds no successful run since today's 06:00 slot and runs it immediately;
                // every later tick that day finds one and skips.
                var alreadyRanThisSlot = await dbContext.JobRunHistories.AnyAsync(
                    h => h.JobName == "LicenseWatch" && h.Success && h.StartedUtc >= dueSlotUtc, stoppingToken);
                if (alreadyRanThisSlot)
                {
                    // `return` (not `continue`) — this is the guarded tick body; returning ends this
                    // tick and the do-while waits for the next hourly one.
                    return;
                }

                var jobRunHistoryLogger = scope.ServiceProvider.GetRequiredService<JobRunHistoryLogger>();
                var watchService = scope.ServiceProvider.GetRequiredService<LicenseWatchService>();

                await jobRunHistoryLogger.RunAsync(
                    "LicenseWatch",
                    watchService.RunAsync,
                    // Global rather than per-team: one scan covers every team's rows, so there is no
                    // single team id to attribute the run to.
                    null,
                    stoppingToken);
            });
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
