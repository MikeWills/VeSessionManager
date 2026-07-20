using VeSessionManager.Core.Jobs;

namespace VeSessionManager.Worker;

/// <summary>
/// Phase 0 proof-of-pattern: a timer-driven job that runs through JobRunHistoryLogger.
/// Every real job added in later phases (ULS watcher, payment reminders, PII purge, ...)
/// should follow this same shape.
/// </summary>
public class HelloWorldJob(
    IServiceScopeFactory scopeFactory,
    ILogger<HelloWorldJob> logger,
    IConfiguration configuration) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSeconds = configuration.GetValue("Jobs:HelloWorldIntervalSeconds", 60);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));

        do
        {
            using var scope = scopeFactory.CreateScope();
            var jobRunHistoryLogger = scope.ServiceProvider.GetRequiredService<JobRunHistoryLogger>();

            await jobRunHistoryLogger.RunAsync(
                "HelloWorld",
                _ =>
                {
                    logger.LogInformation("Hello, world!");
                    return Task.CompletedTask;
                },
                null,
                stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
