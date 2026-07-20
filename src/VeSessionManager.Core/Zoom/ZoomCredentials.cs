namespace VeSessionManager.Core.Zoom;

/// <summary>Per-Team Zoom Server-to-Server OAuth app credentials — TeamId keys ZoomClient's internal per-team cached access token, since each team has its own separate Zoom subscription/OAuth app (not shared across teams).</summary>
public sealed record ZoomCredentials(int TeamId, string AccountId, string ClientId, string ClientSecret, string UserId);
