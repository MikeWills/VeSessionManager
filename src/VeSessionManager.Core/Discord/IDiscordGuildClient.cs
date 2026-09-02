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

    /// <summary>
    /// Every member of the guild, with the roles each holds — what the tag sync matches against.
    ///
    /// <para><b>Needs the <c>GUILD_MEMBERS</c> privileged intent</b>, enabled for the bot application
    /// in the Discord developer portal. Unlike <see cref="ListRolesAsync"/>, which reads roles off the
    /// guild object, this calls Discord's paged member list, and without the intent it comes back
    /// <b>empty rather than failing</b>.</para>
    ///
    /// <para>That silence is why every caller must treat an empty result as "could not read", never as
    /// "the server has nobody in it" — a real guild always contains at least the bot. Under the sync's
    /// rule, "holds no role" means "remove the mapped tag", so an empty list read literally would
    /// strip every mapped tag from every matched VE. See docs/discord-tag-sync.md.</para>
    /// </summary>
    Task<IReadOnlyList<DiscordGuildMember>> ListMembersAsync(ulong guildId, CancellationToken cancellationToken);
}

/// <param name="Id">The member's snowflake — the identity that survives every rename.</param>
/// <param name="Username">The account's own name, stable-ish and global to Discord.</param>
/// <param name="DisplayName">What the server shows: the per-guild nickname where one is set, otherwise the account's display name. This is where a call sign usually is.</param>
/// <param name="Nickname">The per-guild nickname alone, or null. Kept beside <paramref name="DisplayName"/> so a report can say which one carried the match.</param>
/// <param name="RoleIds">Roles held in this guild. Never includes @everyone, which every member holds and no tag may map to.</param>
public record DiscordGuildMember(
    ulong Id,
    string Username,
    string DisplayName,
    string? Nickname,
    IReadOnlyList<ulong> RoleIds);

/// <param name="Id">The role's snowflake — the stable identity, and what a mapping stores.</param>
/// <param name="Name">What it is called today. Stored beside the id only as a display snapshot; roles get renamed.</param>
public record DiscordRoleSummary(ulong Id, string Name);
