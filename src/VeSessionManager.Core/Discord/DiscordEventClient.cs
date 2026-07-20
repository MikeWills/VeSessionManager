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
public sealed class DiscordEventClient : IDiscordEventClient, IDisposable
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

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.BotToken) && _options.GuildId != 0;

    public async Task<DiscordEvent> CreateEventAsync(DiscordEventRequest request, CancellationToken cancellationToken)
    {
        var guild = await GetGuildAsync(cancellationToken);
        var scheduledEvent = await guild.CreateEventAsync(
            request.Name,
            ToOffset(request.StartTimeUtc),
            GuildScheduledEventType.External,
            description: request.Description,
            endTime: ToOffset(request.EndTimeUtc),
            location: request.Location);

        _logger.LogInformation("Created Discord scheduled event {DiscordEventId}", scheduledEvent.Id);
        return new DiscordEvent { Id = scheduledEvent.Id.ToString() };
    }

    public async Task UpdateEventAsync(string eventId, DiscordEventRequest request, CancellationToken cancellationToken)
    {
        var guild = await GetGuildAsync(cancellationToken);
        var scheduledEvent = await guild.GetEventAsync(ulong.Parse(eventId))
            ?? throw new InvalidOperationException($"Discord scheduled event {eventId} no longer exists (deleted outside the app?).");

        await scheduledEvent.ModifyAsync(props =>
        {
            props.Name = request.Name;
            props.Description = request.Description;
            props.StartTime = ToOffset(request.StartTimeUtc);
            props.EndTime = ToOffset(request.EndTimeUtc);
            props.Location = request.Location;
        });
        _logger.LogInformation("Updated Discord scheduled event {DiscordEventId}", eventId);
    }

    public async Task DeleteEventAsync(string eventId, CancellationToken cancellationToken)
    {
        var guild = await GetGuildAsync(cancellationToken);
        var scheduledEvent = await guild.GetEventAsync(ulong.Parse(eventId));
        if (scheduledEvent is null)
        {
            _logger.LogInformation("Discord scheduled event {DiscordEventId} already gone — nothing to delete", eventId);
            return;
        }

        await scheduledEvent.DeleteAsync();
        _logger.LogInformation("Deleted Discord scheduled event {DiscordEventId}", eventId);
    }

    /// <summary>DateTimeOffset(DateTime, TimeSpan.Zero) requires Kind=Utc (or Unspecified); force it so a value that round-tripped through EF/Sqlite (which drops Kind) never throws.</summary>
    private static DateTimeOffset ToOffset(DateTime utc) =>
        new(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeSpan.Zero);

    private async Task<RestGuild> GetGuildAsync(CancellationToken cancellationToken)
    {
        await EnsureLoggedInAsync(cancellationToken);
        return await _client.GetGuildAsync(_options.GuildId)
            ?? throw new InvalidOperationException(
                $"Discord guild {_options.GuildId} was not found, or this bot is not a member of it. " +
                "Check Discord:GuildId and that the bot was actually invited via the OAuth2 URL Generator (bot scope + Manage Events permission) — see docs/zoom-discord-scheduling.md.");
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
            _logger.LogInformation("Logged into Discord as a bot for guild {GuildId}", _options.GuildId);
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
