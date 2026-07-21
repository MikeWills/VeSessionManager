namespace VeSessionManager.Core.Discord;

/// <summary>Domain-facing request for creating/updating a guild scheduled event.</summary>
public record DiscordEventRequest(string Name, string Description, DateTime StartTimeUtc, DateTime EndTimeUtc, string Location);

public class DiscordEvent
{
    public required string Id { get; set; }

    /// <summary>Only populated by <see cref="IDiscordEventClient.ListEventsAsync"/> — used to de-duplicate against an event a previous, crashed poll already created. <see cref="IDiscordEventClient.CreateEventAsync"/>'s return value doesn't need these (the caller already has them from the request it just sent).</summary>
    public string? Name { get; set; }

    public DateTime? StartTimeUtc { get; set; }
}
