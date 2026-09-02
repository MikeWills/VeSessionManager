using Discord;
using Discord.Rest;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VeSessionManager.Core.Discord;

/// <summary>
/// Wraps a Discord.Net DiscordRestClient (REST-only — no gateway connection needed for a
/// background poller) to create/modify/delete guild scheduled events. Each session's Zoom join
/// link is stored as the event's "location" for an External-type event, per
/// https://docs.discordnet.dev/guides/guild_events/creating-guild-events.html. Registered as a
/// singleton so the login only happens once; bot tokens don't expire, unlike Zoom's.
/// </summary>
public sealed class DiscordEventClient : IDiscordEventClient, IDiscordChannelMessageClient, IDiscordGuildClient, IDisposable
{
    private readonly DiscordOptions _options;
    private readonly ILogger<DiscordEventClient> _logger;
    private readonly DiscordRestClient _client = new();
    private readonly SemaphoreSlim _loginLock = new(1, 1);
    private bool _loggedIn;

    public DiscordEventClient(IOptions<DiscordOptions> options, ILogger<DiscordEventClient> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.BotToken);

    public async Task<DiscordEvent> CreateEventAsync(ulong guildId, DiscordEventRequest request, CancellationToken cancellationToken)
    {
        var guild = await GetGuildAsync(guildId, cancellationToken);
        var scheduledEvent = await guild.CreateEventAsync(
            request.Name,
            ToOffset(request.StartTimeUtc),
            GuildScheduledEventType.External,
            description: request.Description,
            endTime: ToOffset(request.EndTimeUtc),
            location: request.Location);

        _logger.LogInformation("Created Discord scheduled event {DiscordEventId} in guild {GuildId}", scheduledEvent.Id, guildId);
        return new DiscordEvent { Id = scheduledEvent.Id.ToString() };
    }

    public async Task UpdateEventAsync(ulong guildId, string eventId, DiscordEventRequest request, CancellationToken cancellationToken)
    {
        var guild = await GetGuildAsync(guildId, cancellationToken);
        // A typed exception, not a message: this is the one Discord failure the caller can fix by
        // itself, and it can only do that if it can tell this apart from a permission problem.
        var scheduledEvent = await guild.GetEventAsync(ulong.Parse(eventId))
            ?? throw new DiscordEventNotFoundException(guildId, eventId);

        await scheduledEvent.ModifyAsync(props =>
        {
            props.Name = request.Name;
            props.Description = request.Description;
            props.StartTime = ToOffset(request.StartTimeUtc);
            props.EndTime = ToOffset(request.EndTimeUtc);
            props.Location = request.Location;
        });
        _logger.LogInformation("Updated Discord scheduled event {DiscordEventId} in guild {GuildId}", eventId, guildId);
    }

    public async Task<IReadOnlyList<DiscordEvent>> ListEventsAsync(ulong guildId, CancellationToken cancellationToken)
    {
        var guild = await GetGuildAsync(guildId, cancellationToken);
        var events = await guild.GetEventsAsync();
        return events
            .Where(e => e.Status is GuildScheduledEventStatus.Scheduled or GuildScheduledEventStatus.Active)
            .Select(e => new DiscordEvent { Id = e.Id.ToString(), Name = e.Name, StartTimeUtc = e.StartTime.UtcDateTime })
            .ToList();
    }

    public async Task DeleteEventAsync(ulong guildId, string eventId, CancellationToken cancellationToken)
    {
        var guild = await GetGuildAsync(guildId, cancellationToken);
        var scheduledEvent = await guild.GetEventAsync(ulong.Parse(eventId));
        if (scheduledEvent is null)
        {
            _logger.LogInformation("Discord scheduled event {DiscordEventId} already gone — nothing to delete", eventId);
            return;
        }

        await scheduledEvent.DeleteAsync();
        _logger.LogInformation("Deleted Discord scheduled event {DiscordEventId} in guild {GuildId}", eventId, guildId);
    }

    /// <summary>
    /// Posts into a text channel (#401 PR4). Deliberately not "create if missing" or idempotent in
    /// any way of its own: a Discord message cannot be matched back to the thing that sent it the way
    /// a scheduled event can be matched by name and time, so <b>the caller's
    /// <c>MessageRuleRun</c> marker is the only thing standing between a retry and a duplicate
    /// post</b>. That is the same rule the rest of this app follows for a non-idempotent external
    /// call; it is worth stating here because the methods above it all query first.
    /// </summary>
    public async Task PostMessageAsync(ulong guildId, ulong channelId, string message, IReadOnlyList<ulong> mentionableRoleIds, CancellationToken cancellationToken)
    {
        var guild = await GetGuildAsync(guildId, cancellationToken);
        var channel = await guild.GetTextChannelAsync(channelId)
            ?? throw new InvalidOperationException(
                $"Discord channel {channelId} was not found in guild {guildId}, or the bot cannot see it. " +
                "Check the channel id on the message rule, and that the bot has View Channel + Send Messages there.");

        // The control that makes DiscordMessageText's decision not to escape markdown safe: a
        // candidate whose name is "@everyone" cannot ping the server, whatever the text says, because
        // no mention in this message resolves. Enforced at the API rather than by string-mangling that
        // would have to anticipate every syntax Discord adds.
        //
        // A team may now name roles it wants pingable (#116). That stays an allow-list rather than a
        // switch — @everyone/@here are a separate AllowedMentionTypes flag that is never set, and user
        // mentions never resolve — so the guarantee above survives being granted. Empty, the default
        // for every team, is exactly the old AllowedMentions.None.
        await channel.SendMessageAsync(message, allowedMentions: DiscordMentionPolicy.For(mentionableRoleIds));
        _logger.LogInformation("Posted a message to Discord channel {ChannelId} in guild {GuildId}", channelId, guildId);
    }

    /// <summary>See <see cref="IDiscordChannelMessageClient.ListTextChannelsAsync"/>.</summary>
    public async Task<IReadOnlyList<DiscordChannelSummary>> ListTextChannelsAsync(ulong guildId, CancellationToken cancellationToken)
    {
        var guild = await GetGuildAsync(guildId, cancellationToken);
        var channels = await guild.GetTextChannelsAsync();
        return [.. channels.OrderBy(c => c.Position).Select(c => new DiscordChannelSummary(c.Id, c.Name))];
    }

    /// <summary>See <see cref="IDiscordGuildClient.ListRolesAsync"/>.</summary>
    public async Task<IReadOnlyList<DiscordRoleSummary>> ListRolesAsync(ulong guildId, CancellationToken cancellationToken)
    {
        var guild = await GetGuildAsync(guildId, cancellationToken);

        // Roles come off the already-fetched guild — no extra call, and no privileged intent involved
        // (that gate is on the member list, which arrives with the sync itself).
        //
        // @everyone is filtered out rather than shown and rejected later: Discord models it as a real
        // role whose id equals the guild id, every member holds it, and a tag mapped to it could be
        // added to the whole roster and never removed from anyone.
        return
        [
            .. guild.Roles
                .Where(r => r.Id != guildId)
                .OrderByDescending(r => r.Position)
                .Select(r => new DiscordRoleSummary(r.Id, r.Name))
        ];
    }

    /// <summary>See <see cref="IDiscordGuildClient.ListMembersAsync"/> — including why an empty result is not "the server is empty".</summary>
    public async Task<IReadOnlyList<DiscordGuildMember>> ListMembersAsync(ulong guildId, CancellationToken cancellationToken)
    {
        var guild = await GetGuildAsync(guildId, cancellationToken);
        var members = new List<DiscordGuildMember>();

        // Paged by Discord, 1000 at a time. Enumerated to the end rather than capped: a partial roster
        // is indistinguishable from "these people hold no roles", which under the sync's rule means
        // "remove their tags" — so a cap here would quietly become a data-loss knob.
        await foreach (var page in guild.GetUsersAsync().WithCancellation(cancellationToken))
        {
            foreach (var user in page)
            {
                members.Add(new DiscordGuildMember(
                    user.Id,
                    user.Username,
                    user.DisplayName,
                    user.Nickname,
                    // @everyone is a real role in Discord's model, with the guild's own id. Dropped so
                    // "holds a mapped role" cannot be satisfied by simply being in the server.
                    [.. user.RoleIds.Where(id => id != guildId)]));
            }
        }

        _logger.LogInformation("Read {MemberCount} member(s) from Discord guild {GuildId}", members.Count, guildId);
        return members;
    }

    /// <summary>DateTimeOffset(DateTime, TimeSpan.Zero) requires Kind=Utc (or Unspecified); force it so a value that round-tripped through EF/Sqlite (which drops Kind) never throws.</summary>
    private static DateTimeOffset ToOffset(DateTime utc) =>
        new(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeSpan.Zero);

    private async Task<RestGuild> GetGuildAsync(ulong guildId, CancellationToken cancellationToken)
    {
        await EnsureLoggedInAsync(cancellationToken);
        return await _client.GetGuildAsync(guildId)
            ?? throw new InvalidOperationException(
                $"Discord guild {guildId} was not found, or this bot is not a member of it. " +
                "Check Team.DiscordGuildId and that the bot was actually invited via the OAuth2 URL Generator (bot scope + Manage Events permission) — see docs/zoom-discord-scheduling.md.");
    }

    private async Task EnsureLoggedInAsync(CancellationToken cancellationToken)
    {
        if (_loggedIn)
        {
            return;
        }

        await _loginLock.WaitAsync(cancellationToken);
        try
        {
            if (_loggedIn)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(_options.BotToken))
            {
                throw new InvalidOperationException(
                    "Discord bot token is not configured. Set Discord:BotToken via user-secrets or environment variables.");
            }

            await _client.LoginAsync(TokenType.Bot, _options.BotToken);
            _loggedIn = true;
            _logger.LogInformation("Logged into Discord as a bot — shared across every team");
        }
        finally
        {
            _loginLock.Release();
        }
    }

    public void Dispose()
    {
        _client.Dispose();
        _loginLock.Dispose();
    }
}
