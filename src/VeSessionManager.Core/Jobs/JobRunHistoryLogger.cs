using Microsoft.Extensions.Logging;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Jobs;

/// <summary>
/// Wraps a background job's run in a JobRunHistory row (start/success/error). Every job in
/// every later phase should call RunAsync rather than logging its own start/end bookkeeping,
/// so JobRunHistory stays a single consistent source for the ops dashboard (Phase 9c).
/// </summary>
public class JobRunHistoryLogger(AppDbContext dbContext, ILogger<JobRunHistoryLogger> logger)
{
    public async Task RunAsync(string jobName, Func<CancellationToken, Task> job, CancellationToken cancellationToken)
    {
        var history = new JobRunHistory
        {
            JobName = jobName,
            StartedUtc = DateTime.UtcNow
        };
        dbContext.JobRunHistories.Add(history);
        await dbContext.SaveChangesAsync(cancellationToken);

        try
        {
            await job(cancellationToken);
            history.Success = true;
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
