namespace VeSessionManager.Core.Discord;

public class DiscordOptions
{
    public const string SectionName = "Discord";

    /// <summary>The guild (server) scheduled events are created in.</summary>
    public ulong GuildId { get; set; }

    // BotToken comes from user-secrets or environment variables, never from appsettings files.
    public string BotToken { get; set; } = "";
}
