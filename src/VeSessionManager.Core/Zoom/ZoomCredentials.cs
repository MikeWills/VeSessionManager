using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Zoom;

/// <summary>Per-Team Zoom Server-to-Server OAuth app credentials — TeamId keys ZoomClient's internal per-team cached access token, since each team has its own separate Zoom subscription/OAuth app (not shared across teams).</summary>
public sealed record ZoomCredentials(int TeamId, string AccountId, string ClientId, string ClientSecret, string UserId);

/// <summary>
/// Single definition of the Team -> ZoomCredentials mapping, including the "me" fallback used when a
/// team hasn't set ZoomUserId explicitly — previously re-typed identically at both call sites in
/// SessionEventSchedulingService, risking the fallback silently drifting between them. Same pattern
/// as Team.ToEmailCredentials()/ToSquareCredentials().
/// </summary>
public static class TeamZoomCredentialsExtensions
{
    /// <summary>
    /// Call only behind an <c>IsConfigured</c> check — the null-forgiving operators here reflect the
    /// optional-integration gate having already run, not a guarantee the columns are populated.
    /// </summary>
    public static ZoomCredentials ToZoomCredentials(this Team team) =>
        new(team.Id, team.ZoomAccountId!, team.ZoomClientId!, team.ZoomClientSecret!, team.ZoomUserId ?? "me");
}
