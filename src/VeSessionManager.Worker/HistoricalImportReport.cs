using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Ingestion;

namespace VeSessionManager.Worker;

/// <summary>
/// The Worker's <c>--report-historical-imports</c> switch (#88) — lists every existing
/// <c>Session</c> row that was likely created by historical import before
/// <c>Session.ImportedHistoricallyUtc</c> existed to say so directly, so an operator can review the
/// list before any backfill actually writes the flag.
///
/// <para><b>Why a report and not a backfill.</b> Mike, asked directly how to handle the risk of
/// mis-tagging a real session: dry-run report first. A wrong tag here means a real HRCC/MARC session
/// silently stops getting payment reminders and license checks — a correctness bug in the direction
/// nobody notices, which is worse than the thing #88 exists to fix. This command never writes.</para>
///
/// <para><b>Two combinable, imperfect signals</b> — neither reliably identifies every
/// historically-imported session on its own:</para>
/// <list type="bullet">
/// <item><b>The audit trail.</b> <c>SessionIngestionService.MarkVecSubmitted</c> writes a
/// <c>VecSubmissionMarked</c> audit entry keyed by <c>Session.Id</c> whenever an import flips a
/// session's VEC-submission flag — exact, but silent for a session that happened to already be
/// Submitted before the import ran (the method's own early return writes nothing).</item>
/// <item><b>The creation gap.</b> The routine sweep only ever reaches back
/// <see cref="SessionIngestionService.CompletedSessionBackfillWindow"/> (7 days) from "now" — so a
/// session whose <c>CreatedUtc</c> sits far past its own <c>ScheduledStartUtc</c>, beyond that
/// window, could only have arrived via a deliberate historical import. A heuristic, not a proof: a
/// self-hoster who edited the clock, or restored a very old backup, could in principle produce the
/// same shape without ever running an import.</item>
/// </list>
///
/// <para>The report is the union of both, with each row saying which signal(s) matched — an
/// operator with the actual context (which teams were ever historically imported, over what ranges)
/// is the only one who can decide whether a flagged row is real. A separate, explicit backfill step
/// (not built by this pass — see the issue) would apply the reviewed list.</para>
/// </summary>
internal static class HistoricalImportReport
{
    internal static async Task<int> RunAsync(
        AppDbContext dbContext,
        ILogger logger,
        TextWriter output,
        TimeProvider timeProvider,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;

        var auditedSessionIds = await dbContext.AuditLogs
            .Where(a => a.Action == "VecSubmissionMarked" && a.EntityType == "Session")
            .Select(a => a.EntityId)
            .Distinct()
            .ToListAsync(cancellationToken);
        var auditedSet = auditedSessionIds.ToHashSet();

        // Only sessions not already stamped — a stamped row is already handled and is not a backfill
        // candidate, regardless of what the two signals below would say about it.
        var candidates = await dbContext.Sessions
            .Where(s => s.ImportedHistoricallyUtc == null)
            .Select(s => new
            {
                s.Id,
                s.ExamToolsSessionId,
                TeamName = s.Team.Name,
                s.ScheduledStartUtc,
                s.CreatedUtc
            })
            .ToListAsync(cancellationToken);

        var flagged = candidates
            .Select(s => new
            {
                s.Id,
                s.ExamToolsSessionId,
                s.TeamName,
                s.ScheduledStartUtc,
                s.CreatedUtc,
                MatchedByAudit = auditedSet.Contains(s.Id),
                MatchedByGap = s.CreatedUtc - s.ScheduledStartUtc > SessionIngestionService.CompletedSessionBackfillWindow
            })
            .Where(s => s.MatchedByAudit || s.MatchedByGap)
            .OrderBy(s => s.TeamName)
            .ThenBy(s => s.ScheduledStartUtc)
            .ToList();

        logger.LogInformation("Historical-import report: {SessionCount} candidate session(s) not yet flagged out of {TotalCount} total",
            flagged.Count, candidates.Count);

        if (flagged.Count == 0)
        {
            await output.WriteLineAsync("No candidate sessions found — nothing looks like an un-flagged historical import.");
            return 0;
        }

        await output.WriteLineAsync(
            $"{flagged.Count} candidate session(s), review before backfilling Session.ImportedHistoricallyUtc:");
        await output.WriteLineAsync();

        foreach (var session in flagged)
        {
            var signals = new List<string>();
            if (session.MatchedByAudit)
            {
                signals.Add("audit trail (VecSubmissionMarked)");
            }

            if (session.MatchedByGap)
            {
                var gap = session.CreatedUtc - session.ScheduledStartUtc;
                signals.Add($"creation gap ({gap.Days}d — created long after its own scheduled date)");
            }

            await output.WriteLineAsync(
                $"  Session {session.Id} ({session.ExamToolsSessionId}), team {session.TeamName}, " +
                $"scheduled {session.ScheduledStartUtc:yyyy-MM-dd}, created {session.CreatedUtc:yyyy-MM-dd} — " +
                string.Join(", ", signals));
        }

        return 0;
    }
}
