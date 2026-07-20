namespace VeSessionManager.Core.Zoom;

/// <summary>
/// Client for the subset of the Zoom Meetings API Phase 2 needs. Wrapped in an interface so
/// scheduling logic can be unit tested without live calls (per the spec's testing rules).
/// </summary>
public interface IZoomClient
{
    Task<ZoomMeeting> CreateMeetingAsync(ZoomMeetingRequest request, CancellationToken cancellationToken);

    Task UpdateMeetingAsync(string meetingId, ZoomMeetingRequest request, CancellationToken cancellationToken);

    Task DeleteMeetingAsync(string meetingId, CancellationToken cancellationToken);
}
