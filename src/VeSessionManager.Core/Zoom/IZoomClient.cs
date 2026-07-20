namespace VeSessionManager.Core.Zoom;

/// <summary>
/// Client for the subset of the Zoom Meetings API Phase 2 needs. Wrapped in an interface so
/// scheduling logic can be unit tested without live calls (per the spec's testing rules).
/// </summary>
public interface IZoomClient
{
    /// <summary>True once AccountId/ClientId/ClientSecret are all set. Zoom is an optional integration — a team that hasn't finished Zoom S2S OAuth app setup yet must not see a repeated failed-call error every poll; SessionEventSchedulingService checks this before attempting Zoom at all.</summary>
    bool IsConfigured { get; }

    Task<ZoomMeeting> CreateMeetingAsync(ZoomMeetingRequest request, CancellationToken cancellationToken);

    Task UpdateMeetingAsync(string meetingId, ZoomMeetingRequest request, CancellationToken cancellationToken);

    Task DeleteMeetingAsync(string meetingId, CancellationToken cancellationToken);
}
