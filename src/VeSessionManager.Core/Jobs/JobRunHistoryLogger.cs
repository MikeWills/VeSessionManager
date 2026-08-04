using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
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
        var jobLabel = teamId is null ? jobName : $"{jobName} (team {teamId})";

        var history = new JobRunHistory
        {
            JobName = jobName,
            TeamId = teamId,
            StartedUtc = DateTime.UtcNow
        };

        // Bookkeeping must never cost us the actual work (2026-08-03). Web and Worker share one
        // SQLite file, so this write can fail transiently ("database is locked"); before, that threw
        // straight out of RunAsync, past ExecuteAsync, and — with .NET's default
        // BackgroundServiceExceptionBehavior.StopHost — stopped the whole Worker over a missing
        // history row. The job still runs; only its dashboard entry is lost, which is the right way
        // round.
        var historyTracked = false;
        try
        {
            dbContext.JobRunHistories.Add(history);
            await dbContext.SaveChangesAsync(cancellationToken);
            historyTracked = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            dbContext.Entry(history).State = EntityState.Detached;
            logger.LogError(ex, "Could not write the JobRunHistory start row for {JobLabel} — running the job anyway; this run will be missing from the ops dashboard", jobLabel);
        }

        logger.LogInformation("Starting job: {JobLabel}", jobLabel);
        var stopwatch = Stopwatch.StartNew();

        var failed = false;
        try
        {
            await job(cancellationToken);
            history.Success = true;
            logger.LogInformation("Finished job: {JobLabel} ({ElapsedMs}ms)", jobLabel, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            failed = true;
            history.Success = false;
            history.ErrorMessage = ex.Message;
            logger.LogError(ex, "Job {JobName} failed", jobName);
        }
        finally
        {
            history.CompletedUtc = DateTime.UtcNow;
            await TryCompleteHistoryAsync(history, jobLabel, historyTracked, failed, cancellationToken);
        }
    }

    /// <summary>
    /// Writes the completion half of the history row without ever letting bookkeeping take down the
    /// host (2026-08-03).
    ///
    /// The subtle failure this exists for: this logger shares its scoped DbContext with the job's
    /// own services. When a job fails partway through a SaveChangesAsync, the entity that caused the
    /// failure is *still tracked* — so this save would attempt it again, throw the same error, and
    /// escape through the finally block. One team's bad row would become a full Worker outage.
    /// Clearing the tracker on the failure path drops that poisoned state; the history row is then
    /// re-attached on its own so the failure is still recorded.
    /// </summary>
    private async Task TryCompleteHistoryAsync(JobRunHistory history, string jobLabel, bool historyTracked, bool failed, CancellationToken cancellationToken)
    {
        if (!historyTracked)
        {
            // The start row never persisted, so there is nothing to complete. Anything the failed
            // job left tracked still needs dropping, or the next user of this scoped context inherits it.
            dbContext.ChangeTracker.Clear();
            return;
        }

        try
        {
            if (failed)
            {
                dbContext.ChangeTracker.Clear();
                dbContext.JobRunHistories.Attach(history);
                dbContext.Entry(history).State = EntityState.Modified;
            }

            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not write the JobRunHistory completion row for {JobLabel} — the job itself already ran; only its dashboard entry is incomplete", jobLabel);
            dbContext.ChangeTracker.Clear();
        }
    }
}
