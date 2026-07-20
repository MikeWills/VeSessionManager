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
///     "update". ZoomDiscordSyncedStartUtc is only advanced once *both* succeed, so a session
///     where Zoom succeeded but Discord failed is retried correctly on the next run (Zoom is
///     skipped since its id is already set; Discord is retried since its id is still null).
///   - Status Cancelled and either id is still set -> needs cleanup (delete + null the id).
/// This means a poll that crashes or fails partway always resumes correctly next run purely by
/// re-reading Session state — matching Phase 1's polling philosophy.
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
    public async Task<SchedulingResult> RunAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var result = new SchedulingResult();

        var sessionsNeedingSync = await dbContext.Sessions
            .Where(s => s.Status == SessionStatus.Active && s.ScheduledStartUtc != s.ZoomDiscordSyncedStartUtc)
            .ToListAsync(cancellationToken);

        foreach (var session in sessionsNeedingSync)
        {
            try
            {
                await SyncZoomAndDiscordAsync(session, cancellationToken);
                result.SessionsSynced++;
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
            .Where(s => s.Status == SessionStatus.Cancelled && (s.ZoomMeetingId != null || s.DiscordEventId != null))
            .ToListAsync(cancellationToken);

        foreach (var session in sessionsNeedingCleanup)
        {
            try
            {
                await CleanupZoomAndDiscordAsync(session, now, cancellationToken);
                result.SessionsCleanedUp++;
            }
            catch (Exception ex)
            {
                result.SessionsFailed++;
                logger.LogError(ex, "Failed to clean up Zoom/Discord for cancelled session {ExamToolsSessionId}", session.ExamToolsSessionId);
            }

            await dbContext.SaveChangesAsync(cancellationToken);
        }

        logger.LogInformation("Session event scheduling finished: {Result}", result);
        return result;
    }

    private async Task SyncZoomAndDiscordAsync(Session session, CancellationToken cancellationToken)
    {
        var zoomRequest = new ZoomMeetingRequest(session.Title, session.ScheduledStartUtc, session.DurationMinutes);
        if (session.ZoomMeetingId is null)
        {
            var meeting = await zoomClient.CreateMeetingAsync(zoomRequest, cancellationToken);
            session.ZoomMeetingId = meeting.Id;
            session.ZoomJoinUrl = meeting.JoinUrl;
        }
        else
        {
            await zoomClient.UpdateMeetingAsync(session.ZoomMeetingId, zoomRequest, cancellationToken);
        }

        var endTimeUtc = session.ScheduledStartUtc.AddMinutes(session.DurationMinutes);
        var discordRequest = new DiscordEventRequest(
            session.Title,
            $"Ham radio VE exam session. Join via Zoom: {session.ZoomJoinUrl}",
            session.ScheduledStartUtc,
            endTimeUtc,
            session.ZoomJoinUrl!);

        if (session.DiscordEventId is null)
        {
            var scheduledEvent = await discordEventClient.CreateEventAsync(discordRequest, cancellationToken);
            session.DiscordEventId = scheduledEvent.Id;
        }
        else
        {
            await discordEventClient.UpdateEventAsync(session.DiscordEventId, discordRequest, cancellationToken);
        }

        // Only advance once both sides reflect the current time — see class remarks.
        session.ZoomDiscordSyncedStartUtc = session.ScheduledStartUtc;
    }

    private async Task CleanupZoomAndDiscordAsync(Session session, DateTime now, CancellationToken cancellationToken)
    {
        if (session.ZoomMeetingId is not null)
        {
            await zoomClient.DeleteMeetingAsync(session.ZoomMeetingId, cancellationToken);
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
            await discordEventClient.DeleteEventAsync(session.DiscordEventId, cancellationToken);
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
