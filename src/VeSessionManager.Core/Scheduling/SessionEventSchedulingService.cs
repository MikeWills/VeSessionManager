using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Discord;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Integrations;
using VeSessionManager.Core.Zoom;

namespace VeSessionManager.Core.Scheduling;

/// <summary>
/// Phase 2: creates/updates/tears down the Zoom meeting and Discord scheduled event for each
/// Session, driven entirely by comparing stored state — no event queue needed:
///   - Status Active and ScheduledStartUtc != ZoomDiscordSyncedStartUtc -> needs create-or-update.
///     A null ZoomMeetingId/DiscordEventId on that session means "create"; a non-null one means
///     "update". ZoomDiscordSyncedStartUtc only advances once *everything currently able to run
///     has run successfully* — see the "settled" logic in SyncZoomAndDiscordAsync.
///   - Status Cancelled and either id is still set -> needs cleanup (delete + null the id).
///
/// Both Zoom and Discord are optional integrations (unlike ExamTools, which fails loudly when
/// unconfigured, since ingestion is the one thing everything else depends on): a team that
/// hasn't finished setting one up yet must not see a repeated failed-call error every poll.
/// Zoom is per-team (Team.IsZoomConfigured, each team has its own separate Zoom S2S OAuth app);
/// Discord shares one bot across every team (IDiscordEventClient.IsConfigured) but still needs a
/// per-team Guild selected (Team.IsDiscordConfigured) — see docs/multi-team.md. Either integration
/// backfills automatically the moment it becomes configured — no other config change or manual
/// retrigger needed, matching Phase 3/4's optional integrations.
///
/// Discord's event needs the Zoom join link for its description/location, so Discord can only
/// actually run once Zoom has produced one — if Zoom isn't configured (or hasn't succeeded yet),
/// Discord stays pending too, even if Discord itself is fully configured; the moment Zoom
/// produces a link, Discord picks up automatically on the same or a later poll.
///
/// This means a poll that crashes or fails partway always resumes correctly next run purely by
/// re-reading Session state — matching Phase 1's polling philosophy. Multi-team: this service now
/// operates on one Team's sessions per call — see docs/multi-team.md.
///
/// SyncZoomAndDiscordAsync calls SaveChangesAsync itself immediately after a newly-created
/// ZoomMeetingId/DiscordEventId is set — the id is the only handle back to a resource that already
/// exists externally (needed for every future update/delete), so the window where it's known only
/// in memory is kept as small as possible rather than deferred to RunAsync's own end-of-session
/// save. FindExistingMeetingAsync/FindExistingEventAsync are the backstop for what's left of that
/// window (and for the case where the previous poll never even reached the save).
/// Per spec: cancellation cleanup never sends any candidate-facing notification — that is a
/// manual Session Manager action, not something this job (or any later phase) should do.
/// </summary>
public class SessionEventSchedulingService(
    AppDbContext dbContext,
    IZoomClient zoomClient,
    IDiscordEventClient discordEventClient,
    TimeProvider timeProvider,
    TeamIntegrationState integrationState,
    ILogger<SessionEventSchedulingService> logger)
{
    /// <param name="onlySessionId">Restrict the run to one session (the Detail page's
    /// session-scoped refresh); null (every scheduled/team-wide run) scans the whole team.</param>
    public async Task<SchedulingResult> RunAsync(Team team, CancellationToken cancellationToken, int? onlySessionId = null)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var result = new SchedulingResult();

        // Query-side coarse bound (2026-08-01): a past session can never satisfy the
        // ScheduledStartUtc == ZoomDiscordSyncedStartUtc equality, because it is deliberately never
        // synced — so without this, every session the historical import backfilled was loaded,
        // filtered out and log-counted on every tick, forever, with the count only growing (794 for
        // one team). A session starting more than a day ago has certainly ended (durations are
        // hours), so the precise HasEnded check below still sees everything it needs.
        var recentSessionCutoff = now.AddDays(-1);

        var candidateSessions = await dbContext.Sessions
            .Where(s => s.TeamId == team.Id
                        && s.Status == SessionStatus.Active
                        && s.ScheduledStartUtc >= recentSessionCutoff
                        && s.ScheduledStartUtc != s.ZoomDiscordSyncedStartUtc
                        // Exact rather than relying on the coarse bound above alone (#88) — see
                        // CandidateRegisteredScanner's identical comment for why the 1-day cutoff
                        // alone isn't quite enough.
                        && s.ImportedHistoricallyUtc == null
                        && (onlySessionId == null || s.Id == onlySessionId))
            .ToListAsync(cancellationToken);

        // A session ingested via the completed-session backfill window (see SessionIngestionService)
        // has already ended by the time this ever runs — never worth a real Zoom meeting/Discord
        // event. Filtered out here, not query-side, since HasEnded's arithmetic is plain C#, not
        // something worth relying on the SQLite provider to translate.
        var sessionsNeedingSync = candidateSessions.Where(s => !s.HasEnded(now)).ToList();

        var pastDueCount = candidateSessions.Count - sessionsNeedingSync.Count;
        if (pastDueCount > 0)
        {
            result.SessionsSkippedPastDue = pastDueCount;
            logger.LogInformation("Skipped Zoom/Discord scheduling for {Count} already-past session(s) in team {TeamId} — likely backfilled via the completed-session ingestion window",
                pastDueCount, team.Id);
        }

        LogUnconfiguredIntegrations(team, sessionsNeedingSync);

        foreach (var session in sessionsNeedingSync)
        {
            try
            {
                await SyncZoomAndDiscordAsync(team, session, cancellationToken);
                if (session.ZoomDiscordSyncedStartUtc == session.ScheduledStartUtc)
                {
                    result.SessionsSynced++;
                }
                else
                {
                    // Everything that could run this pass did; what's left is waiting on Zoom
                    // and/or Discord to be configured — not a failure, see SchedulingResult remarks.
                    result.SessionsAwaitingIntegrationConfig++;
                }
            }
            catch (Exception ex)
            {
                result.SessionsFailed++;
                logger.LogError(ex, "Failed to sync Zoom/Discord for session {ExamToolsSessionId}", session.ExamToolsSessionId);
            }

            // Save after every session, success or partial failure, so a crash mid-run never loses
            // progress already made (e.g. Zoom succeeded, Discord didn't).
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        var sessionsNeedingCleanup = await dbContext.Sessions
            .Where(s => s.TeamId == team.Id && s.Status == SessionStatus.Cancelled && (s.ZoomMeetingId != null || s.DiscordEventId != null)
                        && (onlySessionId == null || s.Id == onlySessionId))
            .ToListAsync(cancellationToken);

        LogUnconfiguredCleanups(team, sessionsNeedingCleanup);

        foreach (var session in sessionsNeedingCleanup)
        {
            try
            {
                var fullyCleanedUp = await CleanupZoomAndDiscordAsync(team, session, now, cancellationToken);
                if (fullyCleanedUp)
                {
                    result.SessionsCleanedUp++;
                }
                else
                {
                    // Same reasoning as sessionsNeedingSync's SessionsAwaitingIntegrationConfig
                    // branch above: whatever could be cleaned up this pass was, the rest is just
                    // waiting on the team's Zoom/Discord config — not a failure worth an [ERR].
                    result.SessionsAwaitingIntegrationConfig++;
                }
            }
            catch (Exception ex)
            {
                result.SessionsFailed++;
                logger.LogError(ex, "Failed to clean up Zoom/Discord for cancelled session {ExamToolsSessionId}", session.ExamToolsSessionId);
            }

            await dbContext.SaveChangesAsync(cancellationToken);
        }

        logger.LogInformation("Session event scheduling finished for team {TeamId} ({TeamName}): {Result}", team.Id, team.Name, result);
        return result;
    }

    private void LogUnconfiguredIntegrations(Team team, List<Session> sessionsNeedingSync)
    {
        // Discord needs both gates true: the shared bot itself ready, and this team having picked
        // a Guild — see Team.IsDiscordConfigured's remarks.
        var discordReady = discordEventClient.IsConfigured && team.IsDiscordConfigured;
        if (team.IsZoomConfigured && discordReady)
        {
            return;
        }

        var pendingCount = sessionsNeedingSync.Count(s =>
            (!team.IsZoomConfigured && s.ZoomMeetingId is null) ||
            (!discordReady && s.DiscordEventId is null));
        if (pendingCount == 0)
        {
            return;
        }

        var unconfigured = string.Join(" and ",
            new[] { team.IsZoomConfigured ? null : "Zoom", discordReady ? null : "Discord" }
                .Where(name => name is not null));
        logger.LogInformation("{Unconfigured} not fully configured for team {TeamId} — {PendingCount} session(s) waiting; will create automatically once configured",
            unconfigured, team.Id, pendingCount);
    }

    /// <summary>
    /// Mirrors LogUnconfiguredIntegrations for the cleanup side (found live 2026-07-29 — see
    /// docs/zoom-discord-scheduling.md): a cancelled
    /// session's stale Zoom meeting/Discord event still needs tearing down eventually even if the
    /// team's config for that integration was removed (or never finished) since it was created, but
    /// that must not mean a repeating [ERR] every single poll in the meantime — one aggregate INFO
    /// line per run, same as the sync-side pattern above.
    /// </summary>
    private void LogUnconfiguredCleanups(Team team, List<Session> sessionsNeedingCleanup)
    {
        var discordReady = discordEventClient.IsConfigured && team.IsDiscordConfigured;
        if (team.IsZoomConfigured && discordReady)
        {
            return;
        }

        var pendingCount = sessionsNeedingCleanup.Count(s =>
            (!team.IsZoomConfigured && s.ZoomMeetingId is not null) ||
            (!discordReady && s.DiscordEventId is not null));
        if (pendingCount == 0)
        {
            return;
        }

        var unconfigured = string.Join(" and ",
            new[] { team.IsZoomConfigured ? null : "Zoom", discordReady ? null : "Discord" }
                .Where(name => name is not null));
        logger.LogInformation("{Unconfigured} not fully configured for team {TeamId} — {PendingCount} cancelled session(s) still have a stale meeting/event pending cleanup; will clean up automatically once configured",
            unconfigured, team.Id, pendingCount);
    }

    private async Task SyncZoomAndDiscordAsync(Team team, Session session, CancellationToken cancellationToken)
    {
        // Mute switches first, and before the IsConfigured checks (#64): a team that has deliberately
        // switched Zoom off should not also be told it has not finished configuring Zoom. Both are
        // true and only one is useful.
        var zoomEnabled = integrationState.ShouldCall(team, TeamIntegration.Zoom, "creating or updating a Zoom meeting");
        var discordEnabled = integrationState.ShouldCall(team, TeamIntegration.Discord, "creating or updating a Discord event");

        if (zoomEnabled && team.IsZoomConfigured)
        {
            var zoomCredentials = team.ToZoomCredentials();
            var zoomRequest = new ZoomMeetingRequest(session.Title, session.ScheduledStartUtc, session.DurationMinutes, team.ZoomBreakoutRoomCount);
            if (session.ZoomMeetingId is null)
            {
                // Same reasoning as the Discord dedup check below: guard against a previous poll
                // that crashed/restarted after Zoom's API call succeeded but before the returned
                // id was persisted.
                var existingMeeting = await FindExistingMeetingAsync(zoomCredentials, session, cancellationToken);
                if (existingMeeting is not null)
                {
                    logger.LogWarning(
                        "Found an existing Zoom meeting {ZoomMeetingId} matching session {ExamToolsSessionId} by topic/time — adopting it instead of creating a duplicate (likely a previous poll crashed after creating it but before saving its id)",
                        existingMeeting.Id, session.ExamToolsSessionId);
                    session.ZoomMeetingId = existingMeeting.Id;
                    session.ZoomJoinUrl = existingMeeting.JoinUrl;
                }
                else
                {
                    var meeting = await zoomClient.CreateMeetingAsync(zoomCredentials, zoomRequest, cancellationToken);
                    session.ZoomMeetingId = meeting.Id;
                    session.ZoomJoinUrl = meeting.JoinUrl;
                }

                // Save the instant the id is known, before Discord's work (a second round trip)
                // even starts for this session — the id is the only handle back to this meeting
                // for every future update/delete, so the window where it's known only in memory
                // needs to be as small as possible, not "however long the rest of this method
                // takes." The dedup check above is the backstop for what's left of that window.
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            else
            {
                await zoomClient.UpdateMeetingAsync(zoomCredentials, session.ZoomMeetingId, zoomRequest, cancellationToken);
            }
        }

        // Discord's location/description needs the Zoom join link, so it can't do anything
        // meaningful until Zoom has actually produced one, regardless of Discord's own config.
        // Both gates must be true: the shared bot ready, and this team has picked a Guild.
        if (discordEnabled && discordEventClient.IsConfigured && team.IsDiscordConfigured && session.ZoomJoinUrl is not null)
        {
            var guildId = team.DiscordGuildId!.Value;
            var endTimeUtc = session.ScheduledStartUtc.AddMinutes(session.DurationMinutes);
            var discordRequest = new DiscordEventRequest(
                session.Title,
                $"Ham radio VE exam session. Join via Zoom: {session.ZoomJoinUrl}",
                session.ScheduledStartUtc,
                endTimeUtc,
                session.ZoomJoinUrl);

            if (session.DiscordEventId is null)
            {
                // Guard against a previous poll that crashed/restarted after Discord's API call
                // succeeded but before the returned id was persisted (see docs/zoom-discord-scheduling.md's "Duplicate
                // Discord scheduled events" entry) — adopt a matching existing event instead of
                // creating a second one for the same session.
                var existingEvent = await FindExistingEventAsync(guildId, session, cancellationToken);
                if (existingEvent is not null)
                {
                    logger.LogWarning(
                        "Found an existing Discord event {DiscordEventId} matching session {ExamToolsSessionId} by name/time — adopting it instead of creating a duplicate (likely a previous poll crashed after creating it but before saving its id)",
                        existingEvent.Id, session.ExamToolsSessionId);
                    session.DiscordEventId = existingEvent.Id;
                }
                else
                {
                    var scheduledEvent = await discordEventClient.CreateEventAsync(guildId, discordRequest, cancellationToken);
                    session.DiscordEventId = scheduledEvent.Id;
                }

                // Save immediately for the same reason as Zoom's id above — this is the last step
                // in the method, but the caller (RunAsync) still does other work (computing/logging
                // the result) before its own save, so this isn't redundant.
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            else
            {
                try
                {
                    await discordEventClient.UpdateEventAsync(guildId, session.DiscordEventId, discordRequest, cancellationToken);
                }
                catch (DiscordEventNotFoundException ex)
                {
                    // Somebody deleted the event in Discord. Forgetting the id is the whole
                    // recovery: discordSettled below reads "id is not null", so clearing it leaves
                    // the session unsettled and the create branch above picks it up next pass —
                    // and that branch lists the guild first, so an event recreated by hand in the
                    // meantime is adopted rather than duplicated.
                    //
                    // ⚠️ Not rethrown, so the pass is not counted as a failure. Before this, the
                    // id stayed and every tick logged the same error forever while the session
                    // silently never got another event (found in Mike's Worker log, 2026-08-21).
                    // Only THIS exception is swallowed — a permission problem or a bad token still
                    // surfaces, because forgetting the id there would create a duplicate the
                    // moment access came back.
                    logger.LogWarning(ex,
                        "Discord event {DiscordEventId} for session {ExamToolsSessionId} no longer exists — forgetting it so a new one is created on the next pass",
                        session.DiscordEventId, session.ExamToolsSessionId);
                    session.DiscordEventId = null;
                    await dbContext.SaveChangesAsync(cancellationToken);
                }
            }
        }

        // Still deliberately *not* "OR not configured" — an unconfigured integration must stay
        // pending forever (re-checked every poll, one quiet aggregate line via
        // LogUnconfiguredIntegrations) so it backfills automatically the moment it is configured,
        // exactly like Phase 3/4's optional integrations.
        //
        // "OR switched off" is the opposite case and IS correct here (#64), which is worth stating
        // because it looks like the antipattern CLAUDE.md warns about and is its mirror image. That
        // warning is about *unconfigured*, where the whole point is to keep retrying. A deliberate,
        // indefinite switch must settle instead: never retried, nothing queued for re-enabling.
        //
        // This is also what closes #289. A team using Zoom but deliberately not Discord could never
        // settle, so the else-branch re-PATCHed every future session on every poll — roughly 2,880
        // Zoom calls a day for ten sessions, forever, for data that had not changed. Switching
        // Discord off now makes "deliberately not Discord" expressible, and the session settles.
        var zoomSettled = !zoomEnabled || session.ZoomMeetingId is not null;
        var discordSettled = !discordEnabled || session.DiscordEventId is not null;
        if (zoomSettled && discordSettled)
        {
            session.ZoomDiscordSyncedStartUtc = session.ScheduledStartUtc;
        }
    }

    /// <summary>Matches by name + start time (within a minute — Discord's own timestamp granularity plus float/round-trip slack) rather than requiring an exact tick match.</summary>
    private async Task<DiscordEvent?> FindExistingEventAsync(ulong guildId, Session session, CancellationToken cancellationToken)
    {
        var events = await discordEventClient.ListEventsAsync(guildId, cancellationToken);
        return events.FirstOrDefault(e =>
            e.Name == session.Title &&
            e.StartTimeUtc is not null &&
            (e.StartTimeUtc.Value - session.ScheduledStartUtc).Duration() < TimeSpan.FromMinutes(1));
    }

    /// <summary>Matches by topic + start time (within a minute, same slack as FindExistingEventAsync) rather than requiring an exact tick match.</summary>
    private async Task<ZoomMeeting?> FindExistingMeetingAsync(ZoomCredentials credentials, Session session, CancellationToken cancellationToken)
    {
        var meetings = await zoomClient.ListMeetingsAsync(credentials, cancellationToken);
        return meetings.FirstOrDefault(m =>
            m.Topic == session.Title &&
            m.StartTimeUtc is not null &&
            (m.StartTimeUtc.Value - session.ScheduledStartUtc).Duration() < TimeSpan.FromMinutes(1));
    }

    /// <summary>Returns true once every piece this session still has an id for has actually been torn down — false means at least one piece is still pending purely because its integration isn't configured yet (see LogUnconfiguredCleanups), not a failure.</summary>
    private async Task<bool> CleanupZoomAndDiscordAsync(Team team, Session session, DateTime now, CancellationToken cancellationToken)
    {
        var fullyCleanedUp = true;

        if (session.ZoomMeetingId is not null)
        {
            // Switched off deletes nothing, and settles rather than retrying (#64). The meeting is
            // left in the real Zoom account permanently — an accepted consequence, stated in the
            // issue: a muted team must not reach into a real account for ANY reason, teardown
            // included. ZoomMeetingId is deliberately left set, so the orphan is still visible here
            // rather than being forgotten locally too. Safe order when muting a team with live
            // resources is therefore: clean up first, switch off second.
            if (!integrationState.ShouldCall(team, TeamIntegration.Zoom, "deleting a Zoom meeting"))
            {
                // fullyCleanedUp stays true: settled without doing it, never re-attempted.
            }
            else if (!team.IsZoomConfigured)
            {
                // An existing meeting still needs tearing down even if the team's Zoom setup
                // changed (or was never finished) since it was created, but there's no way to call
                // Zoom's API without credentials — leave ZoomMeetingId set so this retries
                // automatically the moment the team's Zoom config is added, same as every other
                // optional-integration gate in this app. See LogUnconfiguredCleanups for the
                // one-aggregate-line-per-run log this produces instead of a repeating [ERR].
                fullyCleanedUp = false;
            }
            else
            {
                var zoomCredentials = team.ToZoomCredentials();
                await zoomClient.DeleteMeetingAsync(zoomCredentials, session.ZoomMeetingId, cancellationToken);
                // userId null = system action, not a person.
                dbContext.AddAuditLog(null, "ZoomMeetingCancelled", nameof(Session), session.Id,
                    $"Zoom meeting {session.ZoomMeetingId} cancelled for ExamTools session {session.ExamToolsSessionId}.", now,
                    teamId: team.Id);
                session.ZoomMeetingId = null;
                session.ZoomJoinUrl = null;
            }
        }

        if (session.DiscordEventId is not null)
        {
            // Same as Zoom above — suppressed, settled, and the event left in the real guild.
            if (!integrationState.ShouldCall(team, TeamIntegration.Discord, "deleting a Discord event"))
            {
                // fullyCleanedUp stays true.
            }
            else if (!discordEventClient.IsConfigured || team.DiscordGuildId is null)
            {
                // Same reasoning as Zoom's guard above.
                fullyCleanedUp = false;
            }
            else
            {
                await discordEventClient.DeleteEventAsync(team.DiscordGuildId.Value, session.DiscordEventId, cancellationToken);
                dbContext.AddAuditLog(null, "DiscordEventCancelled", nameof(Session), session.Id,
                    $"Discord scheduled event {session.DiscordEventId} deleted for ExamTools session {session.ExamToolsSessionId}.", now,
                    teamId: team.Id);
                session.DiscordEventId = null;
            }
        }

        return fullyCleanedUp;
    }
}
