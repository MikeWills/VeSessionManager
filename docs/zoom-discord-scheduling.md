# Zoom + Discord Scheduling (Phase 2)

What `SessionEventSchedulingService` (`VeSessionManager.Core/Scheduling/`) relies on. Unlike
`docs/examtools-api.md`, these are official, documented APIs — this page just records the exact
shapes/gotchas this codebase depends on, with sources, so a future change doesn't need to
re-derive them.

## Account Setup (one-time, before the four secrets in the README mean anything)

Neither of these can be done from the repo or by me — they're account-level setup in Zoom's and
Discord's own dashboards, done once by whoever owns the accounts.

### Zoom: create the Server-to-Server OAuth app

1. Sign into the [Zoom App Marketplace](https://marketplace.zoom.us/) with an account that has
   developer permissions (needs to be an account **admin**, not just a regular user — S2S OAuth
   scopes require admin-level "User and Permission Management" access).
2. **Developer** (lower-left) → **Build an app** → choose **Server-to-Server OAuth** → **Create**.
3. Name it (e.g. "VE Session Manager"), fill in the required basic info/company/contact fields.
4. **Scopes** tab → **Add Scopes** → search "meeting" and add the create/update/delete/read
   scopes for meetings. Zoom's scope naming has shifted over time and varies by account; look for
   entries like `meeting:write:meeting:admin`, `meeting:update:meeting:admin`,
   `meeting:delete:meeting:admin` (older accounts may instead show a single coarser
   `meeting:write:admin`). The `:admin` variants are the ones you want — Server-to-Server apps
   act at the account level, so even managing meetings only under your own user still needs the
   admin-scoped versions ([confirmed on the Zoom developer forum](https://devforum.zoom.us/t/server-to-server-oauth-app-permissions-and-scopes/92331)).
5. **Activation** tab → activate the app. Per Zoom's docs, [you cannot generate an access token
   at all until the app is activated](https://developers.zoom.us/docs/internal-apps/create/) —
   easy step to miss and get a confusing auth failure from.
6. Back on the app's overview, the **App Credentials** section shows **Account ID**, **Client
   ID**, and **Client Secret** — these are the three `Zoom:*` secrets in the README.

### Discord: create the bot and invite it to your server

1. Go to the [Discord Developer Portal](https://discord.com/developers/applications), sign in,
   **New Application**, give it a name (e.g. "VE Session Manager").
2. Left sidebar → **Bot**. A bot user is created automatically with the application. Click
   **Reset Token** to reveal it (2FA confirmation if you have it enabled) — this is `Discord:BotToken`.
   Treat it like a password; Discord will silently invalidate it if it ever leaks into a public repo.
3. Left sidebar → **OAuth2** → **URL Generator**. Under **Scopes**, check **bot**. Under the
   **Bot Permissions** that appears, check **Manage Events** (this is what allows creating,
   modifying, and deleting guild scheduled events — no other permissions or privileged gateway
   intents are needed, since `DiscordEventClient` only ever makes REST calls).
4. Copy the generated URL, open it in a browser, pick your Discord server, **Authorize**.
5. **Guild ID** (`Discord:GuildId`): in the Discord app, enable **User Settings → Advanced →
   Developer Mode**, then right-click your server's icon → **Copy Server ID**.

Once both are done, set the four secrets/one config value per the README's "Configuration &
Secrets" section and the Worker's next poll cycle will pick up any session still needing a sync.

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

### Breakout rooms (2026-07-28)

`settings.breakout_room` on the Create/Update Meeting payload — `{ enable: true, rooms: [{name, participants}] }`.
Multiple 2022-2024 Zoom devforum threads (see the "Zoom meeting templates" TODO's neighboring
investigation) claimed the Create Meeting API silently ignores this block regardless of a correctly
formatted payload. **Live-tested against this account 2026-07-28, and it isn't true here** — the
block persists exactly as sent, confirmed by opening the real meeting's "Breakout Room Assignment"
dialog in the Zoom client and seeing the named rooms actually there. Whatever caused those old
reports either got fixed since, was account/plan-specific, or needed some other precondition —
either way, don't take a 2-4-year-old forum thread as gospel without testing against the real
account first, same lesson as everywhere else "verified live" shows up in this app's docs.

`Team.ZoomBreakoutRoomCount` (admin-editable on `/Admin/TeamSettings`, default 2) is a flat per-team
count — every session's meeting gets that many empty rooms named "Exam Room 1", "Exam Room 2", etc.,
with no participants pre-assigned (VEs move candidates in manually once the meeting starts). Chosen
over auto-deriving a count from something like the session's credited-VE count because this app
doesn't track "parallel testing stations" as a concept, just individual VEs — a per-team explicit
setting is honest about that rather than guessing. 0 means don't request any breakout rooms at all
(`ZoomMeetingRequest.BreakoutRoomCount <= 0` omits the `settings.breakout_room` block from the
request entirely, not `enable: false`).

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

## An event deleted in Discord is recovered from, not retried forever (2026-08-21)

Found in the Worker log on Mike's own deployment, not by a test:

```
[ERR] Failed to sync Zoom/Discord for session 6950a55a887662328eb6a169
System.InvalidOperationException: Discord scheduled event 1532165026406731887 no longer exists
```

`UpdateEventAsync` threw, the caller logged it, and **`Session.DiscordEventId` stayed set** — so every
tick tried to update an event that was gone, failed the same way, and the session never got another
one. A recoverable state that read as a permanent error, once per tick, forever.

It also contradicted the query-before-create pattern the rest of this file is about: a resource that
disappeared should be re-created, not retried against a dead id.

**The recovery is to forget the id.** `discordSettled` is `DiscordEventId is not null`, so clearing it
leaves the session unsettled and the ordinary create branch picks it up on the next pass — and that
branch lists the guild first, so an event somebody recreated by hand in the meantime is adopted
rather than duplicated. No new state, no retry counter.

⚠️ **Only a missing event is swallowed**, via the dedicated `DiscordEventNotFoundException`. A
permission problem, a bad token or a guild the bot was removed from still surfaces as a failure —
forgetting the id on any of those would create a second event the moment access came back. That is
what the typed exception buys over the `InvalidOperationException` with an explanatory message it
replaced: the caller can tell the two apart.

`DeleteEventAsync` is deliberately not part of this — it already treats an already-gone event as
success, because there the absence is the goal.
