namespace VeSessionManager.Worker;

/// <summary>
/// Wraps one iteration of a BackgroundService's timer loop so a failure inside it can never stop
/// the Worker process (2026-08-03).
///
/// .NET's default <c>BackgroundServiceExceptionBehavior</c> is <c>StopHost</c>: anything escaping
/// <c>ExecuteAsync</c> takes down the entire host, not just the job that threw. JobRunHistoryLogger
/// already catches exceptions from a job's *body*, but every job does real work outside it — loading
/// SystemSettings and the team list, peeking the import queue, checking the last ULS slot, stamping
/// LastIngestionRunUtc. Web and Worker share one SQLite file, so a transient "database is locked"
/// at any of those points would stop **every** job in the Worker, permanently, until someone noticed
/// and restarted the service.
///
/// That is the same failure class as the 2026-07-21 incident where an unconfigured Square credential
/// thrown from a constructor killed ExamTools/Zoom/Discord polling too (see CLAUDE.md's Known
/// Constraints) — reached through a different door, since the constructor rule alone does not cover
/// per-tick queries.
///
/// A failed tick is abandoned, not retried inline: every job here is scan-based and idempotent, so
/// the next tick re-derives whatever this one missed. Cancellation is rethrown rather than swallowed
/// so shutdown still stops the loop promptly.
/// </summary>
internal static class JobTick
{
    public static async Task GuardedAsync(ILogger logger, string jobName, Func<Task> tick)
    {
        try
        {
            await tick();
        }
        catch (OperationCanceledException)
        {
            // Shutdown, not a fault — let it propagate so the host stops cleanly.
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{JobName} tick failed outside the job body — abandoning this tick; the job continues on its next one", jobName);
        }
    }
}
