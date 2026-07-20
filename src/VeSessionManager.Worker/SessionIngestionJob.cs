using VeSessionManager.Core.Ingestion;
using VeSessionManager.Core.Jobs;

namespace VeSessionManager.Worker;

/// <summary>
/// Phase 1: polls ExamTools on a timer and runs the Session/Candidate ingestion diff.
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

            await jobRunHistoryLogger.RunAsync(
                "SessionIngestion",
                ingestionService.RunAsync,
                stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
