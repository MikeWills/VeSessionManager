namespace VeSessionManager.Core.Discord;

/// <summary>
/// Client for the subset of Discord's guild scheduled events API Phase 2 needs. Wrapped in an
/// interface so scheduling logic can be unit tested without live calls (per the spec's testing
/// rules) — Discord.Net's own types aren't easily mockable directly.
/// </summary>
public interface IDiscordEventClient
{
    Task<DiscordEvent> CreateEventAsync(DiscordEventRequest request, CancellationToken cancellationToken);

    Task UpdateEventAsync(string eventId, DiscordEventRequest request, CancellationToken cancellationToken);

    Task DeleteEventAsync(string eventId, CancellationToken cancellationToken);
}
