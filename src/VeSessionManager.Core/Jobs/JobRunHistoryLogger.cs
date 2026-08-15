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
    private const int MaxSummaryLength = 500;

    /// <summary>
    /// Runs a job that reports a result, recording the result's own summary on the history row.
    ///
    /// <para>Every result type in this codebase already overrides <c>ToString()</c> to produce the
    /// exact one-line summary the Worker log prints ("sent 0, failed 1"), so this captures the text
    /// that was already being written to a file nobody reads from the dashboard — rather than
    /// inventing a second, drift-prone description of the same run.</para>
    /// </summary>
    /// <summary>
    /// Returns <c>true</c> when the step completed, <c>false</c> when it threw (#242).
    ///
    /// <para>This logger deliberately catches and does not rethrow — that is what keeps one team's
    /// bad row from taking down the Worker. The cost was that callers could not tell a clean run from
    /// a total failure: a pipeline whose every step threw returned zero counts, and the manual
    /// refresh rendered "Refreshed HRCC — 0 new candidate(s)" in green. Reporting the outcome here
    /// is what lets a caller say something truthful; nothing about the swallow-and-continue
    /// behavior changes.</para>
    ///
    /// <para>Existing callers that ignore the value are unaffected — including the ones that pass
    /// this straight through as a <c>Task</c>, since <c>Task&lt;bool&gt;</c> is one.</para>
    /// </summary>
    public Task<bool> RunAsync<TResult>(string jobName, Func<CancellationToken, Task<TResult>> job, int? teamId, CancellationToken cancellationToken) =>
        RunCoreAsync(jobName, async ct => (await job(ct))?.ToString(), teamId, cancellationToken);

    /// <inheritdoc cref="RunAsync{TResult}(string, Func{CancellationToken, Task{TResult}}, int?, CancellationToken)"/>
    public Task<bool> RunAsync(string jobName, Func<CancellationToken, Task> job, int? teamId, CancellationToken cancellationToken) =>
        RunCoreAsync(jobName, async ct => { await job(ct); return null; }, teamId, cancellationToken);

    /// <summary>Returns whether the job body completed without throwing — see the public overloads.</summary>
    private async Task<bool> RunCoreAsync(string jobName, Func<CancellationToken, Task<string?>> job, int? teamId, CancellationToken cancellationToken)
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
            var summary = await job(cancellationToken);
            history.Success = true;
            // Capped rather than left unbounded: these are one-liners today, but a future result
            // type is one careless interpolation away from putting a wall of text in every row.
            history.ResultSummary = summary is { Length: > MaxSummaryLength }
                ? summary[..MaxSummaryLength]
                : summary;
            logger.LogInformation("Finished job: {JobLabel} ({ElapsedMs}ms)", jobLabel, stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host shutdown, not a fault. Recording it as a failed run put a red row on the ops
            // dashboard every time the Worker was restarted, which trains people to ignore red rows
            // — the opposite of what the dashboard is for. Still marked not-successful, since the
            // run genuinely did not finish; ErrorMessage says why in words rather than a stack.
            failed = true;
            history.Success = false;
            history.ErrorMessage = "Cancelled by host shutdown.";
            logger.LogInformation("Job {JobName} cancelled by host shutdown", jobName);
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
            await TryCompleteHistoryAsync(history, jobLabel, historyTracked, failed);
        }

        return !failed;
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
    private async Task TryCompleteHistoryAsync(JobRunHistory history, string jobLabel, bool historyTracked, bool failed)
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

            // CancellationToken.None on purpose (2026-08-10). This save runs from a finally block,
            // and the commonest reason to reach it is the token having just been cancelled by host
            // shutdown — passing it here meant the completion write was itself cancelled, so the row
            // was left with a null CompletedUtc and showed as perpetually running on the ops
            // dashboard. Worse, TaskCanceledException is an OperationCanceledException, which the
            // filter below deliberately does not catch, so it escaped RunCoreAsync entirely: the
            // exact "bookkeeping takes down the actual work" failure this method exists to prevent.
            // Recording the outcome is fast, local, and must not be cancellable.
            await dbContext.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Catches everything now, including OperationCanceledException. With a None token a
            // cancellation here is not shutdown, it is a genuine fault — and either way, nothing
            // this method does is worth propagating.
            logger.LogError(ex, "Could not write the JobRunHistory completion row for {JobLabel} — the job itself already ran; only its dashboard entry is incomplete", jobLabel);
            dbContext.ChangeTracker.Clear();
        }
    }
}
