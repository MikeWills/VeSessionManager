using System.Text.Json.Serialization;

namespace VeSessionManager.Core.Zoom;

/// <summary>Domain-facing request for creating/updating a meeting — ZoomClient maps this to Zoom's wire format.</summary>
public record ZoomMeetingRequest(string Topic, DateTime StartTimeUtc, int DurationMinutes);

public class ZoomMeeting
{
    public required string Id { get; set; }
    public required string JoinUrl { get; set; }
}

// Wire DTOs below mirror Zoom's snake_case JSON exactly — see
// https://developers.zoom.us/docs/api/meetings/ ("Create a meeting" / "Update a meeting").

internal class ZoomTokenResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = "";

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }
}

internal class ZoomMeetingWireRequest
{
    public string Topic { get; set; } = "";

    /// <summary>2 = scheduled meeting.</summary>
    public int Type { get; set; } = 2;

    [JsonPropertyName("start_time")]
    public string StartTime { get; set; } = "";

    public int Duration { get; set; }

    public string Timezone { get; set; } = "UTC";
}

internal class ZoomMeetingWireResponse
{
    public long Id { get; set; }

    [JsonPropertyName("join_url")]
    public string JoinUrl { get; set; } = "";
}
