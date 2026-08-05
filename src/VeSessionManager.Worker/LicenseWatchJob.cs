using VeSessionManager.Core.Jobs;
using VeSessionManager.Core.Uls;

namespace VeSessionManager.Worker;

/// <summary>
/// Refreshes watched licences from ExamTools' ULS mirror — see docs/renewal-monitor.md.
///
/// <para>Deliberately the plain "tick every N hours from Worker start" idiom rather than
/// UlsWatcherJob's wall-clock-ET slot machinery. That job anchors to 08:00/20:00 ET because FCC runs
/// its issuance at 02:00 ET and a morning poll wants that day's grants to exist. Nothing here is
/// time-of-day sensitive: a licence term is ten years and a renewal takes days to weeks, so an
/// arbitrary tick offset costs nothing. The pre-condition CLAUDE.md sets for reusing this idiom
/// holds — the data is current state on every call, not a one-shot window, so a missed tick
/// self-heals on the next one.</para>
///
/// <para>The service itself decides what is actually due (<see cref="LicenseWatchService.RefreshInterval"/>)
/// and caps how much it does per run, so ticking more often than strictly needed is free.</para>
/// </summary>
public class LicenseWatchJob(
    IServiceScopeFactory scopeFactory,
    ILogger<LicenseWatchJob> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromHours(4);

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
