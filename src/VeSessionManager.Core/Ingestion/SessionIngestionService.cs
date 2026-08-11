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
    /// start tracking past candidates/VE stats for sessions that already happened.
    ///
    /// Narrowed from 30 days to 7 (issue #67): once a session is completed in ExamTools there is
    /// nothing further to pull *about the session*, so this window is now purely a **discovery net**
    /// — it exists to catch a session that completed while the Worker was down, not to keep
    /// re-reading a month of history on every tick, for every team, forever. Deliberately pulling
    /// real history (a full year for the stats page) is a one-off operation now, not a continuous
    /// one: see HistoricalImportService and docs/historical-import.md.
    ///
    /// The feed still returns unfiltered full history, so the bound also does what NewSessionPastGrace
    /// does — a "done" session from years ago is exactly as undesirable to backfill accidentally as a
    /// zombie "pend" one. SessionEventSchedulingService/CandidateNotificationService separately guard
    /// against live-scheduling or emailing a session ingested this way once it's already over
    /// (Session.HasEnded) — this window only controls whether the row gets created at all.
    /// </summary>
    private static readonly TimeSpan CompletedSessionBackfillWindow = TimeSpan.FromDays(7);

    /// <summary>
    /// Issue #67 part 2: imports completed sessions over an explicit date range, for the one-off
    /// historical import (see HistoricalImportService and docs/historical-import.md). Bypasses
    /// CompletedSessionBackfillWindow — that window governs the *continuous* sweep, and the whole
    /// point here is a deliberate operator-chosen range that the sweep should never cover.
    ///
    /// **This deliberately does not reuse RunAsync, and must not be "simplified" into it.** RunAsync
    /// treats a known, still-open session's absence from the feed as a cancellation. This feed is
    /// filtered to one date range, so every session outside that range is absent by construction —
    /// running the cancellation pass here would mark a team's entire live schedule Cancelled. For
    /// the same reason there is no reschedule handling and no ExtId backfill: this method only ever
    /// *creates* sessions that are missing, and touches nothing that already exists.
    ///
    /// Candidates are synced only for sessions this call actually creates. An already-imported
    /// session is skipped whole, which makes re-running a range cheap and — more importantly — keeps
    /// WithdrawMissingCandidates away from historical rosters, where a short or empty export from a
    /// long-finished session would clear real candidates' PII irreversibly.
    /// </summary>
    public async Task<IngestionResult> ImportHistoricalRangeAsync(
        Team team, DateOnly startDate, DateOnly endDate, int requestedByUserId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var result = new IngestionResult();

        if (!team.IsExamToolsConfigured)
        {
            logger.LogInformation("Team {TeamId} ({TeamName}) has no ExamTools credentials configured — skipping historical import", team.Id, team.Name);
            return result;
        }

        var credentials = ExamToolsCredentials.For(team, examToolsOptions.Value.BaseUrl);
        var remoteSessions = await examToolsClient.GetTeamClosedSessionsAsync(credentials, startDate, endDate, cancellationToken);

        var existingIds = await dbContext.Sessions
            .Where(s => s.TeamId == team.Id)
            .Select(s => s.ExamToolsSessionId)
            .ToListAsync(cancellationToken);
        var existing = existingIds.ToHashSet();

        foreach (var remote in remoteSessions)
        {
            if (existing.Contains(remote.Id))
            {
                // Already stored, so there is nothing to create — but still reconcile the VEC
                // submission flag. That is deliberately NOT inside the create branch: a range
                // imported before this behaviour existed would otherwise stay NotSubmitted forever,
                // since re-running an import skips every session it already has. Re-running the
                // range is the supported way to fix such a backlog.
                await MarkHistoricalSessionSubmittedAsync(remote.Id, team, requestedByUserId, now, result, cancellationToken);
                continue;
            }

            var created = await TryCreateSessionAsync(remote, team, now, result, cancellationToken);
            if (created is null)
            {
                continue;
            }

            // Stamped immediately: this session is already closed upstream (it came from the closed
            // feed), and without the stamp the routine sweep's cancellation heuristic has only
            // HasEnded to fall back on. Costs nothing here and keeps the imported rows in the same
            // shape the continuous path eventually produces.
            created.ExamToolsClosedUtc = now;
            existing.Add(remote.Id);

            // A historical session's VEC paperwork was filed outside this app, months ago — leaving
            // it NotSubmitted would drop the whole imported range into the submission tracker as if
            // it were outstanding work, one manual click each to clear.
            MarkVecSubmitted(created, requestedByUserId, now, result);

            // Isolated per session, same reasoning as the routine candidate loop: one session
            // ExamTools can't serve must not abort an import of a hundred others.
            try
            {
                var applicants = await examToolsClient.GetSessionApplicantsAsync(credentials, remote.Id, cancellationToken);
                SyncCandidates(created, applicants, remote.ApplicantCount, now, result);
                MarkHistoricalCandidatesGranted(created, result);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result.SessionsFailedCandidateSync++;
                logger.LogError(ex, "Historical import: candidate sync failed for session {ExamToolsSessionId} (team {TeamId}) — the session row is kept, the rest of the import continues",
                    remote.Id, team.Id);
            }

            await dbContext.SaveChangesAsync(cancellationToken);
        }

        logger.LogInformation("Historical import {StartDate}..{EndDate} finished for team {TeamId} ({TeamName}): {Result}",
            startDate, endDate, team.Id, team.Name, result);
        return result;
    }

    /// <summary>
    /// Historical candidates are assumed to have been granted, per the session-lifecycle rule: there
    /// is no reason to keep asking FCC whether a license from one to four years ago was issued. Left
    /// non-terminal they would be polled by UlsWatcherService — one HTTP call per candidate, twice a
    /// day, forever — and counted as outstanding on the Applicant Status screen.
    ///
    /// Deliberately does NOT invent a CallSign or LicenseGrantDateUtc: those stay null because they
    /// were never verified. Only the status is asserted. Where UlsWatcherService *did* manage to
    /// match a real license during an earlier run, that candidate is already terminal and is left
    /// exactly as it is, call sign and grant date intact.
    ///
    /// No per-candidate audit entry, on purpose: an import writes thousands of these at once and the
    /// audit log is a fixed 200-row window with no filtering (issue #86). The aggregate count is
    /// reported in the import's own result and log line instead.
    /// </summary>
    private static void MarkHistoricalCandidatesGranted(Session session, IngestionResult result)
    {
        foreach (var candidate in session.Candidates.Where(c => !c.ApplicationStatus.IsTerminal()))
        {
            candidate.ApplicationStatus = CandidateApplicationStatus.Granted;
            result.CandidatesAssumedGranted++;
        }
    }

    /// <summary>
    /// Loads an already-stored session by its ExamTools id and marks it submitted-to-VEC. Separate
    /// from the create path only because that one already has the entity in hand.
    /// </summary>
    private async Task MarkHistoricalSessionSubmittedAsync(
        string examToolsSessionId, Team team, int requestedByUserId, DateTime now, IngestionResult result, CancellationToken cancellationToken)
    {
        var session = await dbContext.Sessions
            .FirstOrDefaultAsync(s => s.TeamId == team.Id && s.ExamToolsSessionId == examToolsSessionId, cancellationToken);
        if (session is null || !MarkVecSubmitted(session, requestedByUserId, now, result))
        {
            return;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Marks one session submitted-to-VEC, mirroring VecSubmissionService.MarkSubmittedAsync's rule
    /// that an already-Submitted session is left completely alone — re-running an import must never
    /// reassign credit for a submission a real Session Manager recorded, or overwrite its date.
    /// Returns whether anything changed. Does not save; the caller batches that.
    /// </summary>
    private bool MarkVecSubmitted(Session session, int requestedByUserId, DateTime now, IngestionResult result)
    {
        if (session.VecSubmissionStatus == VecSubmissionStatus.Submitted)
        {
            return false;
        }

        session.VecSubmissionStatus = VecSubmissionStatus.Submitted;
        session.VecSubmittedDate = now;
        session.VecSubmittedByUserId = requestedByUserId;

        // Audited like the manual action, but with wording that makes the provenance obvious — a
        // reader must be able to tell "the import assumed this" from "a person confirmed this".
        dbContext.AddAuditLog(requestedByUserId, "VecSubmissionMarked", nameof(Session), session.Id,
            $"Session {session.ExamToolsSessionId} auto-marked as submitted to the VEC by historical import (predates tracking in this app).", now);

        result.SessionsMarkedVecSubmitted++;
        return true;
    }

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

        // Loaded before the closed-session merge below, which needs to know what we already have.
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

        // Issue #67: a closed session we have already stored *and already observed closing* has
        // nothing left to give this feed. The only two things the loop below would still do to it
        // are ApplyRescheduleRules (meaningless for a session that already happened) and the
        // one-time ExtId backfill from 2026-07-30 (long since complete), so it is dropped from the
        // merge entirely rather than re-processed on every tick, for every team, forever.
        //
        // The ExamToolsClosedUtc test is what makes this safe, and "already known locally" alone
        // would NOT be: a session that is locally known but has never been seen closed still needs
        // this feed for two things that issue #68's fix depends on — the ExamToolsClosedUtc stamp
        // itself (only "done" sessions carry it, and only this feed returns them), and the final
        // candidate sync on the run that discovers the close. Dropping those would resurrect the
        // false-cancellation bug. Already-settled sessions are excluded from remoteIds too, which is
        // harmless: cancellation detection already ignores anything with a closed stamp.
        bool IsSettledLocally(ExamToolsSession remote) =>
            localByExternalId.TryGetValue(remote.Id, out var local) && local.ExamToolsClosedUtc is not null;

        // GetTeamSessionsAsync never returns a closed ("done") session, confirmed live 2026-07-28 —
        // closed sessions only exist behind this separate date-range feed. Merge the two, preferring
        // the pend feed's own copy of a session id if (implausibly) both returned it.
        var closedSessions = await examToolsClient.GetTeamClosedSessionsAsync(
            credentials, DateOnly.FromDateTime(now - CompletedSessionBackfillWindow), DateOnly.FromDateTime(now.AddDays(1)), cancellationToken);
        var pendIds = remoteSessions.Select(r => r.Id).ToHashSet();
        remoteSessions.AddRange(closedSessions.Where(c => !pendIds.Contains(c.Id) && !IsSettledLocally(c)));

        var remoteIds = remoteSessions.Select(r => r.Id).ToHashSet();

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

                // Assigned, not ??=. ExtId above backfills once and then stops, because a changed
                // identifier upstream is not worth chasing. The lead is different: it names a person
                // who may be emailed about this session, so a stale value means notifying the wrong
                // VE. Reassigning also backfills existing rows on the next poll, so no migration
                // script is needed for sessions ingested before this field existed.
                //
                // Only overwritten when ExamTools actually reports one — a feed that omits it must
                // not silently erase a lead we already knew.
                if (CallSign.NormalizeFormat(remote.SessionDef?.TeamLeadCallsign) is { } leadCallSign)
                {
                    local.TeamLeadCallSign = leadCallSign;
                }
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
    /// Session-scoped variant of RunAsync's candidate sync, for the session Detail page's "Refresh
    /// candidates" button (via ManualCandidateRefreshService.RunForSessionAsync) — re-fetches ONE
    /// session's applicant export instead of running the whole team feed's worth of candidate syncs.
    ///
    /// Deliberately does NOT create sessions or run cancellation detection: both require diffing the
    /// complete team feed (a session id disappearing from the feed IS the cancellation signal —
    /// issue #68), which is exactly the team-wide work this exists to avoid. The team feed is still
    /// fetched, read-only, for this session's closed-stamp/reschedule/ExtId handling and for the
    /// applicantCount that gates withdrawal detection; when the session is in neither feed a null
    /// count is passed, which makes SyncCandidates skip withdrawal detection rather than misread
    /// absence as "everyone withdrew".
    ///
    /// **Both feeds, not just the pend one.** GetTeamSessionsAsync never returns a closed ("done")
    /// session — that is the whole reason RunAsync merges GetTeamClosedSessionsAsync — so reading
    /// only the pend feed made the close-stamp branch below unreachable and left this button unable
    /// to ever close a session (fixed 2026-08-03, reported live the same day). The closed feed is
    /// queried only when the session is absent from the pend feed, so the common "still open" case
    /// still costs one call. Its date range is anchored on this session's own scheduled date rather
    /// than RunAsync's rolling CompletedSessionBackfillWindow: the range is per-session here, so it
    /// can be exact, and that also lets a Session Manager pull the close stamp for a session far
    /// older than the rolling window.
    /// </summary>
    public async Task<IngestionResult> RefreshSessionCandidatesAsync(Team team, int sessionId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var result = new IngestionResult();

        if (!team.IsExamToolsConfigured)
        {
            logger.LogInformation("Team {TeamId} ({TeamName}) has no ExamTools credentials configured yet — skipping session refresh", team.Id, team.Name);
            return result;
        }

        // Payments eager-loaded for the same reason as RunAsync: withdrawal detection clears a
        // candidate's live Square checkout link via CandidatePiiFields.Clear.
        var local = await dbContext.Sessions
            .Include(s => s.Candidates).ThenInclude(c => c.Payments)
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.TeamId == team.Id, cancellationToken);
        if (local is null)
        {
            return result;
        }

        var credentials = ExamToolsCredentials.For(team, examToolsOptions.Value.BaseUrl);

        // Same last-chance rule as RunAsync: sync while open, including the refresh that discovers
        // the close — but a session already stamped closed before this refresh began has nothing
        // left to give (candidate-level updates for a finished session arrive through
        // ExamResultSyncService and the ULS watcher, not this feed).
        var closedBeforeThisRefresh = local.ExamToolsClosedUtc is not null;

        var remote = (await examToolsClient.GetTeamSessionsAsync(credentials, cancellationToken))
            .FirstOrDefault(r => r.Id == local.ExamToolsSessionId);

        if (remote is null)
        {
            // Absent from the pend feed means either "closed" or "no longer carried at all". The
            // closed feed is the only place a done session exists, and it is a date-range query —
            // bounded to this session's own date ±1 day, since ExamTools' `date` and our stored UTC
            // start can land either side of a day boundary.
            var sessionDate = DateOnly.FromDateTime(local.ScheduledStartUtc);
            remote = (await examToolsClient.GetTeamClosedSessionsAsync(
                    credentials, sessionDate.AddDays(-1), sessionDate.AddDays(1), cancellationToken))
                .FirstOrDefault(r => r.Id == local.ExamToolsSessionId);
        }

        if (remote is not null)
        {
            if (remote.State == DoneState && local.ExamToolsClosedUtc is null)
            {
                local.ExamToolsClosedUtc = now;
                result.SessionsClosedByExamTools++;
                logger.LogInformation("Session {ExamToolsSessionId} reported closed by ExamTools — no further session-level polling", local.ExamToolsSessionId);
            }

            ApplyRescheduleRules(local, remote, now, result);
            local.ExtId ??= remote.SessionDef?.ExtId;
        }

        if (local.Status == SessionStatus.Active && local.TestingCompletedUtc is null && !closedBeforeThisRefresh)
        {
            // No per-session try/catch here, unlike RunAsync's loop: there is only this one session,
            // and the caller (JobRunHistoryLogger) records the failure — nothing else to protect.
            var applicants = await examToolsClient.GetSessionApplicantsAsync(credentials, local.ExamToolsSessionId, cancellationToken);
            SyncCandidates(local, applicants, remote?.ApplicantCount, now, result);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Session-scoped refresh finished for session {SessionId} ({ExamToolsSessionId}), team {TeamId}: {Result}",
            local.Id, local.ExamToolsSessionId, team.Id, result);
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
        // Match on Vec.ExamToolsCode, falling back to Name when it's null — ExamTools' code is not
        // always the org's name (GLAARG reports "lagroup"), and matching Name alone silently skipped
        // every session of any such VEC forever. Coalesce is spelled out here rather than using
        // Vec.MatchCode so EF Core can translate it to SQL.
        var vecCode = remote.Vec.ToLowerInvariant();
        var vec = await dbContext.Vecs.FirstOrDefaultAsync(
            v => (v.ExamToolsCode ?? v.Name).ToLower() == vecCode, cancellationToken);
        if (vec is null)
        {
            logger.LogWarning("Skipping new session {ExamToolsSessionId}: no Vec matches ExamTools code '{VecCode}' — add a VEC with that ExamTools code (Admin → VECs) and the session will ingest on the next poll",
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
            TeamLeadCallSign = CallSign.NormalizeFormat(remote.SessionDef?.TeamLeadCallsign),
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
        // userId null = system action, not a person.
        dbContext.AddAuditLog(null, "RescheduleFlaggedForReview", nameof(Session), local.Id,
            $"ExamTools session {local.ExamToolsSessionId} moved from {local.ScheduledStartUtc:u} to {remote.Date:u} while it has registered candidates; stored time left unchanged pending review.",
            now);
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
