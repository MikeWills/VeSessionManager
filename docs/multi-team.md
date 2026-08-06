# Multi-Team Foundation

What `Team` (`VeSessionManager.Core/Entities/Team.cs`) is, why it's shaped the way it is, and the
pattern any future per-team integration (Zoom/Discord/Square/Email) should follow.

## The hierarchy: VEC ⇒ Team ⇒ VE

A `Vec` is an FCC-recognized coordinating org (ARRL, W5YI, Laurel, etc.) — it dictates a session's
fee schedule and stays exactly what it's always been: a **shared, global reference table**. There
is one real-world "ARRL" row, not one per team. A `Team` is the group of VEs actually operating a
deployment of this app — it holds the ExamTools/Zoom/Discord/Square/Email credentials, and (per
Phase 7) individual `VolunteerExaminer`s belong to it.

**`Vec` is deliberately not owned by `Team`.** An earlier draft of this design proposed
`Vec.TeamId` to stop two teams' identically-named VEC rows from colliding — that was the wrong fix.
Since `Vec` is genuinely shared, there's only ever one "ARRL" row for the whole app regardless of
team count, so the ingestion service's existing unscoped Vec-by-name lookup already resolves
correctly for multi-team with **no change**: it naturally finds the one shared Vec every team's
ARRL-coordinated sessions should use. A VEC dictates fees *universally*, not per-team-negotiated,
so sharing `FeeConfiguration` this way is correct, not an oversight.

`Session` is the join point between the two: it already had `VecId` (which fee schedule applies);
it now also has `TeamId` (which team operationally ran it) as an independent second FK. No
relationship exists between `Vec` and `Team` themselves — a team can work with multiple VECs across
different sessions, and the same VEC can be shared by multiple teams, entirely independently.

## Credential storage: plaintext in SQLite, by design

`Team.ExamToolsUsername`/`ExamToolsPassword`/etc. are plain columns, no encryption-at-rest. This
was a deliberate choice, not an oversight: it matches the trust boundary this app has always used
(today's user-secrets file is also unencrypted plaintext on disk), and it matches how
`EmailSettings` already stores its hand-edited fields. Adding an encryption subsystem would mean
key storage/rotation/loss-recovery to design and maintain — not worth it unless the DB file's real
exposure risk turns out to be materially higher than the current user-secrets file's.

## The per-team client pattern

Before this change, every external-API client (`ExamToolsClient`, `ZoomClient`,
`DiscordEventClient`, `SquareClient`) was a DI singleton holding **exactly one** cached
credential/token/cookie state, resolved once from `IOptions<T>` at container startup. That can't
serve two teams at once.

`ExamToolsClient` is the reference implementation for the fix — reuse this shape for the deferred
Zoom/Discord/Square/Email fast-follow:

- **Stays a single `AddSingleton`.** No keyed DI, no factory-per-team pattern.
- Public interface methods take a small credentials record (`ExamToolsCredentials(TeamId,
  TeamCode, Username, Password)`) instead of reading from injected options.
- Internally, the client holds a `ConcurrentDictionary<int, TeamSession>` keyed by `TeamId`, where
  `TeamSession` is whatever that integration needs cached per team (for ExamTools: its own
  `HttpClient`/`CookieContainer` + login-lock + logged-in flag). `GetOrAdd` on first use per team.
- `Dispose()` iterates every cached `TeamSession`, not just one.
- Anything that's genuinely environment-level rather than per-team (ExamTools' `BaseUrl` — which
  host to hit, same for every team on one deployment) **stays** in `IOptions<T>`/appsettings. Only
  the values that actually vary per team move onto `Team`.
- The calling service (not the client) decides whether a team is "configured enough" to call at
  all — `Team.IsExamToolsConfigured` is checked in `SessionIngestionService.RunAsync` before ever
  touching the client, mirroring the `IsConfigured`-gate convention already used for Zoom/Discord/
  Square/Email, just living on the entity now instead of a client-held options object.

## What's per-team now (fast-follow complete, 2026-07-20)

Every external integration except one is now fully per-team, following the exact `ExamToolsClient`
pattern above. Landed as four separate commits — Zoom, Discord, Square (+ webhook route), Email —
each with its own migration:

- **ExamTools** — `Team.ExamToolsTeamCode`/`ExamToolsUsername`/`ExamToolsPassword`.
  `SessionIngestionService`.
- **Zoom** — `Team.ZoomAccountId`/`ZoomClientId`/`ZoomClientSecret`/`ZoomUserId`. `ZoomClient`
  caches a `(AccessToken, ExpiresUtc, SemaphoreSlim)` per `TeamId` in a
  `ConcurrentDictionary<int, TeamZoomSession>` (Bearer-token auth needs no per-team `HttpClient`,
  unlike ExamTools' cookie-jar).
- **Discord — the one exception.** One Discord bot token is shared across every team
  (`Discord:BotToken`, stays in `IOptions<DiscordOptions>`/user-secrets, unchanged from before this
  fast-follow); only *which guild* the bot posts events into varies per team
  (`Team.DiscordGuildId`, non-secret, `null`/`0` = "not configured"). This was an explicit user
  decision, not the default pattern: Discord's bot model has no one-guild-per-token constraint, so
  one bot identity legitimately serving multiple teams' servers is a normal supported setup —
  unlike a Zoom/Square/Email account, which genuinely can't be shared. `DiscordEventClient` needs
  **no per-team cache at all** as a result — it keeps the single shared `DiscordRestClient`/
  `_loggedIn`/login-lock it always had; only the `guildId` passed into each call varies.
  `IDiscordEventClient.IsConfigured` stays client-level (bot-token readiness); a session's Discord
  attempt needs **both** `discordEventClient.IsConfigured && team.IsDiscordConfigured` true.
- **Square** — `Team.SquareAccessToken`/`SquareLocationId`/`SquareWebhookSignatureKey`/
  `SquareWebhookNotificationUrl`, and (since 2026-08-06) `SquareEnvironment` too. Sandbox-vs-Production
  was originally left behind in `appsettings.json` as "a whole-deployment choice"; that was wrong —
  a token authenticates against exactly one environment's host, so the global switch made
  "real team on Production, test team on Sandbox" impossible on one deployment. `SquareOptions` is
  gone; nothing Square-related remains in config. `PaymentGenerationService` per-team.
  **Webhook route changed to `/webhooks/square/{teamId}`** — the URL identifies the team *before*
  signature verification (which needs that team's own `WebhookSignatureKey`), exactly the design
  problem flagged as needing "genuinely new design" before this fast-follow started.
  `SquareWebhookHandler.ProcessAsync(teamId, ...)` looks up the `Team` by route id first; an
  unknown or webhook-unconfigured team returns `InvalidSignature` (same outcome either way — never
  leak whether a `teamId` is valid vs. just unconfigured); after matching a `Payment` by `order_id`
  (unchanged), a defense-in-depth check confirms that payment's actual `Session.TeamId` matches the
  route's `teamId`, returning `Ignored` (not `Processed`) on mismatch — catches a misconfigured
  `WebhookNotificationUrl` pointing at the wrong team before it marks the wrong team's payment paid.
- **Email/SMTP** — `Team.SmtpHost`/`SmtpPort`/`SmtpUsername`/`SmtpPassword`/`SmtpUseStartTls` (no
  baked-in default on any of them — see the CLAUDE.md gotcha about a shipped default making
  `IsConfigured` read true before setup). `EmailSettings` moved from a true singleton to one row
  per team (`TeamId` unique index); `EmailTemplate`'s unique index moved from `Key` alone to
  `(TeamId, Key)` — **template wording (Subject/Body) is now customizable per team**, not shared,
  per an explicit user decision (different teams may want different tone/branding, and email
  addresses differ per team regardless). `EmailDefaultsSeeder` loops every `Team` and seeds one
  `EmailSettings` row + the full template set per team, idempotent per row exactly as before.

`FccUlsWatcherService` never needed this treatment — it already matches candidates across every
session/team in one pass, by design (FCC data has no concept of "which team").

**Onboarding a second team** is now just inserting a new `Team` row (direct DB edit — no admin UI
yet) with its own ExamTools/Zoom/Square/Email credentials and Discord guild id. Every job (ingestion,
scheduling, payment generation, notifications, reminders) picks it up automatically on its next
poll — no restart, no separate backfill step. See `TODO.md`'s Multi-Team Foundation section for the
exact checklist.

## Migration follow-up — required before ExamTools polling works again

The `Phase6_5MultiTeamFoundation` migration seeds one `Team` row (`Name`/`ExamToolsTeamCode` =
`"WX0MIK"`, the value already committed in `appsettings.json` pre-migration) but **cannot** carry
real credentials — migrations must never contain secrets, even ones already sitting in this repo's
user-secrets. `ExamToolsUsername`/`ExamToolsPassword` are left `NULL` on that seeded row.

**Real ExamTools polling stops working the moment this migration deploys**, until someone manually
sets those two columns on the seeded `Team` row via direct DB edit (same "hand-edit in the DB, no
admin UI yet" pattern as `EmailSettings`/`EmailTemplates`) — see `TODO.md`.
