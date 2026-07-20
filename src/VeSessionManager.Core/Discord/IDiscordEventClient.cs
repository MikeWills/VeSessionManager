namespace VeSessionManager.Core.Discord;

/// <summary>
/// Client for the subset of Discord's guild scheduled events API Phase 2 needs. Wrapped in an
/// interface so scheduling logic can be unit tested without live calls (per the spec's testing
/// rules) — Discord.Net's own types aren't easily mockable directly.
/// </summary>
public interface IDiscordEventClient
{
    /// <summary>True once BotToken and GuildId are both set. Discord is an optional integration — a team that hasn't set up (or doesn't want) the Discord bot must not see a repeated failed-call error every poll; SessionEventSchedulingService checks this before attempting Discord at all, and still creates/updates the Zoom meeting regardless.</summary>
    bool IsConfigured { get; }

    Task<DiscordEvent> CreateEventAsync(DiscordEventRequest request, CancellationToken cancellationToken);

    Task UpdateEventAsync(string eventId, DiscordEventRequest request, CancellationToken cancellationToken);

    Task DeleteEventAsync(string eventId, CancellationToken cancellationToken);
}
