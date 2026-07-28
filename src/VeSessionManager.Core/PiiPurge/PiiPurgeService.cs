using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.PiiPurge;

/// <summary>
/// Phase 10: nulls candidate PII once the admin-configured retention window has elapsed, anchored
/// to whichever end-of-process date fits the candidate's outcome. Global, not per-team —
/// SystemSettings.PiiRetentionWindowDays is a single deployment-wide value (see docs/pii-purge.md),
/// unlike every Team-scoped credential/setting from Phases 2-4/6.
///
///   - Trigger A (passed): LicenseGrantDateUtc is set and at least RetentionWindowDays days old,
///     anchored on the LATER of LicenseGrantDateUtc/Session.ScheduledStartUtc (not the bare grant
///     date) — an existing licensee re-testing (upgrade or repeat) re-matches their own already-old
///     license, and FCC's Grant Date doesn't change on a class upgrade, so the bare date would purge
///     them almost immediately after a real, current session. See
///     Candidate.LicenseGrantPredatesSession/docs/fcc-uls-watcher.md, found live 2026-07-28.
///   - Trigger B (failed): ApplicationStatus = Failed and the session's ScheduledStartUtc is at
///     least RetentionWindowDays days old — there's no FCC process to track once a Session Manager
///     has recorded a failing result, so the exam date itself is the anchor instead of a license date.
///
/// NotTested is deliberately excluded from both triggers — that PII is nulled immediately at the
/// moment of the Phase 9 delete/no-show action, not on this scheduled window. Unmatched/Received
/// candidates never match either trigger (LicenseGrantDateUtc is only ever set on Granted, and
/// ApplicationStatus == Failed excludes them), so they're naturally never purged regardless of age.
///
/// PiiPurgedUtc null is both the query filter and the idempotency guard, same idiom as every other
/// scan-based service's ...Utc tracking field — a candidate is never reprocessed once purged.
/// </summary>
public class PiiPurgeService(
    AppDbContext dbContext,
    SystemSettingsService systemSettingsService,
    TimeProvider timeProvider,
    ILogger<PiiPurgeService> logger)
{
    public async Task<PiiPurgeResult> RunAsync(CancellationToken cancellationToken)
    {
        var result = new PiiPurgeResult();
        var settings = await systemSettingsService.GetAsync(cancellationToken);

        if (settings.PiiRetentionWindowDays is null)
        {
            // No default assumed per spec — an admin must set this explicitly before the purge job
            // can run. Same "skip quietly, one aggregate INFO line" idiom as an unconfigured optional
            // integration (Zoom/Discord/Square/Email), even though this isn't external-API-shaped.
            logger.LogInformation("PII purge skipped: SystemSettings.PiiRetentionWindowDays is not configured");
            return result;
        }

        var retentionWindowDays = settings.PiiRetentionWindowDays.Value;
        var now = timeProvider.GetUtcNow().UtcDateTime;

        // A candidate is purge-eligible once "today - anchorDate >= retentionWindowDays" measured in
        // whole calendar days. Expressed as an exclusive upper bound (anchorDate < cutoffExclusive)
        // rather than comparing anchorDate.Date against a threshold, so the comparison stays a plain
        // translatable DateTime comparison — same idiom CandidateNotificationService uses for its
        // own "tomorrow" range — regardless of what time-of-day the anchor field carries (a Session's
        // ScheduledStartUtc has a real time-of-day; LicenseGrantDateUtc effectively doesn't).
        var cutoffExclusive = now.Date.AddDays(-retentionWindowDays + 1);

        result.GrantedCandidatesPurged = await PurgeGrantedCandidatesAsync(cutoffExclusive, retentionWindowDays, now, cancellationToken);
        result.FailedCandidatesPurged = await PurgeFailedCandidatesAsync(cutoffExclusive, retentionWindowDays, now, cancellationToken);

        logger.LogInformation("PII purge run finished: {Result}", result);
        return result;
    }

    private async Task<int> PurgeGrantedCandidatesAsync(DateTime cutoffExclusive, int retentionWindowDays, DateTime now, CancellationToken cancellationToken)
    {
        // Anchored on the LATER of LicenseGrantDateUtc/Session.ScheduledStartUtc, not the bare grant
        // date — found live 2026-07-28: an existing licensee re-testing (a class upgrade, or simply
        // testing again) gets re-matched against their own already-old license, and FCC's Grant Date
        // does not change on a class upgrade (confirmed against real ULS data), so the bare date
        // would make this candidate purge-eligible almost immediately after a real, current session.
        // See Candidate.LicenseGrantPredatesSession/docs/fcc-uls-watcher.md. For a genuine new grant
        // (the common case, always after the session), this anchor equals LicenseGrantDateUtc exactly
        // as before — no behavior change there.
        var candidates = await dbContext.Candidates
            .Include(c => c.Session)
            .Include(c => c.Payments)
            .Where(c => c.PiiPurgedUtc == null
                        && c.LicenseGrantDateUtc != null
                        && (c.LicenseGrantDateUtc < c.Session.ScheduledStartUtc ? c.Session.ScheduledStartUtc : c.LicenseGrantDateUtc.Value) < cutoffExclusive)
            .ToListAsync(cancellationToken);

        foreach (var candidate in candidates)
        {
            PurgeCandidate(candidate, now);
            var reason = candidate.LicenseGrantPredatesSession()
                ? $"pre-existing license held since {candidate.LicenseGrantDateUtc:d}, anchored on session date {candidate.Session.ScheduledStartUtc:d} instead"
                : $"license granted {candidate.LicenseGrantDateUtc:d}";
            AddAudit(candidate.Id, $"PII purged (Trigger A: {reason}, {retentionWindowDays}-day retention window elapsed).", now);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return candidates.Count;
    }

    private async Task<int> PurgeFailedCandidatesAsync(DateTime cutoffExclusive, int retentionWindowDays, DateTime now, CancellationToken cancellationToken)
    {
        var candidates = await dbContext.Candidates
            .Include(c => c.Session)
            .Include(c => c.Payments)
            .Where(c => c.PiiPurgedUtc == null
                        && c.ApplicationStatus == CandidateApplicationStatus.Failed
                        && c.Session.ScheduledStartUtc < cutoffExclusive)
            .ToListAsync(cancellationToken);

        foreach (var candidate in candidates)
        {
            PurgeCandidate(candidate, now);
            AddAudit(candidate.Id, $"PII purged (Trigger B: exam date {candidate.Session.ScheduledStartUtc:d}, {retentionWindowDays}-day retention window elapsed).", now);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return candidates.Count;
    }

    // CandidatePiiFields.Clear is the shared definition of "PII cleared" — CallSign/LicenseGrantDateUtc
    // /ApplicationStatus/SessionId and every Payment.Amount/Status/Reason are deliberately left
    // untouched, since they're needed for historical session/VE/financial stats. Unlike the delete
    // action, this purge never touches ApplicationStatus/ResultMarkedBy* — it's a privacy retention
    // action, not a status change.
    private static void PurgeCandidate(Candidate candidate, DateTime now) => CandidatePiiFields.Clear(candidate, now);

    private void AddAudit(int candidateId, string details, DateTime now) =>
        dbContext.AddAuditLog(null, "CandidatePiiPurged", nameof(Candidate), candidateId, details, now); // system action, not a person
}
