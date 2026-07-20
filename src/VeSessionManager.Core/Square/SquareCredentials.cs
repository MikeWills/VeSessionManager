namespace VeSessionManager.Core.Square;

/// <summary>Per-Team Square merchant account credentials — TeamId keys SquareClient's internal per-team cached SDK client, since each team has its own separate Square account (not shared across teams).</summary>
public sealed record SquareCredentials(int TeamId, string AccessToken, string LocationId);
