using VeSessionManager.Core.Ingestion;
using VeSessionManager.Core.Jobs;
using VeSessionManager.Core.Scheduling;

namespace VeSessionManager.Worker;

/// <summary>
/// Polls ExamTools on a timer, runs the Session/Candidate ingestion diff (Phase 1), then —
/// per the spec's "hook this into the end of Phase 1's new session detected path" — runs Phase
/// 2's Zoom/Discord scheduling pass in the same tick. Each still gets its own JobRunHistory
/// entry: a scheduling failure shouldn't read as an ingestion failure on the ops dashboard, and
/// vice versa, and scheduling should still run against whatever ingestion already committed even
/// if scheduling itself later fails.
/// </summary>
public class SessionIngestionJob(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSeconds = configuration.GetValue("Jobs:SessionIngestionIntervalSeconds", 300);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));

        do
        {
            using var scope = scopeFactory.CreateScope();
            var jobRunHistoryLogger = scope.ServiceProvider.GetRequiredService<JobRunHistoryLogger>();
            var ingestionService = scope.ServiceProvider.GetRequiredService<SessionIngestionService>();
            var schedulingService = scope.ServiceProvider.GetRequiredService<SessionEventSchedulingService>();

            await jobRunHistoryLogger.RunAsync(
                "SessionIngestion",
                ingestionService.RunAsync,
                stoppingToken);

            await jobRunHistoryLogger.RunAsync(
                "SessionEventScheduling",
                schedulingService.RunAsync,
                stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
