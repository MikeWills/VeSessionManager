using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Discord;
using VeSessionManager.Core.Entities;
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
/// Per spec: cancellation cleanup never sends any candidate-facing notification — that is a
/// manual Session Manager action, not something this job (or any later phase) should do.
/// </summary>
public class SessionEventSchedulingService(
    AppDbContext dbContext,
    IZoomClient zoomClient,
    IDiscordEventClient discordEventClient,
    TimeProvider timeProvider,
    ILogger<SessionEventSchedulingService> logger)
{
    public async Task<SchedulingResult> RunAsync(Team team, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var result = new SchedulingResult();

        var sessionsNeedingSync = await dbContext.Sessions
            .Where(s => s.TeamId == team.Id && s.Status == SessionStatus.Active && s.ScheduledStartUtc != s.ZoomDiscordSyncedStartUtc)
            .ToListAsync(cancellationToken);

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
            .Where(s => s.TeamId == team.Id && s.Status == SessionStatus.Cancelled && (s.ZoomMeetingId != null || s.DiscordEventId != null))
            .ToListAsync(cancellationToken);

        foreach (var session in sessionsNeedingCleanup)
        {
            try
            {
                await CleanupZoomAndDiscordAsync(team, session, now, cancellationToken);
                result.SessionsCleanedUp++;
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

    private async Task SyncZoomAndDiscordAsync(Team team, Session session, CancellationToken cancellationToken)
    {
        if (team.IsZoomConfigured)
        {
            var zoomCredentials = new ZoomCredentials(team.Id, team.ZoomAccountId!, team.ZoomClientId!, team.ZoomClientSecret!, team.ZoomUserId ?? "me");
            var zoomRequest = new ZoomMeetingRequest(session.Title, session.ScheduledStartUtc, session.DurationMinutes);
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
            }
            else
            {
                await zoomClient.UpdateMeetingAsync(zoomCredentials, session.ZoomMeetingId, zoomRequest, cancellationToken);
            }
        }

        // Discord's location/description needs the Zoom join link, so it can't do anything
        // meaningful until Zoom has actually produced one, regardless of Discord's own config.
        // Both gates must be true: the shared bot ready, and this team has picked a Guild.
        if (discordEventClient.IsConfigured && team.IsDiscordConfigured && session.ZoomJoinUrl is not null)
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
                // succeeded but before the returned id was persisted (see TODO.md's "Duplicate
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
            }
            else
            {
                await discordEventClient.UpdateEventAsync(guildId, session.DiscordEventId, discordRequest, cancellationToken);
            }
        }

        // Deliberately *not* "OR not configured" — an unconfigured integration must stay pending
        // forever (re-checked every poll, one quiet aggregate log line via
        // LogUnconfiguredIntegrations) so it backfills automatically the moment it's configured,
        // exactly like Phase 3/4's optional integrations. Only an integration that has actually
        // produced its id counts as settled.
        if (session.ZoomMeetingId is not null && session.DiscordEventId is not null)
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

    private async Task CleanupZoomAndDiscordAsync(Team team, Session session, DateTime now, CancellationToken cancellationToken)
    {
        if (session.ZoomMeetingId is not null)
        {
            var zoomCredentials = new ZoomCredentials(team.Id, team.ZoomAccountId!, team.ZoomClientId!, team.ZoomClientSecret!, team.ZoomUserId ?? "me");
            await zoomClient.DeleteMeetingAsync(zoomCredentials, session.ZoomMeetingId, cancellationToken);
            dbContext.AuditLogs.Add(new AuditLog
            {
                UserId = null, // system action, not a person
                Action = "ZoomMeetingCancelled",
                EntityType = nameof(Session),
                EntityId = session.Id,
                TimestampUtc = now,
                Details = $"Zoom meeting {session.ZoomMeetingId} cancelled for ExamTools session {session.ExamToolsSessionId}."
            });
            session.ZoomMeetingId = null;
            session.ZoomJoinUrl = null;
        }

        if (session.DiscordEventId is not null)
        {
            if (team.DiscordGuildId is null)
            {
                // Same reasoning as Zoom's cleanup guard above: cleanup always attempts regardless
                // of current configuration (an existing event needs tearing down even if the
                // team's Discord setup changed since it was created), so this needs a clear
                // exception rather than a confusing null-guildId call to Discord's API.
                throw new InvalidOperationException(
                    $"Team {team.Id} has no DiscordGuildId set but Session {session.Id} still has a DiscordEventId — cannot clean it up without knowing which guild it's in.");
            }

            await discordEventClient.DeleteEventAsync(team.DiscordGuildId.Value, session.DiscordEventId, cancellationToken);
            dbContext.AuditLogs.Add(new AuditLog
            {
                UserId = null,
                Action = "DiscordEventCancelled",
                EntityType = nameof(Session),
                EntityId = session.Id,
                TimestampUtc = now,
                Details = $"Discord scheduled event {session.DiscordEventId} deleted for ExamTools session {session.ExamToolsSessionId}."
            });
            session.DiscordEventId = null;
        }
    }
}
