using VeSessionManager.Core.Ingestion;
using VeSessionManager.Core.Jobs;

namespace VeSessionManager.Worker;

/// <summary>
/// Issue #67 part 2: drains the HistoricalImportRequest queue that Admin → Team Maintenance writes
/// to. See docs/historical-import.md for why the import is queued for the Worker rather than run
/// inline in the web request.
///
/// Ticks often (a queue check is one indexed query against a table that is empty almost always), so
/// an admin who queues an import sees it start within a minute rather than waiting on the ingestion
/// job's own 5-minute cadence. Only one request is processed per tick — a long import must not
/// starve the queue check, and the next tick picks up the next request anyway.
///
/// Unlike every other job here this is **not** a scan against a remote feed; it is genuinely
/// event-driven, because the event (an admin asked for a specific range) carries information no
/// amount of scanning could reconstruct. The idempotency guarantee is the same though: the request
/// row's own Status is both the "needs action" filter and the guard against double-processing, and
/// re-running an already-imported range is harmless because ingestion only ever creates missing
/// sessions.
/// </summary>
public class HistoricalImportJob(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<HistoricalImportJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var descriptor = JobSchedules.For(JobSchedules.HistoricalImport);
        var intervalSeconds = configuration.GetValue(descriptor.IntervalConfigKey!, descriptor.DefaultIntervalSeconds!.Value);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));

        do
        {
            await JobTick.GuardedAsync(logger, "HistoricalImport", async () =>
            {
                using var scope = scopeFactory.CreateScope();
                var importService = scope.ServiceProvider.GetRequiredService<HistoricalImportService>();
                var jobRunHistoryLogger = scope.ServiceProvider.GetRequiredService<JobRunHistoryLogger>();

                // Peek first, and only write a JobRunHistory row when there is genuinely work to do.
                // Logging every empty queue check would bury the ops dashboard under a row a minute —
                // the same "silence means nothing happened" property every other job here relies on.
                var hasPending = await importService.HasPendingAsync(stoppingToken);
                if (!hasPending)
                {
                    // `return` (not `continue`) — this is the guarded tick body, so returning ends
                    // this tick and the do-while goes on to wait for the next one.
                    return;
                }

                await jobRunHistoryLogger.RunAsync(
                    "HistoricalImport",
                    ct => importService.RunNextPendingAsync(ct),
                    // teamId null: the request row carries its own team, and this job step is the queue
                    // drain rather than work on one team's behalf.
                    null,
                    stoppingToken);
            });
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
