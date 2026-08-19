using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.VecSubmissions;

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
    ArrlSubmissionArchiveStore arrlArchiveStore,
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

        await PurgeArrlArchivesAsync(result, settings, now, cancellationToken);

        return result;
    }

    /// <summary>
    /// Deletes the files filed with ARRL once the window has passed, leaving the row behind.
    ///
    /// <para><b>File first, then the row.</b> Split across two stores there is no transaction, so the
    /// order decides which way an interrupted run fails: this way leaves a deleted file with an
    /// unmarked row, which the next run settles harmlessly. The reverse would mark the row purged and
    /// leave the file on disk forever, with nothing left pointing at it.</para>
    ///
    /// <para><b>Known gap:</b> a crash between writing the files and saving the row leaves orphans no
    /// row can name. Catching those needs a walk of an unbounded directory tree to cover a window of
    /// milliseconds, which is not worth it — recorded here rather than left for someone to rediscover.</para>
    /// </summary>
    private async Task PurgeArrlArchivesAsync(
        RecordRetentionResult result, SystemSettings settings, DateTime now, CancellationToken cancellationToken)
    {
        if (settings.VecSubmissionArchiveRetentionDays is not { } archiveDays)
        {
            logger.LogInformation("ARRL archive retention skipped: SystemSettings.VecSubmissionArchiveRetentionDays is not configured (keeping everything)");
            return;
        }

        var cutoff = now - TimeSpan.FromDays(archiveDays);
        var stale = await dbContext.ArrlVecSubmissions
            .Where(s => s.FilesPurgedUtc == null && s.SubmittedUtc < cutoff)
            .ToListAsync(cancellationToken);

        foreach (var submission in stale)
        {
            DeleteIfPresent(submission.ArchiveStoredPath);
            DeleteIfPresent(submission.AttachmentStoredPath);

            // The stored paths are kept: "there was an archive here and it aged out" is a different
            // answer from "there never was one", and only the first is true.
            submission.FilesPurgedUtc = now;
        }

        if (stale.Count > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        result.ArrlSubmissionArchivesPurged = stale.Count;
        logger.LogInformation(
            "ARRL archive retention: cleared the files of {Count} submission(s) older than {Days} day(s) (before {CutoffUtc:u}); the submission records themselves are kept",
            stale.Count, archiveDays, cutoff);

        void DeleteIfPresent(string? relativePath)
        {
            // Already gone is success, not a retry: a run interrupted between the delete and the save
            // must settle on its next pass rather than loop forever.
            if (arrlArchiveStore.ResolveFullPath(relativePath) is not { } fullPath)
            {
                return;
            }

            try
            {
                File.Delete(fullPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogError(ex, "Could not delete an aged-out ARRL archive at {RelativePath}", relativePath);
            }
        }
    }
}

public class RecordRetentionResult
{
    public int AuditLogsDeleted { get; set; }
    public int JobRunHistoriesDeleted { get; set; }

    /// <summary>Submissions whose filed files were cleared. The rows themselves are never deleted.</summary>
    public int ArrlSubmissionArchivesPurged { get; set; }

    public override string ToString() =>
        $"audit entries deleted {AuditLogsDeleted}, job runs deleted {JobRunHistoriesDeleted}, ARRL archives purged {ArrlSubmissionArchivesPurged}";
}
