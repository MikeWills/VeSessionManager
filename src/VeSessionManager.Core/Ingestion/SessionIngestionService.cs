using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.ExamTools;

namespace VeSessionManager.Core.Ingestion;

/// <summary>
/// Phase 1 ingestion: diffs the polled ExamTools feed against the local Session/Candidate tables.
/// Data-only — no Zoom/Discord/Square/email side effects happen here (later phases hook onto the
/// state changes this makes). PII rule: log ExamTools ids and counts, never names/emails/FRNs.
/// </summary>
public class SessionIngestionService(
    AppDbContext dbContext,
    IExamToolsClient examToolsClient,
    TimeProvider timeProvider,
    IOptions<ExamToolsOptions> examToolsOptions,
    ILogger<SessionIngestionService> logger)
{
    private const string PendingState = "pend";
    private const string DoneState = "done";

    /// <summary>Fallback when ExamTools reports no duration (or 0) for a session's sessionDef.</summary>
    private const int DefaultDurationMinutes = 60;

    /// <summary>
    /// The real feed contains stale "pend" sessions that were never closed out upstream — still
    /// "pend" but years old (observed on examtools.dev). Never first-ingesting a "pend" session
    /// already this far past its start keeps Phase 2 from creating Zoom/Discord events for dead
    /// sessions, while the grace window tolerates polling right as a session starts/runs.
    /// </summary>
    private static readonly TimeSpan NewSessionPastGrace = TimeSpan.FromDays(1);

    /// <summary>
    /// A "done" session was never first-ingested at all before this (issue #22) — teams want to
    /// start tracking past candidates/VE stats for sessions that already happened. Bounded to ~30
    /// days back for the same reason NewSessionPastGrace is bounded: the feed returns unfiltered
    /// full history, and a "done" session from years ago is exactly as undesirable to backfill as a
    /// zombie "pend" one. SessionEventSchedulingService/CandidateNotificationService separately
    /// guard against live-scheduling or emailing a session ingested this way once it's already over
    /// (Session.HasEnded) — this window only controls whether the row gets created at all.
    /// </summary>
    private static readonly TimeSpan CompletedSessionBackfillWindow = TimeSpan.FromDays(30);

    public async Task<IngestionResult> RunAsync(Team team, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var result = new IngestionResult();

        if (!team.IsExamToolsConfigured)
        {
            // ExamTools is the one hard requirement at the whole-app level, but once credentials
            // are per-Team, an individual team that hasn't finished setup yet must not error-log
            // every poll — same skip-quietly convention as every optional integration elsewhere.
            logger.LogInformation("Team {TeamId} ({TeamName}) has no ExamTools credentials configured yet — skipping ingestion until Team.ExamToolsTeamCode/Username/Password are set",
                team.Id, team.Name);
            return result;
        }

        var credentials = ExamToolsCredentials.For(team, examToolsOptions.Value.BaseUrl);

        var remoteSessions = (await examToolsClient.GetTeamSessionsAsync(credentials, cancellationToken)).ToList();

        // GetTeamSessionsAsync never returns a closed ("done") session, confirmed live 2026-07-28 —
        // closed sessions only exist behind this separate date-range feed. Merge the two, preferring
        // the pend feed's own copy of a session id if (implausibly) both returned it.
        var closedSessions = await examToolsClient.GetTeamClosedSessionsAsync(
            credentials, DateOnly.FromDateTime(now - CompletedSessionBackfillWindow), DateOnly.FromDateTime(now.AddDays(1)), cancellationToken);
        var pendIds = remoteSessions.Select(r => r.Id).ToHashSet();
        remoteSessions.AddRange(closedSessions.Where(c => !pendIds.Contains(c.Id)));

        var remoteIds = remoteSessions.Select(r => r.Id).ToHashSet();

        // Scoped to this team — otherwise another team's still-active sessions (never in this
        // team's own remoteIds, since ExamTools' feed is per-team) would look "disappeared" below
        // and get wrongly marked Cancelled.
        var localSessions = await dbContext.Sessions.Include(s => s.Candidates).Where(s => s.TeamId == team.Id).ToListAsync(cancellationToken);
        var localByExternalId = localSessions.ToDictionary(s => s.ExamToolsSessionId);

        foreach (var remote in remoteSessions)
        {
            if (localByExternalId.TryGetValue(remote.Id, out var local))
            {
                ApplyRescheduleRules(local, remote, now, result);

                // Backfill (2026-07-30): ExtId was added after many sessions were already ingested;
                // same "fill in a null field on the next poll" idiom as ExamResultSyncService's
                // license-class backfill — no one-off migration script needed, every existing
                // session picks it up the next time it's still in the feed.
                local.ExtId ??= remote.SessionDef?.ExtId;
            }
            else if (ShouldIngestNewSession(remote, now))
            {
                var created = await TryCreateSessionAsync(remote, team, now, result, cancellationToken);
                if (created is not null)
                {
                    localSessions.Add(created);
                    localByExternalId[remote.Id] = created;
                }
            }
            // Anything else (unknown state, or too far past its respective window) is history from
            // before this tool existed, or too stale to be worth backfilling — not ingested.
        }

        // Cancellation: ExamTools has no cancelled flag; a known, still-open session vanishing
        // from the feed by id *is* the cancellation signal (confirmed against real API responses).
        foreach (var local in localSessions.Where(s =>
                     s.Id != 0 && // freshly added this run — always still in the feed
                     s.Status == SessionStatus.Active &&
                     s.TestingCompletedUtc is null &&
                     !remoteIds.Contains(s.ExamToolsSessionId)))
        {
            local.Status = SessionStatus.Cancelled;
            local.CancelledUtc = now;
            result.SessionsCancelled++;
            logger.LogWarning("Session {ExamToolsSessionId} disappeared from the ExamTools feed — marked Cancelled", local.ExamToolsSessionId);
        }

        // Candidate sync for every open session still in the feed.
        foreach (var remote in remoteSessions)
        {
            if (!localByExternalId.TryGetValue(remote.Id, out var local)
                || local.Status != SessionStatus.Active
                || local.TestingCompletedUtc is not null)
            {
                continue;
            }

            // applicantCount lets us skip the PII-bearing export call when there is nothing to sync.
            if (remote.ApplicantCount == 0 && local.Candidates.Count == 0)
            {
                continue;
            }

            var applicants = await examToolsClient.GetSessionApplicantsAsync(credentials, remote.Id, cancellationToken);
            SyncCandidates(local, applicants, result);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Session ingestion finished for team {TeamId} ({TeamName}): {Result}", team.Id, team.Name, result);
        return result;
    }

    private static bool ShouldIngestNewSession(ExamToolsSession remote, DateTime now) =>
        (remote.State == PendingState && remote.Date >= now - NewSessionPastGrace)
        || (remote.State == DoneState && remote.Date >= now - CompletedSessionBackfillWindow);

    private async Task<Session?> TryCreateSessionAsync(
        ExamToolsSession remote, Team team, DateTime now, IngestionResult result, CancellationToken cancellationToken)
    {
        var vecCode = remote.Vec.ToLowerInvariant();
        var vec = await dbContext.Vecs.FirstOrDefaultAsync(v => v.Name.ToLower() == vecCode, cancellationToken);
        if (vec is null)
        {
            logger.LogWarning("Skipping new session {ExamToolsSessionId}: no Vec named '{VecCode}' exists yet — add it and the session will ingest on the next poll",
                remote.Id, remote.Vec);
            result.SessionsSkippedNoConfig++;
            return null;
        }

        // Snapshot whichever fee schedule is active right now, per the data model's FeeConfigurationId note.
        var feeConfiguration = await dbContext.FeeConfigurations
            .Where(f => f.VecId == vec.Id && f.EffectiveDate <= now)
            .OrderByDescending(f => f.EffectiveDate)
            .FirstOrDefaultAsync(cancellationToken);
        if (feeConfiguration is null)
        {
            logger.LogWarning("Skipping new session {ExamToolsSessionId}: Vec '{VecName}' has no FeeConfiguration in effect — add one and the session will ingest on the next poll",
                remote.Id, vec.Name);
            result.SessionsSkippedNoConfig++;
            return null;
        }

        var session = new Session
        {
            ExamToolsSessionId = remote.Id,
            Title = string.IsNullOrWhiteSpace(remote.SessionDef?.Summary) ? remote.Id : remote.SessionDef!.Summary,
            ExtId = remote.SessionDef?.ExtId,
            ScheduledStartUtc = remote.Date,
            DurationMinutes = remote.SessionDef?.Duration > 0 ? remote.SessionDef.Duration / 60 : DefaultDurationMinutes,
            VecId = vec.Id,
            TeamId = team.Id,
            FeeConfigurationId = feeConfiguration.Id,
            CreatedUtc = now
        };
        dbContext.Sessions.Add(session);
        result.SessionsAdded++;
        logger.LogInformation("New session {ExamToolsSessionId} scheduled {ScheduledStartUtc:u} ingested", remote.Id, remote.Date);
        return session;
    }

    private void ApplyRescheduleRules(Session local, ExamToolsSession remote, DateTime now, IngestionResult result)
    {
        if (local.Status != SessionStatus.Active || local.TestingCompletedUtc is not null)
        {
            return;
        }

        if (remote.Date == local.ScheduledStartUtc)
        {
            return;
        }

        var hasBlockingCandidates = local.Candidates.Any(c => !c.ApplicationStatus.IsTerminal());
        if (!hasBlockingCandidates)
        {
            logger.LogInformation("Session {ExamToolsSessionId} rescheduled {OldStart:u} -> {NewStart:u} (no candidates — applied automatically)",
                local.ExamToolsSessionId, local.ScheduledStartUtc, remote.Date);
            local.ScheduledStartUtc = remote.Date;
            result.SessionsRescheduled++;
            return;
        }

        // Policy says sessions are only rescheduled with zero candidates, so this needs a human;
        // keep the stored time untouched and flag it. Flag once — the mismatch will show up on
        // every subsequent poll until someone resolves it, and re-flagging would spam AuditLog.
        if (local.RescheduleFlaggedForReview)
        {
            return;
        }

        local.RescheduleFlaggedForReview = true;
        local.RescheduleFlaggedUtc = now;
        dbContext.AuditLogs.Add(new AuditLog
        {
            UserId = null, // system action, not a person
            Action = "RescheduleFlaggedForReview",
            EntityType = nameof(Session),
            EntityId = local.Id,
            TimestampUtc = now,
            Details = $"ExamTools session {local.ExamToolsSessionId} moved from {local.ScheduledStartUtc:u} to {remote.Date:u} while it has registered candidates; stored time left unchanged pending review."
        });
        result.SessionsFlaggedForReview++;
        logger.LogWarning("Session {ExamToolsSessionId} rescheduled {OldStart:u} -> {NewStart:u} but has candidates — flagged for review, stored time unchanged",
            local.ExamToolsSessionId, local.ScheduledStartUtc, remote.Date);
    }

    private void SyncCandidates(Session local, IReadOnlyList<ExamToolsApplicant> applicants, IngestionResult result)
    {
        foreach (var applicant in applicants)
        {
            var existing = local.Candidates.FirstOrDefault(c => c.ExamToolsApplicantId == applicant.Id);
            if (existing is null)
            {
                local.Candidates.Add(new Candidate
                {
                    ExamToolsApplicantId = applicant.Id,
                    Name = applicant.FullName(),
                    FirstName = applicant.Firstname,
                    Email = applicant.Email,
                    Frn = applicant.FrnIsMissing() ? null : applicant.Frn,
                    FrnMissingAtRegistration = applicant.FrnIsMissing(),
                    HasFelonyDisclosure = applicant.HasFelony,
                    DateRegisteredUtc = applicant.Created
                });
                result.CandidatesAdded++;
                logger.LogInformation("New candidate {ExamToolsApplicantId} ingested for session {ExamToolsSessionId}",
                    applicant.Id, local.ExamToolsSessionId);
                continue;
            }

            // Purged rows must stay purged, and terminal candidates are frozen for reporting.
            if (existing.PiiPurgedUtc is not null || existing.ApplicationStatus.IsTerminal())
            {
                continue;
            }

            var changed = false;

            var name = applicant.FullName();
            if (existing.Name != name)
            {
                existing.Name = name;
                changed = true;
            }

            if (existing.FirstName != applicant.Firstname)
            {
                existing.FirstName = applicant.Firstname;
                changed = true;
            }

            if (existing.Email != applicant.Email)
            {
                existing.Email = applicant.Email;
                changed = true;
            }

            // Only ever gain an FRN from the feed — a placeholder upstream must not wipe one that a
            // Session Manager entered manually. FrnMissingAtRegistration stays as the historical flag.
            if (!applicant.FrnIsMissing() && existing.Frn != applicant.Frn)
            {
                existing.Frn = applicant.Frn;
                changed = true;
            }

            if (applicant.HasFelony is not null && existing.HasFelonyDisclosure != applicant.HasFelony)
            {
                existing.HasFelonyDisclosure = applicant.HasFelony;
                changed = true;
            }

            if (changed)
            {
                result.CandidatesUpdated++;
                logger.LogInformation("Candidate {ExamToolsApplicantId} updated from feed for session {ExamToolsSessionId}",
                    applicant.Id, local.ExamToolsSessionId);
            }
        }
        // Applicants that disappear upstream are intentionally left alone: withdrawal/no-show is a
        // manual Session Manager action (Phase 9's delete flow), not something the poller infers.
    }
}
