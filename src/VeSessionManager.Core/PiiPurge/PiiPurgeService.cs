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
        result.AlreadyPurgedCandidatesRepaired = await RepairIncompletelyPurgedCandidatesAsync(now, cancellationToken);

        result.VolunteerExaminersPurged = await PurgeInactiveVolunteerExaminersAsync(now, settings.VeContactRetentionYears, cancellationToken);

        logger.LogInformation("PII purge run finished: {Result}", result);
        return result;
    }

    /// <summary>
    /// Clears the contact details of VEs who have stopped volunteering (#313 / L-07).
    ///
    /// <para><b>"Inactive" is two conditions, and both are load-bearing.</b> A VE is eligible only
    /// when they hold <i>no active team membership</i> AND have worked no session inside the window.
    /// Either alone is wrong: a current roster member who simply had a quiet couple of years would
    /// lose the email address their team invites them with, and a VE freshly added to a roster has
    /// never worked a session at all, so a last-worked test on its own would purge them the day they
    /// join.</para>
    ///
    /// <para><b>Never worked, never on a roster</b> falls back to CreatedUtc, so an imported row that
    /// went nowhere still ages out rather than living forever on a technicality — while a
    /// just-created one is safe, since its CreatedUtc is today.</para>
    ///
    /// <para>Merged-away duplicates (MergedIntoVolunteerExaminerId set) are eligible on the same
    /// terms rather than immediately: the merge target carries the person forward, so the loser row's
    /// details are already redundant — but purging it early would be a second rule to reason about
    /// for no practical gain.</para>
    ///
    /// <para>What is kept is the accreditation trail — name, call sign, FRN, accreditations, session
    /// history. See <see cref="VolunteerExaminerPiiFields"/> and docs/ve-retention.md.</para>
    /// </summary>
    private async Task<int> PurgeInactiveVolunteerExaminersAsync(DateTime now, int? retentionYears, CancellationToken cancellationToken)
    {
        if (retentionYears is null)
        {
            // Same explicit-opt-in rule as the candidate window, and a stronger case for it: nobody
            // expects a volunteer roster to start forgetting people because a job shipped.
            logger.LogInformation("VE contact purge skipped: SystemSettings.VeContactRetentionYears is not configured");
            return 0;
        }

        var cutoffExclusive = now.Date.AddYears(-retentionYears.Value);

        // Selected on "still carries a contact field" rather than "PiiPurgedUtc is null". Filtering
        // on the stamp would skip rows purged before a field was added to the definition — the exact
        // gap that needed RepairIncompletelyPurgedCandidatesAsync on the candidate side, avoided here
        // by asking what is actually present instead of what was recorded as done.
        var candidates = await dbContext.VolunteerExaminers
            .Where(v => v.Email != null || v.Phone != null || v.AddressLine1 != null || v.AddressLine2 != null
                || v.City != null || v.State != null || v.PostalCode != null || v.DiscordUsername != null
                || v.Notes != null)
            .Where(v => !v.TeamMemberships.Any(m => m.IsActive))
            .Select(v => new
            {
                VolunteerExaminer = v,
                LastWorkedUtc = dbContext.SessionVolunteerExaminers
                    .Where(l => l.VolunteerExaminerId == v.Id)
                    .Max(l => (DateTime?)l.Session.ScheduledStartUtc)
            })
            .ToListAsync(cancellationToken);

        var purged = 0;
        foreach (var row in candidates)
        {
            var lastActivity = row.LastWorkedUtc ?? row.VolunteerExaminer.CreatedUtc;
            if (lastActivity >= cutoffExclusive)
            {
                continue;
            }

            VolunteerExaminerPiiFields.Clear(row.VolunteerExaminer, now);

            // Ids only, and no contact details in the message. The whole point of this pass is that
            // those details stop existing, so writing them into the audit log on the way out would
            // be self-defeating.
            dbContext.AddAuditLog(null, "VolunteerExaminerPiiPurged", nameof(VolunteerExaminer), row.VolunteerExaminer.Id,
                $"Contact details cleared after {retentionYears} year(s) inactive.", now);

            // Saved per row, like every other scan-based job here, so a crash mid-run never loses the
            // progress already made or repeats it.
            await dbContext.SaveChangesAsync(cancellationToken);
            purged++;
        }

        return purged;
    }

    /// <summary>
    /// Re-clears rows that were purged before FirstName was added to CandidatePiiFields.Clear
    /// (2026-08-03): those candidates carry PiiPurgedUtc, so both triggers above skip them forever,
    /// yet still hold a given name. Scan-based and self-healing rather than a one-off migration
    /// script — the same idiom as the ExtId and license-class backfills — so it needs no deployment
    /// step and costs one indexed-null check per run once the backlog is drained.
    ///
    /// The *action* is the whole shared Clear rather than a targeted `FirstName = null`, because
    /// Clear is idempotent and re-running it costs nothing. The *detection* is deliberately narrow:
    /// `FirstName != null` is the signature of this specific historical gap. *A future field added
    /// to Clear will NOT be repaired by this predicate* — by then FirstName is null everywhere, so
    /// no row matches. Adding a field to Clear therefore means widening this filter to include it
    /// (`|| NewField != null`), or the same class of stale row reappears silently.
    ///
    /// PiiPurgedUtc is preserved, not restamped — the purge date records when retention actually
    /// expired, not when this repair happened to run.
    /// </summary>
    private async Task<int> RepairIncompletelyPurgedCandidatesAsync(DateTime now, CancellationToken cancellationToken)
    {
        var candidates = await dbContext.Candidates
            .Include(c => c.Payments)
            .Where(c => c.PiiPurgedUtc != null && c.FirstName != null)
            .ToListAsync(cancellationToken);

        foreach (var candidate in candidates)
        {
            var originalPurgedUtc = candidate.PiiPurgedUtc!.Value;
            CandidatePiiFields.Clear(candidate, originalPurgedUtc);
            AddAudit(candidate.Id, "PII re-cleared: fields added to the purge definition after this candidate was originally purged.", now, candidate.Session.TeamId);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        if (candidates.Count > 0)
        {
            logger.LogInformation("PII purge repaired {Count} previously-purged candidate(s) that still held fields added to the purge definition later", candidates.Count);
        }

        return candidates.Count;
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
            AddAudit(candidate.Id, $"PII purged (Trigger A: {reason}, {retentionWindowDays}-day retention window elapsed).", now, candidate.Session.TeamId);
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
            AddAudit(candidate.Id, $"PII purged (Trigger B: exam date {candidate.Session.ScheduledStartUtc:d}, {retentionWindowDays}-day retention window elapsed).", now, candidate.Session.TeamId);
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

    /// <param name="teamId">
    /// The candidate's session's team, so a TeamAdmin can see their own team's purges (#86 part 3) —
    /// before this, every entry here was invisible to anyone but a SystemAdmin, because a job has no
    /// acting user to scope through. Every caller has <c>candidate.Session</c> loaded.
    /// </param>
    private void AddAudit(int candidateId, string details, DateTime now, int teamId) =>
        dbContext.AddAuditLog(null, "CandidatePiiPurged", nameof(Candidate), candidateId, details, now,
            teamId: teamId); // userId null: system action, not a person
}
