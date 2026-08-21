namespace VeSessionManager.Core.Discord;

/// <summary>
/// Posting a message into a Discord text channel (#401 PR4) — the second thing this app does with
/// Discord, alongside <see cref="IDiscordEventClient"/>'s scheduled events.
///
/// <para><b>A separate interface, same implementation.</b> <c>DiscordEventClient</c> implements both
/// and is registered once, so the bot login it caches is shared; splitting the contract keeps
/// <c>MessageDispatchService</c> depending on the one call it makes rather than on four event methods
/// it has no business with, and makes it a two-line fake in tests.</para>
/// </summary>
public interface IDiscordChannelMessageClient
{
    /// <summary>True once the shared bot's BotToken is set — the same deployment-wide gate the event client uses. A team additionally needs its own <c>DiscordGuildId</c>, and a rule its own channel id.</summary>
    bool IsConfigured { get; }

    /// <param name="guildId">The team's guild. Taken rather than inferred: the bot may be in several.</param>
    /// <param name="channelId">The channel the rule names. A rule-level setting, so one team can post different rules to different rooms.</param>
    /// <param name="message">Ready to post — plain text with Discord markdown, never HTML. See <c>DiscordMessageText</c>.</param>
    /// <param name="mentionableRoleIds">
    /// Role ids this post may ping. Empty — the default for every team — means nothing in the message
    /// resolves, which is what keeps an unescaped candidate name from pinging a server (#116).
    /// </param>
    Task PostMessageAsync(ulong guildId, ulong channelId, string message, IReadOnlyList<ulong> mentionableRoleIds, CancellationToken cancellationToken);
}
