namespace VeSessionManager.Core.Discord;

/// <summary>Domain-facing request for creating/updating a guild scheduled event.</summary>
public record DiscordEventRequest(string Name, string Description, DateTime StartTimeUtc, DateTime EndTimeUtc, string Location);

public class DiscordEvent
{
    public required string Id { get; set; }
}
