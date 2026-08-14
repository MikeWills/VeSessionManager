using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Data;

namespace VeSessionManager.Core.Retention;

/// <summary>
/// Retention for the two append-only operational tables that nothing ever pruned: <c>AuditLogs</c>
/// (#86) and <c>JobRunHistories</c> (#296). Both grow without bound — JobRunHistories at roughly
/// 150k rows a year on this deployment, since TeamPipeline writes six per team per tick.
///
/// <para>Both windows are null by default, meaning keep forever, and a null window skips its pass
/// entirely with one INFO line — the same explicit-opt-in rule as PiiRetentionWindowDays and
/// VeContactRetentionYears. Neither table contains anything that <i>has</i> to be deleted, so the
/// default must be the one that loses nothing.</para>
///
/// <para><b>Read docs/audit-log.md before touching the audit pass.</b> The audit log is append-only
/// by convention, enforced by the absence of any delete path in <c>src/</c> and guarded by
/// <c>AuditLogAppendOnlyTests</c>. This class is the single exemption that test grants, by filename.
/// Keep the deletion here, narrow, and window-driven — a second delete path elsewhere is exactly
/// what that guard exists to catch, and widening the guard to accommodate one would give away the
/// property rather than spend it.</para>
///
/// <para>Separate from PiiPurgeService, and composed at the job layer alongside it, for the reason
/// recorded in PiiPurgeJob: <c>ExecuteDeleteAsync</c> needs a relational provider, and
/// PiiPurgeService's own tests run on EF InMemory. See WorkerTickHarness for the SQLite-backed
/// coverage this gets instead.</para>
/// </summary>
public class RecordRetentionService(
    AppDbContext dbContext,
    SystemSettingsService systemSettingsService,
    TimeProvider timeProvider,
    ILogger<RecordRetentionService> logger)
{
    public async Task<RecordRetentionResult> RunAsync(CancellationToken cancellationToken)
    {
        var result = new RecordRetentionResult();
        var settings = await systemSettingsService.GetAsync(cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        if (settings.AuditLogRetentionDays is { } auditDays)
        {
            var cutoff = now - TimeSpan.FromDays(auditDays);
            result.AuditLogsDeleted = await dbContext.AuditLogs
                .Where(a => a.TimestampUtc < cutoff)
                .ExecuteDeleteAsync(cancellationToken);

            // Logged whenever it is switched on, at INFO, including the count — deleting audit
            // history silently would be its own small betrayal of what the table is for.
            logger.LogInformation(
                "Audit log retention: deleted {Count} entries older than {Days} day(s) (before {CutoffUtc:u})",
                result.AuditLogsDeleted, auditDays, cutoff);
        }
        else
        {
            logger.LogInformation("Audit log retention skipped: SystemSettings.AuditLogRetentionDays is not configured (keeping everything)");
        }

        if (settings.JobRunHistoryRetentionDays is { } jobDays)
        {
            var cutoff = now - TimeSpan.FromDays(jobDays);
            result.JobRunHistoriesDeleted = await dbContext.JobRunHistories
                .Where(h => h.StartedUtc < cutoff)
                .ExecuteDeleteAsync(cancellationToken);

            logger.LogInformation(
                "Job run history retention: deleted {Count} run(s) older than {Days} day(s) (before {CutoffUtc:u})",
                result.JobRunHistoriesDeleted, jobDays, cutoff);
        }
        else
        {
            logger.LogInformation("Job run history retention skipped: SystemSettings.JobRunHistoryRetentionDays is not configured (keeping everything)");
        }

        return result;
    }
}

public class RecordRetentionResult
{
    public int AuditLogsDeleted { get; set; }
    public int JobRunHistoriesDeleted { get; set; }

    public override string ToString() =>
        $"audit entries deleted {AuditLogsDeleted}, job runs deleted {JobRunHistoriesDeleted}";
}
