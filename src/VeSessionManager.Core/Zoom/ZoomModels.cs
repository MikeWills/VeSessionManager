using System.Text.Json.Serialization;

namespace VeSessionManager.Core.Zoom;

/// <summary>Domain-facing request for creating/updating a meeting — ZoomClient maps this to Zoom's wire format. BreakoutRoomCount &lt;= 0 omits the breakout_room block from the request entirely, rather than sending enable:false.</summary>
public record ZoomMeetingRequest(string Topic, DateTime StartTimeUtc, int DurationMinutes, int BreakoutRoomCount = 0);

public class ZoomMeeting
{
    public required string Id { get; set; }
    public required string JoinUrl { get; set; }

    /// <summary>Only populated by <see cref="IZoomClient.ListMeetingsAsync"/> — used to de-duplicate against a meeting a previous, crashed poll already created. <see cref="IZoomClient.CreateMeetingAsync"/>'s return value doesn't need these (the caller already has them from the request it just sent).</summary>
    public string? Topic { get; set; }

    public DateTime? StartTimeUtc { get; set; }
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

    /// <summary>Null (not serialized, System.Text.Json's default) when no breakout rooms are requested.</summary>
    public ZoomMeetingWireSettings? Settings { get; set; }
}

internal class ZoomMeetingWireSettings
{
    [JsonPropertyName("breakout_room")]
    public required ZoomMeetingWireBreakoutRoom BreakoutRoom { get; set; }
}

/// <summary>Verified live 2026-07-28 against a real meeting — despite years-old devforum reports that the API silently ignores this block, it works on this account: rooms actually persist and show up in the Zoom client's own Breakout Room Assignment UI.</summary>
internal class ZoomMeetingWireBreakoutRoom
{
    public bool Enable { get; set; } = true;
    public List<ZoomMeetingWireBreakoutRoomEntry> Rooms { get; set; } = [];
}

internal class ZoomMeetingWireBreakoutRoomEntry
{
    public string Name { get; set; } = "";

    /// <summary>
    /// Never assigned, so every create-meeting request ships <c>"participants": []</c>.
    ///
    /// <para><b>Kept deliberately</b> (#360, closed 2026-08-16). It is a pre-assignment list — this
    /// app creates empty breakout rooms and lets the host move people at test time — so on the face
    /// of it the property does nothing and a dead-code sweep has flagged it once already.</para>
    ///
    /// <para>Removing it is not free: it changes an outbound payload, and an empty array and an
    /// absent key are not always the same thing to an API. This one creates the breakout rooms a
    /// session actually runs in, so the failure mode is rooms with the wrong structure, discovered
    /// by a VE on exam day. The current shape demonstrably works. Deleting it would need checking
    /// against Zoom's schema or a real test meeting, to save one line — so it stays, and this note
    /// exists so the next sweep does not re-derive the same answer.</para>
    /// </summary>
    public List<string> Participants { get; set; } = [];
}

internal class ZoomMeetingWireResponse
{
    public long Id { get; set; }

    [JsonPropertyName("join_url")]
    public string JoinUrl { get; set; } = "";
}

// Wire DTOs for GET /users/{userId}/meetings ("List meetings") — used only by ListMeetingsAsync's
// dedup check, see https://developers.zoom.us/docs/api/meetings/ma/#tag/meetings/GET/users/{userId}/meetings.

internal class ZoomMeetingListWireResponse
{
    public List<ZoomMeetingListItemWireResponse> Meetings { get; set; } = [];

    /// <summary>
    /// Zoom's cursor for the next page, empty when this is the last one. The DTO had no such field
    /// at all, so every response past the first page was invisible — see ListMeetingsAsync (#251).
    /// </summary>
    [JsonPropertyName("next_page_token")]
    public string? NextPageToken { get; set; }
}

internal class ZoomMeetingListItemWireResponse
{
    public long Id { get; set; }

    public string Topic { get; set; } = "";

    [JsonPropertyName("start_time")]
    public DateTime StartTime { get; set; }

    [JsonPropertyName("join_url")]
    public string JoinUrl { get; set; } = "";
}
