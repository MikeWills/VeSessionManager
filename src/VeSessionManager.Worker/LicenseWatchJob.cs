using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Jobs;
using VeSessionManager.Core.Uls;

namespace VeSessionManager.Worker;

/// <summary>
/// Refreshes watched licenses from ExamTools' ULS mirror — see docs/renewal-monitor.md — and, on
/// the same anchored slot, the VE roster's own licenses (issue #107, docs/ve-license-tracking.md).
///
/// <para><b>Anchored, and it shares <c>UlsWatcherJob</c>'s schedule outright</b> — the same
/// <c>SystemSettings.UlsWatcherStartHourEt</c> and <c>UlsWatcherIntervalHours</c>, which default to
/// <b>08:00 ET, every 12 hours</b>. Anchoring replaced a four-hourly tick from Worker start whose
/// check times drifted with every restart: nobody could say when the next check was without knowing
/// when the service last came up.</para>
///
/// <para><b>This paragraph used to say "06:00 ET, once a day", and that the hour was a constant
/// rather than a settings row.</b> Both stopped being true on 2026-08-06, when the renewal monitor
/// was folded onto the watcher's schedule — same data, same source, one schedule — and neither was
/// corrected. The dead <c>JobSchedules.LicenseWatchStartHourEt = 6</c> and a line in CLAUDE.md said
/// the same wrong thing, so all three places anyone would check agreed with each other and disagreed
/// with the code (issue #301, corrected 2026-08-11). The schedule of record is the descriptor in
/// <see cref="JobSchedules"/>, which the Job Schedule page also reads — that is the point of it.</para>
/// </summary>
public class LicenseWatchJob(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<LicenseWatchJob> logger) : BackgroundService
{
    /// <summary>
    /// Schedule definition shared with the admin Job Schedule page, so the two cannot drift.
    /// </summary>
    private static readonly JobScheduleDescriptor Descriptor = JobSchedules.For(JobSchedules.LicenseWatch);

    /// <summary>
    /// How often the *slot check* runs, not how often licenses are refreshed. Hourly so a Worker that
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
                using var scope = scopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                // Same SystemSettings values the ULS watcher uses — deliberately one schedule for both
                // (2026-08-06). See the class remarks.
                var settings = await scope.ServiceProvider
                    .GetRequiredService<SystemSettingsService>()
                    .GetAsync(stoppingToken);

                var nowEt = DailySlotSchedule.NowEastern(timeProvider);
                var dueSlotUtc = DailySlotSchedule.LatestDueSlotUtc(
                    nowEt,
                    JobSchedules.StartHourOrDefault(settings.UlsWatcherStartHourEt, Descriptor.StartHourEt!.Value),
                    JobSchedules.IntervalOrDefault(settings.UlsWatcherIntervalHours, Descriptor.DefaultIntervalHours!.Value));

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

                // The VE roster's own licenses (issue #107), on the same anchored slot: both read the
                // same nightly FCC data through the same mirror, so a second schedule would be two
                // names for one cadence.
                //
                // A SEPARATE JobRunHistory entry, not a step inside the one above. The slot guard
                // keys on a successful "LicenseWatch" run, so folding these together would mean one
                // failing sweep suppresses the other for the rest of the day — and the ops dashboard
                // could no longer say which half broke.
                var veWatchService = scope.ServiceProvider.GetRequiredService<VolunteerExaminerLicenseWatchService>();

                await jobRunHistoryLogger.RunAsync(
                    "VeLicenseWatch",
                    veWatchService.RunAsync,
                    null,
                    stoppingToken);
            });
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
