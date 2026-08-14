using VeSessionManager.Core.Jobs;
using VeSessionManager.Core.PiiPurge;
using VeSessionManager.Core.VolunteerExaminers;

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
            await JobTick.GuardedAsync(logger, "PiiPurge", () => RunTickAsync(stoppingToken));
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>
    /// One iteration of this job's work, separated from the timer loop so it can be driven directly
    /// by a test (issue #325). The loop above is three lines of framework usage; every bug this job
    /// has had lived in here.
    /// </summary>
    internal async Task RunTickAsync(CancellationToken stoppingToken)
    {
        using var scope = scopeFactory.CreateScope();
        var jobRunHistoryLogger = scope.ServiceProvider.GetRequiredService<JobRunHistoryLogger>();
        var purgeService = scope.ServiceProvider.GetRequiredService<PiiPurgeService>();
        var selfServiceLinkService = scope.ServiceProvider.GetRequiredService<VeSelfServiceLinkService>();

        await jobRunHistoryLogger.RunAsync(
            "PiiPurge",
            async ct =>
            {
                var result = await purgeService.RunAsync(ct);

                // Spent self-service tokens (audit item D-03). PurgeSpentTokensAsync existed and was
                // called by nothing — an unfinished job rather than dead text, which is why it is
                // wired in here rather than deleted: VeSelfServiceTokens grows without bound and
                // every row carries SentToEmail, a real address. A consumed or expired token is
                // already inert, so this is housekeeping over personal data rather than a security
                // fix, which makes the PII purge run its natural home.
                //
                // Composed HERE rather than inside PiiPurgeService on purpose. PurgeSpentTokensAsync
                // uses ExecuteDeleteAsync, which needs a relational provider — PiiPurgeService's own
                // tests run on EF InMemory, so folding it in would have broken every one of them for
                // a reason that has nothing to do with what they test. The job has real SQLite
                // underneath it (WorkerTickHarness), so this is the layer that can hold it.
                //
                // Unconditional, unlike the two windows above: "already unusable, plus seven days" is
                // not a retention policy anyone needs to choose.
                result.SelfServiceTokensPurged = await selfServiceLinkService.PurgeSpentTokensAsync(ct);
                return result;
            },
            null,
            stoppingToken);
    }

}
