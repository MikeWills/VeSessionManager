namespace VeSessionManager.Core.Discord;

/// <summary>
/// The scheduled event this app holds an id for no longer exists in the guild — somebody deleted it
/// in Discord.
///
/// <para><b>A distinct type because it is the one Discord failure this app can fix by itself.</b> It
/// used to be an <c>InvalidOperationException</c> carrying an explanatory message, which the caller
/// could only log: the stored id stayed, every tick tried to update an event that was gone, and the
/// session never got another one. A recoverable state that reads as a permanent error, once per tick,
/// forever.</para>
///
/// <para>Recovery is to forget the id. The create path already lists the guild's events and adopts a
/// match before creating, so the next pass either finds one or makes a new one — no duplicate either
/// way. See <c>SessionEventSchedulingService.SyncZoomAndDiscordAsync</c>.</para>
///
/// <para>⚠️ Deleting is deliberately <b>not</b> a case of this: <c>DeleteEventAsync</c> treats an
/// already-gone event as success, because there the absence is the goal.</para>
/// </summary>
public class DiscordEventNotFoundException(ulong guildId, string eventId)
    : Exception($"Discord scheduled event {eventId} no longer exists in guild {guildId} (deleted outside the app?).")
{
    public ulong GuildId { get; } = guildId;

    public string EventId { get; } = eventId;
}
