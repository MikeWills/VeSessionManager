namespace VeSessionManager.Core.Zoom;

/// <summary>
/// Client for the subset of the Zoom Meetings API Phase 2 needs. Wrapped in an interface so
/// scheduling logic can be unit tested without live calls (per the spec's testing rules).
/// </summary>
public interface IZoomClient
{
    Task<ZoomMeeting> CreateMeetingAsync(ZoomCredentials credentials, ZoomMeetingRequest request, CancellationToken cancellationToken);

    Task UpdateMeetingAsync(ZoomCredentials credentials, string meetingId, ZoomMeetingRequest request, CancellationToken cancellationToken);

    Task DeleteMeetingAsync(ZoomCredentials credentials, string meetingId, CancellationToken cancellationToken);

    /// <summary>
    /// This team's scheduled (not yet started/expired) meetings. Used by
    /// SessionEventSchedulingService to check for an already-existing meeting (matched by topic +
    /// start time) before calling CreateMeetingAsync, so a poll that crashed after Zoom's API call
    /// succeeded but before the returned id was persisted doesn't create a duplicate on retry —
    /// same reasoning as IDiscordEventClient.ListEventsAsync, see docs/zoom-discord-scheduling.md.
    /// </summary>
    Task<IReadOnlyList<ZoomMeeting>> ListMeetingsAsync(ZoomCredentials credentials, CancellationToken cancellationToken);
}
