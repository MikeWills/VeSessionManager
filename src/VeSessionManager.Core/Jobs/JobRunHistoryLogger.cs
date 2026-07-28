using System.Diagnostics;
using Microsoft.Extensions.Logging;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Jobs;

/// <summary>
/// Wraps a background job's run in a JobRunHistory row (start/success/error). Every job in
/// every later phase should call RunAsync rather than logging its own start/end bookkeeping,
/// so JobRunHistory stays a single consistent source for the ops dashboard (Phase 9c).
///
/// Starting/finishing console log lines (issue #21, 2026-07-28): previously the Worker log only
/// showed after-the-fact result summaries (each service's own "X finished: ..." line) with nothing
/// printed when a job actually started, so it was impossible to tell at a glance whether the Worker
/// was mid-run on something or just idle between ticks. Since every job step in every job file
/// already funnels through this one RunAsync, adding the "Starting job"/"Finished job" pair here
/// covers all of them at once rather than needing a change in each job file.
/// </summary>
public class JobRunHistoryLogger(AppDbContext dbContext, ILogger<JobRunHistoryLogger> logger)
{
    public async Task RunAsync(string jobName, Func<CancellationToken, Task> job, int? teamId, CancellationToken cancellationToken)
    {
        var history = new JobRunHistory
        {
            JobName = jobName,
            TeamId = teamId,
            StartedUtc = DateTime.UtcNow
        };
        dbContext.JobRunHistories.Add(history);
        await dbContext.SaveChangesAsync(cancellationToken);

        var jobLabel = teamId is null ? jobName : $"{jobName} (team {teamId})";
        logger.LogInformation("Starting job: {JobLabel}", jobLabel);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await job(cancellationToken);
            history.Success = true;
            logger.LogInformation("Finished job: {JobLabel} ({ElapsedMs}ms)", jobLabel, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            history.Success = false;
            history.ErrorMessage = ex.Message;
            logger.LogError(ex, "Job {JobName} failed", jobName);
        }
        finally
        {
            history.CompletedUtc = DateTime.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }
}
