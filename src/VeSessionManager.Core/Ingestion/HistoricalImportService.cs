using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.VolunteerExaminers;

namespace VeSessionManager.Core.Ingestion;

/// <summary>
/// Issue #67 part 2. Queues and runs one-off historical imports — see
/// <see cref="HistoricalImportRequest"/> for why this is a queued request processed by the Worker
/// rather than work done inline in the web request, and docs/historical-import.md for the whole
/// design.
///
/// **Scope is deliberately narrower than the routine pipeline: sessions, candidates, and VE roster
/// only.** No Square payment links, no Zoom/Discord events, no emails. `Session.HasEnded` guards in
/// SessionEventSchedulingService/CandidateNotificationService would suppress most of that anyway,
/// but relying on them as the sole defence for a *year* of backdated data is exactly the wrong
/// posture — generating live checkout links for sessions that finished in March, or emailing
/// "you're registered!" to someone who tested and passed months ago, is the most embarrassing
/// failure mode available here. So those steps are never invoked at all, and the guards stay as the
/// backstop they were meant to be.
/// </summary>
public class HistoricalImportService(
    AppDbContext dbContext,
    SessionIngestionService ingestionService,
    VolunteerExaminerSyncService veRosterSyncService,
    TimeProvider timeProvider,
    ILogger<HistoricalImportService> logger)
{
    /// <summary>
    /// The range is walked one month at a time rather than in a single call. A year in one request
    /// is a heavy, unbounded ask of someone else's servers and gives no progress signal; a month is
    /// comfortably within what the closed-session feed already serves on every routine tick.
    /// </summary>
    public static readonly TimeSpan ChunkPause = TimeSpan.FromSeconds(2);

    /// <summary>Deliberately no cap on how far back a range may reach — the chunking and the pause are what protect ExamTools, not an arbitrary limit on what an operator is allowed to ask for.</summary>
    public async Task<HistoricalImportQueueResult> QueueAsync(
        int teamId, DateOnly startDate, DateOnly endDate, int requestedByUserId, CancellationToken cancellationToken)
    {
        if (endDate < startDate)
        {
            return HistoricalImportQueueResult.InvalidRange;
        }

        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        if (startDate > today)
        {
            // A future range can only ever return nothing — the feed is closed sessions.
            return HistoricalImportQueueResult.InvalidRange;
        }

        // One at a time per team: two concurrent imports would interleave writes to the same
        // sessions and double the load on ExamTools for no benefit. Ingestion is idempotent so the
        // result would still be correct, but the throttling intent would be lost.
        var alreadyQueued = await dbContext.HistoricalImportRequests
            .AnyAsync(r => r.TeamId == teamId
                           && (r.Status == HistoricalImportStatus.Pending || r.Status == HistoricalImportStatus.Running),
                cancellationToken);
        if (alreadyQueued)
        {
            return HistoricalImportQueueResult.AlreadyRunning;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        dbContext.HistoricalImportRequests.Add(new HistoricalImportRequest
        {
            TeamId = teamId,
            StartDate = startDate,
            EndDate = endDate,
            Status = HistoricalImportStatus.Pending,
            RequestedByUserId = requestedByUserId,
            RequestedUtc = now,
            ChunksTotal = CountChunks(startDate, endDate)
        });

        dbContext.AddAuditLog(requestedByUserId, "HistoricalImportQueued", nameof(HistoricalImportRequest), teamId,
            $"Historical session import queued for team {teamId}, {startDate:yyyy-MM-dd} to {endDate:yyyy-MM-dd}.", now);

        await dbContext.SaveChangesAsync(cancellationToken);
        return HistoricalImportQueueResult.Queued;
    }

    /// <summary>Cheap queue peek, so the Worker can skip writing a JobRunHistory row (and the log lines that come with it) when there is nothing to import.</summary>
    public Task<bool> HasPendingAsync(CancellationToken cancellationToken) =>
        dbContext.HistoricalImportRequests.AnyAsync(r => r.Status == HistoricalImportStatus.Pending, cancellationToken);

    /// <summary>Runs the oldest pending request, if any. Returns false when there was nothing to do, so the job can stay quiet.</summary>
    public async Task<bool> RunNextPendingAsync(CancellationToken cancellationToken)
    {
        var request = await dbContext.HistoricalImportRequests
            .Include(r => r.Team)
            .Where(r => r.Status == HistoricalImportStatus.Pending)
            .OrderBy(r => r.RequestedUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (request?.Team is null)
        {
            return false;
        }

        request.Status = HistoricalImportStatus.Running;
        request.StartedUtc = timeProvider.GetUtcNow().UtcDateTime;
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Historical import {RequestId} starting for team {TeamId}: {StartDate}..{EndDate} in {ChunkCount} chunk(s)",
            request.Id, request.TeamId, request.StartDate, request.EndDate, request.ChunksTotal);

        try
        {
            foreach (var (chunkStart, chunkEnd) in Chunks(request.StartDate, request.EndDate))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var result = await ingestionService.ImportHistoricalRangeAsync(request.Team, chunkStart, chunkEnd, cancellationToken);

                // Counters saved after every chunk, not at the end — the page's progress is read
                // from this row, and a crash mid-import must not lose the record of what already
                // landed. Same "save immediately after each item" rule every scan-based job here
                // follows.
                request.ChunksCompleted++;
                request.SessionsImported += result.SessionsAdded;
                request.CandidatesImported += result.CandidatesAdded;
                await dbContext.SaveChangesAsync(cancellationToken);

                logger.LogInformation("Historical import {RequestId}: chunk {Done}/{Total} ({ChunkStart}..{ChunkEnd}) added {Sessions} session(s), {Candidates} candidate(s)",
                    request.Id, request.ChunksCompleted, request.ChunksTotal, chunkStart, chunkEnd, result.SessionsAdded, result.CandidatesAdded);

                await Task.Delay(ChunkPause, timeProvider, cancellationToken);
            }

            // Once, after every chunk — not per chunk. The sync reconciles every Active session for
            // the team in one pass, so running it per chunk would repeat the same work N times.
            await veRosterSyncService.RunAsync(request.Team, cancellationToken);

            request.Status = HistoricalImportStatus.Completed;
            request.CompletedUtc = timeProvider.GetUtcNow().UtcDateTime;
            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Historical import {RequestId} completed: {Sessions} session(s), {Candidates} candidate(s) imported",
                request.Id, request.SessionsImported, request.CandidatesImported);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            request.Status = HistoricalImportStatus.Failed;
            request.CompletedUtc = timeProvider.GetUtcNow().UtcDateTime;
            // Truncated: an ExamTools stack trace is not something to render on an admin page, and
            // the full exception is already in the log with its structured context.
            request.ErrorMessage = ex.Message.Length > 400 ? ex.Message[..400] : ex.Message;
            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogError(ex, "Historical import {RequestId} failed after {Done}/{Total} chunk(s) — earlier chunks are kept; re-queueing the same range resumes rather than duplicating",
                request.Id, request.ChunksCompleted, request.ChunksTotal);
        }

        return true;
    }

    public static int CountChunks(DateOnly startDate, DateOnly endDate) => Chunks(startDate, endDate).Count();

    /// <summary>
    /// Calendar months rather than fixed 30-day blocks, so chunk boundaries line up with how anyone
    /// would describe the range ("January", "February") in the log and the progress readout.
    /// Public for its tests — this is the one piece of pure arithmetic here worth testing directly.
    /// </summary>
    public static IEnumerable<(DateOnly Start, DateOnly End)> Chunks(DateOnly startDate, DateOnly endDate)
    {
        var cursor = startDate;
        while (cursor <= endDate)
        {
            var monthEnd = new DateOnly(cursor.Year, cursor.Month, DateTime.DaysInMonth(cursor.Year, cursor.Month));
            var chunkEnd = monthEnd < endDate ? monthEnd : endDate;
            yield return (cursor, chunkEnd);
            cursor = chunkEnd.AddDays(1);
        }
    }
}

public enum HistoricalImportQueueResult
{
    Queued,
    InvalidRange,

    /// <summary>This team already has a pending or running import — one at a time.</summary>
    AlreadyRunning
}
