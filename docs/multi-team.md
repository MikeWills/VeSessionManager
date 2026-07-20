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

## What's still global (not yet multi-team)

This slice covers **only ExamTools** — the one hard-required integration. Still single-account,
shared across every team's sessions, exactly as before this change:

- Zoom (`IZoomClient`) / Discord (`IDiscordEventClient`) — `SessionEventSchedulingService`
- Square (`ISquareClient`) — `PaymentGenerationService`, plus the Web project's
  `POST /webhooks/square` route and `SquareWebhookHandler`
- Email/SMTP (`IEmailSender`) — `CandidateNotificationService`, `PaymentReminderService`

`FccUlsWatcherService` never needs this treatment — it already matches candidates across every
session/team in one pass, by design (FCC data has no concept of "which team").

**Fast-follow scope, when it happens:** apply the exact `ExamToolsClient` pattern above to each of
the four remaining clients; add the equivalent credential columns to `Team`; loop
`SessionEventSchedulingService`/`PaymentGenerationService`/`CandidateNotificationService`/
`PaymentReminderService` per-team the same way `SessionIngestionJob` now loops ingestion. The one
piece that needs genuinely new design, not just repetition: the Square webhook route. Square's
signature verification needs the right team's `WebhookSignatureKey` *before* the payload can even
be parsed to find which `Payment`/team it belongs to — the fix is almost certainly a per-team route
(e.g. `/webhooks/square/{teamSlug}`) so the URL itself identifies the team ahead of signature
verification, since `WebhookNotificationUrl` is already a required input to the HMAC anyway.

## Migration follow-up — required before ExamTools polling works again

The `Phase6_5MultiTeamFoundation` migration seeds one `Team` row (`Name`/`ExamToolsTeamCode` =
`"WX0MIK"`, the value already committed in `appsettings.json` pre-migration) but **cannot** carry
real credentials — migrations must never contain secrets, even ones already sitting in this repo's
user-secrets. `ExamToolsUsername`/`ExamToolsPassword` are left `NULL` on that seeded row.

**Real ExamTools polling stops working the moment this migration deploys**, until someone manually
sets those two columns on the seeded `Team` row via direct DB edit (same "hand-edit in the DB, no
admin UI yet" pattern as `EmailSettings`/`EmailTemplates`) — see `TODO.md`.
