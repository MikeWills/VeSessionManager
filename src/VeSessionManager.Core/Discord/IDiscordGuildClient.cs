namespace VeSessionManager.Core.Discord;

/// <summary>
/// Guild-level reads — the people and roles in a team's Discord server, rather than the events
/// (<see cref="IDiscordEventClient"/>) or channel posts (<see cref="IDiscordChannelMessageClient"/>)
/// this app already writes (#519).
///
/// <para><b>A third interface on the same implementation</b>, for the reason the second one exists:
/// <c>DiscordEventClient</c> implements all three and is registered once, so the bot login it caches
/// is shared, while each consumer depends only on the calls it actually makes.</para>
///
/// <para><b>Read-only, permanently.</b> Nothing here — or anywhere downstream of it — writes a role,
/// a nickname or a permission back to Discord. Roles are managed in Discord; this app reads them to
/// decide its own tags, and a tag grants nothing (see <c>VeTag</c>). If a future change wants to push
/// a role, that is a new decision and a new interface, not an extra method here.</para>
/// </summary>
public interface IDiscordGuildClient
{
    /// <summary>True once the shared bot's BotToken is set — the same deployment-wide gate the other two clients use. A team additionally needs its own <c>DiscordGuildId</c>.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// The guild's roles, for the VE tag screen's role picker — so an admin maps "Team member" to a
    /// role by name instead of copying an 18-digit id out of Developer Mode. Ordered as Discord
    /// orders them, highest position first, which is how they appear in the server's own settings.
    ///
    /// <para>Excludes <c>@everyone</c>: every member holds it, so mapping a tag to it would tag the
    /// whole roster and could never remove the tag from anyone — a mapping with no meaning, offered
    /// in a list is a mapping someone will eventually pick by mistake.</para>
    ///
    /// <para>A UI convenience, like <c>ListTextChannelsAsync</c> before it. An empty list is the
    /// documented outcome of every failure this can have — no bot token, wrong guild id, bot not in
    /// the server — and the screen falls back to accepting a typed id rather than erroring.</para>
    /// </summary>
    Task<IReadOnlyList<DiscordRoleSummary>> ListRolesAsync(ulong guildId, CancellationToken cancellationToken);
}

/// <param name="Id">The role's snowflake — the stable identity, and what a mapping stores.</param>
/// <param name="Name">What it is called today. Stored beside the id only as a display snapshot; roles get renamed.</param>
public record DiscordRoleSummary(ulong Id, string Name);
