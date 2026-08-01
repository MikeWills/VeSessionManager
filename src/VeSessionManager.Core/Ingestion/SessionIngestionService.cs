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

    /// <summary>
    /// ExamTools' "In progress" state — the session is running right now (confirmed against the
    /// dev site's own session list, 2026-07-31, where this renders as a blue "In progress" row).
    /// First-ingestable exactly like "pend": a session can gain a new candidate while it is
    /// underway, and before this was recognised a session first seen in this state fell through
    /// ShouldIngestNewSession's final "unknown state" case and became invisible — along with every
    /// candidate on it — until it eventually closed.
    /// </summary>
    private const string InProgressState = "go";

    /// <summary>Every session state this app knows how to act on. Anything else is logged once per run rather than silently dropped — see LogUnknownSessionStates.</summary>
    private static readonly string[] KnownStates = [PendingState, InProgressState, DoneState];

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
        // Payments are eager-loaded because CandidatePiiFields.Clear (used by the auto-withdrawal
        // below) nulls a candidate's live Square checkout link too — with Payments unloaded it would
        // silently clear only the Candidate half and leave a payable link alive.
        var localSessions = await dbContext.Sessions
            .Include(s => s.Candidates).ThenInclude(c => c.Payments)
            .Where(s => s.TeamId == team.Id)
            .ToListAsync(cancellationToken);
        var localByExternalId = localSessions.ToDictionary(s => s.ExamToolsSessionId);

        // Captured *before* this run stamps any new closes. "Poll while the session is open"
        // has to include the poll that discovers it closed — that run is the last chance to pick up
        // final candidate changes. Testing against ExamToolsClosedUtc directly in the candidate loop
        // below would skip that final sync, because the stamp is applied earlier in this same run.
        var closedBeforeThisRun = localSessions
            .Where(s => s.ExamToolsClosedUtc is not null)
            .Select(s => s.ExamToolsSessionId)
            .ToHashSet();

        foreach (var remote in remoteSessions)
        {
            if (localByExternalId.TryGetValue(remote.Id, out var local))
            {
                // ExamTools reporting the session closed is recorded the first time we see it, and
                // never re-stamped. This is what stops issue #68's false cancellations: without it,
                // nothing ever moved a session out of "open", so every real completed session
                // eventually looked like a disappearance and got flipped to Cancelled. It is NOT
                // TestingCompletedUtc — see Session.ExamToolsClosedUtc's own remarks for why that
                // distinction is load-bearing.
                if (remote.State == DoneState && local.ExamToolsClosedUtc is null)
                {
                    local.ExamToolsClosedUtc = now;
                    result.SessionsClosedByExamTools++;
                    logger.LogInformation("Session {ExamToolsSessionId} reported closed by ExamTools — no further session-level polling", local.ExamToolsSessionId);
                }

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

        LogUnknownSessionStates(team, remoteSessions);

        // Cancellation: ExamTools has no cancelled flag; a known, still-open session vanishing
        // from the feed by id *is* the cancellation signal (confirmed against real API responses).
        // Two guards, both added for issue #68, and deliberately not one:
        //
        //   ExamToolsClosedUtc — ExamTools has told us this session is closed, so its later
        //   disappearance from the feed is expected, not a cancellation.
        //
        //   !HasEnded(now) — a session whose scheduled window has already elapsed cannot
        //   meaningfully be "cancelled" by vanishing; at that point disappearing just means it aged
        //   past CompletedSessionBackfillWindow. This one is the backstop that does not depend on
        //   having *observed* the close: sessions that aged out before ExamToolsClosedUtc existed
        //   (and any session ExamTools drops without ever reporting "done") have no closed-stamp to
        //   rely on, and were being flipped to Cancelled 30 days after they really ran.
        //
        // Cancellation therefore now only applies to a session that is still in its future — which
        // is the only case the heuristic was ever meant to catch.
        foreach (var local in localSessions.Where(s =>
                     s.Id != 0 && // freshly added this run — always still in the feed
                     s.Status == SessionStatus.Active &&
                     s.TestingCompletedUtc is null &&
                     s.ExamToolsClosedUtc is null &&
                     !s.HasEnded(now) &&
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
            // Poll while the session is open, including the run that discovers it closed — that
            // last sync is what captures any final candidate changes. From the *next* run on there
            // is nothing further to pull: candidate-level updates for a finished session arrive
            // through ExamResultSyncService (per applicant id) and the ULS watcher (per FRN),
            // neither of which uses this feed.
            if (!localByExternalId.TryGetValue(remote.Id, out var local)
                || local.Status != SessionStatus.Active
                || local.TestingCompletedUtc is not null
                || closedBeforeThisRun.Contains(remote.Id))
            {
                continue;
            }

            // applicantCount lets us skip the PII-bearing export call when there is nothing to sync.
            if (remote.ApplicantCount == 0 && local.Candidates.Count == 0)
            {
                continue;
            }

            // Isolated per session. One session ExamTools can't serve must not take the whole team's
            // ingestion down with it — found live 2026-07-31, when a single 404 stopped HRCC's
            // candidates, VE roster, payments and emails for two hours, every tick, with the only
            // symptom a failed JobRunHistory row. Every other session in the feed still syncs, and
            // the failure is counted so the run summary shows it rather than reading as a clean pass.
            try
            {
                var applicants = await examToolsClient.GetSessionApplicantsAsync(credentials, remote.Id, cancellationToken);
                SyncCandidates(local, applicants, remote.ApplicantCount, now, result);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result.SessionsFailedCandidateSync++;
                logger.LogError(ex, "Candidate sync failed for session {ExamToolsSessionId} (team {TeamId}) — skipping this session, the rest of the team's ingestion continues",
                    remote.Id, team.Id);
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Session ingestion finished for team {TeamId} ({TeamName}): {Result}", team.Id, team.Name, result);
        return result;
    }

    /// <summary>
    /// A state this app doesn't recognise means ExamTools has a lifecycle step we don't model, and
    /// every session in it is silently invisible. That is exactly how "go" (In progress) went
    /// unnoticed until 2026-07-31 — it was dropped by a bare `else` with no log line at all. One
    /// aggregate line per run, not per session, so an unmodelled state announces itself the first
    /// time it appears instead of being found by reading raw API output.
    /// </summary>
    private void LogUnknownSessionStates(Team team, List<ExamToolsSession> remoteSessions)
    {
        var unknown = remoteSessions
            .Where(s => !KnownStates.Contains(s.State))
            .GroupBy(s => s.State ?? "(null)")
            .Select(g => $"{g.Key} x{g.Count()}")
            .ToList();
        if (unknown.Count == 0)
        {
            return;
        }

        logger.LogWarning("ExamTools returned session state(s) this app does not recognise for team {TeamId} ({TeamName}): {UnknownStates} — those sessions were not ingested. Known states are {KnownStates}.",
            team.Id, team.Name, string.Join(", ", unknown), string.Join("/", KnownStates));
    }

    /// <summary>
    /// "go" (In progress) is treated exactly like "pend": a session can pick up a new candidate
    /// while it is underway, so one first seen mid-session must still be ingestable. The same
    /// NewSessionPastGrace bound applies — the dev feed carries a session stuck "In progress" since
    /// 2024, which is as undesirable to ingest as any other zombie.
    /// </summary>
    private static bool ShouldIngestNewSession(ExamToolsSession remote, DateTime now) =>
        ((remote.State == PendingState || remote.State == InProgressState) && remote.Date >= now - NewSessionPastGrace)
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

    private void SyncCandidates(Session local, IReadOnlyList<ExamToolsApplicant> applicants, int? remoteApplicantCount, DateTime now, IngestionResult result)
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
        WithdrawMissingCandidates(local, applicants, remoteApplicantCount, now, result);
    }

    /// <summary>
    /// Issue #70: a candidate who cancels in ExamTools simply stops appearing in that session's
    /// applicant export. Previously this was ignored entirely — withdrawal was a manual Session
    /// Manager action — so a cancelled candidate stayed on the roster indefinitely.
    ///
    /// Lands the candidate in exactly the state CandidateActionService.DeleteAsync produces
    /// (ApplicationStatus = NotTested, PII cleared immediately, row kept for stats) so the UI, the
    /// PII purge and reporting can't tell the two routes apart. The audit entry passes a null user
    /// id, which is how a system action is recorded everywhere else.
    ///
    /// **Payments are deliberately untouched** — a withdrawn candidate may legitimately have paid,
    /// and what happens to that money is a human decision (FlagRefundRequestedAsync exists for it).
    /// The one exception is the live Square checkout *link*, which CandidatePiiFields.Clear nulls
    /// along with the PII; the Payment row, its amount and its status all survive.
    ///
    /// Absence is a dangerous signal to act on — inferring "gone from the feed means it's over" at
    /// *session* level is what caused issue #68, and here the consequence is worse because clearing
    /// PII cannot be undone. Hence the count cross-check below: two independent fields have to agree
    /// that this really is the session's full roster before anything is withdrawn.
    /// </summary>
    private void WithdrawMissingCandidates(Session local, IReadOnlyList<ExamToolsApplicant> applicants, int? remoteApplicantCount, DateTime now, IngestionResult result)
    {
        // The session feed's own applicantCount and the applicant export are separate fields from
        // separate endpoints. Requiring them to agree means a truncated, partial or empty-but-
        // successful export can't be mistaken for "everyone withdrew" — the single most plausible
        // way this feature could silently wipe a live roster.
        if (remoteApplicantCount is null || remoteApplicantCount != applicants.Count)
        {
            logger.LogWarning(
                "Skipping withdrawal detection for session {ExamToolsSessionId}: ExamTools reported {ReportedCount} applicant(s) but the export returned {ReturnedCount} — not treating the difference as withdrawals",
                local.ExamToolsSessionId, remoteApplicantCount, applicants.Count);
            return;
        }

        var remoteApplicantIds = applicants.Select(a => a.Id).ToHashSet();
        var missing = local.Candidates
            .Where(c => c.ExamToolsApplicantId is not null            // never came from the feed
                        && !remoteApplicantIds.Contains(c.ExamToolsApplicantId)
                        && !c.Tested                                  // same refusal DeleteAsync makes
                        && !c.ApplicationStatus.IsTerminal()          // already settled (incl. a previous withdrawal)
                        && c.PiiPurgedUtc is null)
            .ToList();

        foreach (var candidate in missing)
        {
            candidate.ApplicationStatus = CandidateApplicationStatus.NotTested;
            CandidatePiiFields.Clear(candidate, now);
            candidate.ResultMarkedUtc = now;
            // ResultMarkedByUserId deliberately left null — no human made this call.

            dbContext.AddAuditLog(null, "CandidateWithdrawnFromFeed", nameof(Candidate), candidate.Id,
                $"Candidate {candidate.Id} no longer in ExamTools' applicant list for session {local.ExamToolsSessionId} — marked NotTested (withdrew) and PII cleared. Payments left for manual handling.",
                now);
            result.CandidatesWithdrawn++;
            logger.LogInformation("Candidate {CandidateId} withdrew from session {ExamToolsSessionId} (gone from ExamTools' applicant list) — marked NotTested, PII cleared, {PaymentCount} payment(s) left untouched",
                candidate.Id, local.ExamToolsSessionId, candidate.Payments.Count);
        }
    }
}
