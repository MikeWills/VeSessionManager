# Zoom + Discord Scheduling (Phase 2)

What `SessionEventSchedulingService` (`VeSessionManager.Core/Scheduling/`) relies on. Unlike
`docs/examtools-api.md`, these are official, documented APIs — this page just records the exact
shapes/gotchas this codebase depends on, with sources, so a future change doesn't need to
re-derive them.

## Zoom

Client: `VeSessionManager.Core/Zoom/ZoomClient.cs`. Hand-rolled `HttpClient` wrapper, not a NuGet
package — Zoom doesn't publish an official lightweight .NET SDK for this surface.

**Auth — Server-to-Server OAuth** ([docs](https://developers.zoom.us/docs/internal-apps/s2s-oauth/)):
- `POST https://zoom.us/oauth/token`, `Authorization: Basic base64(clientId:clientSecret)`,
  form body `grant_type=account_credentials&account_id={accountId}`.
- Response `access_token` expires in 3600s with **no refresh token** — re-request before expiry.
  `ZoomClient` caches the token and refreshes a minute early.
- Subsequent calls: `Authorization: Bearer {access_token}`.

**Meetings API** ([docs](https://developers.zoom.us/docs/api/meetings/)):
- Create: `POST https://api.zoom.us/v2/users/{userId}/meetings` — body `topic`, `type: 2`
  (scheduled), `start_time` (ISO 8601 UTC, e.g. `2026-07-24T17:00:00Z`), `duration` (minutes),
  `timezone: "UTC"`. Response includes `id` (numeric — stored as a string on `Session`) and
  `join_url`.
- Update: `PATCH https://api.zoom.us/v2/meetings/{meetingId}`, same body shape. 204 No Content.
- Delete/cancel: `DELETE https://api.zoom.us/v2/meetings/{meetingId}`. 204 No Content.
- `userId` is configurable (`Zoom:UserId`, default `"me"` — the account tied to the S2S app).

## Discord

Client: `VeSessionManager.Core/Discord/DiscordEventClient.cs`, wrapping `Discord.Net.Rest`
(`DiscordRestClient` — REST-only, no gateway connection, which a periodic background job doesn't
need). See [Discord.Net's guild scheduled events guide](https://docs.discordnet.dev/guides/guild_events/creating-guild-events.html).

- Login once per process: `DiscordRestClient.LoginAsync(TokenType.Bot, botToken)`. Bot tokens
  don't expire, unlike Zoom's — no refresh logic needed.
- Create: `IGuild.CreateEventAsync(name, startTime, GuildScheduledEventType.External, description:, endTime:, location:)`.
  The Zoom join URL goes in `location` (External-type events support link locations) — this is
  where the spec's "event description/location includes the Zoom join link" requirement is met.
- Update: fetch the event via `IGuild.GetEventAsync(ulong id)`, then
  `IGuildScheduledEvent.ModifyAsync(props => { props.Name = ...; props.StartTime = ...; props.EndTime = ...; props.Location = ...; })`.
  All `GuildScheduledEventsProperties` fields are `Optional<T>` with implicit conversion from `T`.
- Delete: `GetEventAsync` then `IGuildScheduledEvent.DeleteAsync()`. `GetEventAsync` returns
  `null` if the event is already gone (e.g. deleted manually in Discord) — treated as a no-op,
  not an error, so cleanup stays idempotent.
- `StartTime`/`EndTime` are `DateTimeOffset`, not `DateTime` — `DiscordEventClient.ToOffset()`
  forces `DateTimeKind.Utc` before constructing with a zero offset, since values round-tripped
  through EF/Sqlite come back with `Kind = Unspecified`, and `DateTimeOffset`'s Kind-vs-offset
  validation would otherwise throw for a naive `Local`-inferred conversion.
- Package: `Discord.Net.Rest` (chosen over `Discord.Net.WebSocket`, which requires a persistent
  gateway connection this job never needs).
