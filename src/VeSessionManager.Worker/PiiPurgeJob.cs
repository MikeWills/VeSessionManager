using VeSessionManager.Core.Jobs;
using VeSessionManager.Core.PiiPurge;

namespace VeSessionManager.Worker;

/// <summary>
/// Phase 10's daily job: nulls candidate PII once SystemSettings.PiiRetentionWindowDays has
/// elapsed. Same 24-hour PeriodicTimer idiom as the other daily jobs. Global, not per-team — unlike
/// UlsWatcherJob (whose *run interval* is admin-configurable per
/// SystemSettings), the purge job's interval is a fixed config value; only the retention window
/// itself is admin-configurable, and PiiPurgeService reads that fresh from SystemSettings every run.
/// </summary>
public class PiiPurgeJob(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<PiiPurgeJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var descriptor = JobSchedules.For(JobSchedules.PiiPurge);
        var intervalHours = configuration.GetValue(descriptor.IntervalConfigKey!, descriptor.DefaultIntervalHours!.Value);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(intervalHours));

        do
        {
            await JobTick.GuardedAsync(logger, "PiiPurge", async () =>
            {
                using var scope = scopeFactory.CreateScope();
                var jobRunHistoryLogger = scope.ServiceProvider.GetRequiredService<JobRunHistoryLogger>();
                var purgeService = scope.ServiceProvider.GetRequiredService<PiiPurgeService>();

                await jobRunHistoryLogger.RunAsync("PiiPurge", purgeService.RunAsync, null, stoppingToken);
            });
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
