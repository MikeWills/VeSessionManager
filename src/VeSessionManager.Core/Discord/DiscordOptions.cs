namespace VeSessionManager.Core.Discord;

public class DiscordOptions
{
    public const string SectionName = "Discord";

    // BotToken comes from user-secrets or environment variables, never from appsettings files.
    // Global/shared across every team by deliberate choice (confirmed with the user) — one bot
    // application is invited into each team's own Discord server; only which Guild it posts to
    // (Team.DiscordGuildId) varies per team. See docs/multi-team.md.
    public string BotToken { get; set; } = "";
}
