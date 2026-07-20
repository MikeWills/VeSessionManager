namespace VeSessionManager.Core.Discord;

/// <summary>
/// Client for the subset of Discord's guild scheduled events API Phase 2 needs. Wrapped in an
/// interface so scheduling logic can be unit tested without live calls (per the spec's testing
/// rules) — Discord.Net's own types aren't easily mockable directly.
/// </summary>
public interface IDiscordEventClient
{
    /// <summary>True once the shared bot's BotToken is set. Discord is an optional integration — a deployment that hasn't set up (or doesn't want) the Discord bot must not see a repeated failed-call error every poll. This is only the bot-level gate; SessionEventSchedulingService also requires the calling Team's own GuildId to be set (Team.IsDiscordConfigured) before attempting Discord at all, and still creates/updates the Zoom meeting regardless.</summary>
    bool IsConfigured { get; }

    Task<DiscordEvent> CreateEventAsync(ulong guildId, DiscordEventRequest request, CancellationToken cancellationToken);

    Task UpdateEventAsync(ulong guildId, string eventId, DiscordEventRequest request, CancellationToken cancellationToken);

    Task DeleteEventAsync(ulong guildId, string eventId, CancellationToken cancellationToken);
}
