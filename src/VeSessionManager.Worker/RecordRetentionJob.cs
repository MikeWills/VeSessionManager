using VeSessionManager.Core.Jobs;
using VeSessionManager.Core.Retention;

namespace VeSessionManager.Worker;

/// <summary>
/// Daily retention pass over the two operational tables nothing ever pruned — AuditLogs (#86) and
/// JobRunHistories (#296). Same 24-hour PeriodicTimer idiom as the other daily jobs; the windows
/// themselves are admin-configurable and read fresh from SystemSettings every run.
///
/// <para>Both windows default to null, meaning keep forever, so on every existing deployment this
/// job wakes up, deletes nothing, and says so. That is the intended resting state.</para>
///
/// <para><b>Not folded into PiiPurgeJob</b>, despite the identical cadence and the precedent of the
/// self-service token purge riding along there. Neither of these tables holds personal data, and a
/// run filed under "PiiPurge" is a run nobody will find when they go looking for why audit history
/// disappeared. The name is the join key back to JobRunHistory.</para>
/// </summary>
public class RecordRetentionJob(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<RecordRetentionJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var descriptor = JobSchedules.For(JobSchedules.RecordRetention);
        var intervalHours = configuration.GetValue(descriptor.IntervalConfigKey!, descriptor.DefaultIntervalHours!.Value);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(intervalHours));

        do
        {
            await JobTick.GuardedAsync(logger, JobSchedules.RecordRetention, () => RunTickAsync(stoppingToken));
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>
    /// One iteration of this job's work, separated from the timer loop so it can be driven directly
    /// by a test (issue #325). The loop above is three lines of framework usage.
    /// </summary>
    internal async Task RunTickAsync(CancellationToken stoppingToken)
    {
        using var scope = scopeFactory.CreateScope();
        var jobRunHistoryLogger = scope.ServiceProvider.GetRequiredService<JobRunHistoryLogger>();
        var retentionService = scope.ServiceProvider.GetRequiredService<RecordRetentionService>();

        await jobRunHistoryLogger.RunAsync(
            JobSchedules.RecordRetention,
            retentionService.RunAsync,
            null,
            stoppingToken);
    }
}
