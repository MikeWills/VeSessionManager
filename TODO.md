# TODO

Outstanding testing/configuration items for what's already built (Phases 0–6 of
[`docs/spec.md`](docs/spec.md)). This tracks operational follow-ups, not the phase roadmap itself
— see `docs/spec.md` for what's planned but not yet built.

Reminder: Square, Zoom, Discord, and Email/SMTP are all **optional integrations** — the app runs
fine with any subset of these unconfigured (one quiet log line per poll, no errors), so none of
the items below are blocking further phase work. They're blocking *live end-to-end verification*
of Phases 2–4's actual deliverables.

## Square (Phase 3) — not yet live-verified

- [x] ~~Create the Square app + get sandbox credentials~~ — see `docs/square-payments.md`'s Account Setup section
- [x] ~~**Updated by multi-team (see below): these now go on the seeded `Team` row (direct DB edit), not `Square:*` appsettings/user-secrets** — set `Team.SquareAccessToken`/`SquareWebhookSignatureKey`/`SquareLocationId`/`SquareWebhookNotificationUrl`.~~ Done for Team 2 (MARC), the real production team.
- [x] ~~`SquareWebhookNotificationUrl` must be the *team-specific* URL~~ — Team 2 uses `https://<host>/webhooks/square/2`.
- [x] ~~For local testing, tunnel the Web project's webhook endpoint to a public HTTPS URL~~ (ngrok) ~~and register that URL as the Square webhook subscription's notification URL~~ — done via `ngrok http http://localhost:5158` (note: **http**, not https — the local dev launch profile serves plain HTTP on 5158; ngrok terminates the public HTTPS side itself).
- [x] ~~Live test: let the Worker generate a real payment link for a test candidate, pay it with a Square sandbox test card, confirm the webhook flips `Payment.Status` to `Paid`~~ (2026-07-25) — full applicant-through-payment flow verified end to end for Team 2: ExamTools registration → ingestion → Zoom/Discord event creation → Square payment link generation → sandbox checkout → webhook → `Payment.Status = Paid`, all webhook deliveries returned 200. **Found live: Square webhook subscriptions are separate per Sandbox/Production, each with its own signature key** — an existing subscription registered under Production doesn't receive Sandbox events at all (0 delivery attempts), and reusing the Production signature key against a newly-added Sandbox subscription's events causes every delivery to 401 (signature mismatch) even though the URL/event config is otherwise correct. Fixed by adding a separate Sandbox-side subscription and setting `Team.SquareWebhookSignatureKey` to *its* key, not the Production one. Also found: the same Zoom Server-to-Server scope gap as the meeting-templates TODO item (`meeting:read:list_meetings`/`meeting:read:list_meetings:admin`) blocks `SessionEventSchedulingService`'s dedup check too, not just template listing — fixed same way (add both scopes in the Zoom App Marketplace).
- [ ] Live test the post-launch unmatched-payment-matching feature (`docs/square-payments.md`'s "Unmatched payments"/"Order completion" sections): pay via a separate Square-hosted page (not one of this app's own generated links) with a buyer email matching exactly one candidate's outstanding Unpaid payment, confirm auto-match; repeat with no matching candidate and confirm it shows up on `/SessionManager/UnmatchedPayments` for manual matching; confirm a Paid order's Square Order actually flips to `COMPLETED` in the dashboard once its session is marked completed.

## Email/SMTP (Phase 4) — not yet live-verified

- [ ] Get Mailgun's domain-specific SMTP username/password (Mailgun dashboard → Sending → Domain settings → SMTP credentials) — see `docs/email-notifications.md`
- [ ] **Updated by multi-team (see below): these now go on the seeded `Team` row (direct DB edit), not `Email:*` user-secrets** — set `Team.SmtpHost`/`SmtpPort`/`SmtpUsername`/`SmtpPassword`/`SmtpUseStartTls`. No column has a baked-in default (deliberate — see CLAUDE.md's `IsConfigured` gotcha), so all five need setting even to match Mailgun's usual `smtp.mailgun.org:587`+STARTTLS defaults.
- [ ] Replace the seeded `EmailSettings` row's placeholder values (`FromAddress`/`FromDisplayName`/`ReplyToAddress`/`PrivacyPolicyUrl` are currently `noreply@example.org` / `https://example.org/privacy`) with real values for **each team's own `EmailSettings` row** (one per team now, not a singleton) — edit directly in the DB, see `docs/email-notifications.md`. **Partially done (checked 2026-07-29, corrected same day — a code review caught this was incomplete):** Team 2 (MARC) has a real `FromAddress`, but `PrivacyPolicyUrl` is still the literal `https://example.org/privacy` placeholder for **all three teams, including MARC** — not just the two still-placeholder teams for `FromAddress`.
- [ ] Review/rewrite the seeded `RegistrationConfirmation`/`DayBeforeReminder` template content — it's a real starting example (bullet points, `{{CandidateFirstName}}`, etc.) but the actual wording is a placeholder, not final copy. Templates are now per-team (`EmailTemplate.TeamId`) so each team can have its own wording if desired.
- [ ] Live test: confirm a test candidate actually receives both emails with correctly substituted placeholders

## FCC ULS Watcher (Phase 5) — not yet live-verified

- [x] ~~Live test: find (or wait for) a real candidate whose FRN appears in an actual FCC daily application file and confirm `FccDailyWatcherJob` flips them to `Received` with a sane `ApplicationDateEnteredUtc`~~ (confirmed 2026-07-29 — found already done while auditing this list against real DB state, not a new test) — 9 real HRCC candidates currently sit in `Received`.
- [x] ~~Live test: confirm the same candidate's eventual license grant flips them to `Granted` with the correct `CallSign`/`LicenseGrantDateUtc`~~ (confirmed 2026-07-29, same audit; corrected 2026-07-29 — a code review caught the original count was wrong) — 5 real HRCC candidates currently sit in `Granted` (William Denney and Jason Pelowitz each twice across two sessions, plus Richard J Sawyer once), including the upgrade-exam matches already documented in the "Upgrade exam handling" fix above. A 6th `Granted` candidate (Sarah Nguyen) belongs to Team 1 (WX0MIK), not HRCC — the original note wrongly attributed the whole unscoped total to HRCC.
- [x] ~~Let `FccWeeklyCatchupJob` actually run on a real Monday at least once and confirm it hits `complete/a_amat.zip`/`complete/l_amat.zip` successfully~~ (confirmed live 2026-07-30 — see CLAUDE.md's Change Log entry). First real attempt (Monday 07-27) hit a `403 Forbidden`; turned out to be transient (identical request succeeds on retry), but `FccWeeklyCatchupJob` had no retry logic at all, so the whole safety net silently stayed dark for a week. Fixed and verified live: a manual trigger successfully downloaded and processed both ~190MB files (~4 minutes) and cleared most of a real HRCC backlog (40 Unmatched/8 Received down to 3 Unmatched/50 Granted).
- [x] ~~Revisit the deferred "upgrade exam" (existing licensee) matching logic~~ (resolved 2026-07-28 with real HRCC data — William Denney/Jason Pelowitz — see `docs/fcc-uls-watcher.md`'s "Upgrade exam handling" section). Matching still can't avoid re-detecting a pre-existing license (FCC's own Grant Date doesn't change on a class upgrade), but the real consequence — premature PII purge — is fixed by anchoring `PiiPurgeService`'s retention Trigger A on the later of `LicenseGrantDateUtc`/`Session.ScheduledStartUtc`, and the distinction is surfaced on the new applicant detail page.
  - **Found a second, more severe consequence of the same gap (2026-07-30):** `ProcessLicensesAsync` itself had no guard at all — three real same-day upgrade candidates (Erik Nielsen, Katelynn Schneider, Zachary Coffey) were incorrectly flipped straight to `Granted` off their old pre-existing license record, not today's actual exam result, the moment the weekly-catchup retry fix above started actually running reliably. Fixed by gating the license match on Grant Date being on/after `Session.ScheduledStartUtc` (same rule as the application-file match); the three affected candidates were manually reverted to `Unmatched`. **Still genuinely unsolved:** an upgrade candidate now simply never auto-confirms as `Granted` via this pipeline (FCC's Grant Date doesn't move on an upgrade, and this app doesn't fetch the AM.dat operator-class field that would) — there's no positive ULS signal today to detect "the upgrade actually went through." Would need either AM.dat parsing or a manual confirmation UI to close.

## Payment Reminders (Phase 6) — not yet live-verified

- [ ] Replace `EmailSettings.AdminNotificationEmail`'s seeded placeholder (`admin@example.org`) with a real inbox — this is where every `PaymentExpirationNotice` goes, so it silently goes nowhere useful until changed. **Partially done (checked 2026-07-29):** Teams 1 (WX0MIK) and 2 (MARC) already have a real inbox set; Team 3 (HRCC) is still the literal placeholder.
- [ ] Review/rewrite the seeded `PaymentReminder5Day`/`PaymentExpirationNotice` template content — same "real starting example, not final copy" caveat as Phase 4's templates
- [ ] Live test: let a real candidate's Unpaid payment age past 5 days (`Received` status, `ApplicationDateEnteredUtc` from Phase 5) and confirm the reminder actually sends with correct placeholders
- [ ] Live test: let a real candidate's Unpaid payment age past 10 days and confirm `Payment.ExpiredUnpaid` flips and the admin notice arrives at the configured `AdminNotificationEmail`
- [ ] Decide whether `PaymentReminder:UnmatchedReviewWindowDays` (default 5) is the right value once real sessions are running through Phase 1/5 — the spec calls this "some reasonable window," not a fixed number

## Admin Backend Auth (Phase 9a) — not yet live-tested with real accounts

- [ ] Create a Google OAuth app + set `Authentication:Google:ClientId`/`ClientSecret` (user-secrets locally, `Authentication__Google__ClientId`/`Authentication__Google__ClientSecret` env vars in prod) — see `docs/admin-auth.md`
- [ ] Create a Microsoft/Entra app registration + set `Authentication:Microsoft:ClientId`/`ClientSecret` the same way
- [ ] Live test: sign in with a real Google account and a real Microsoft account once credentials are set, confirm the account-linking flow (matches by email to an existing seeded/admin-created `User` row — no self-service registration) works end to end
- [ ] The four dev test users (`sysadmin`/`teamadmin`/`sessionmanager`/`teamlead@example.com`) only exist in Development via `DevAuthSeeder` — Production needs real `User` rows created by hand (direct DB edit, no admin UI yet) until Phase 9c ships user management
- [x] ~~**Surface `Payment.PaymentLinkUrl` somewhere in the Session Manager UI**~~ (closed 2026-07-28, see `docs/applicant-detail.md`) — done as part of the new applicant detail page, which shows every payment (not just the primary one) with its link.
- [x] ~~**Show `Team.Id` on the admin Team list and Team settings pages**~~ (closed 2026-07-28) — `Pages/Admin/Teams.cshtml` now has an ID column; `Pages/Admin/TeamSettings.cshtml` shows it in a note above the credential forms (with the Square webhook URL as a worked example), so a human no longer has to query the DB directly to construct a team-specific URL.
- [ ] **Create the first production SystemAdmin user** — `DevAuthSeeder` never runs outside Development, and there's no admin UI yet to create the very first account, so prod's `AspNetUsers` table starts empty. Needs a one-off script/direct DB insert with a real `PasswordHash` (`PasswordHasher<User>`) or by linking a real Google/Microsoft account's email to a hand-inserted `User` row (`Role = SystemAdmin`, `TeamId = null`) via `ExternalLoginCallback`'s existing email-match logic. Blocking: nobody can sign into prod at all until this exists.
- [x] ~~Decide whether Apple Sign-In is ever worth its $99/year Developer account cost~~ — deliberately deferred in Phase 9a, decided 2026-07-22: **not worth it, skip it.** Username/password + Google + Microsoft is the final sign-in set; see `docs/admin-auth.md`.
- [x] ~~Review the password policy set in `Program.cs`~~ (closed 2026-07-28) — `RequiredLength` bumped 10 → 12 per NIST 800-63B (length matters more than composition rules); `RequireDigit`/`RequireLowercase`/`RequireUppercase` left at Identity's own `true` defaults as extra friction on top since these are admin/VE accounts, not public self-service ones. `RequireNonAlphanumeric` stays `false`, unchanged.

## Multi-Team Foundation — consolidated per-team setup checklist

All four fast-follow stages (Zoom, Discord, Square, Email) are done — every integration except
Discord's bot token is now fully per-team; see `docs/multi-team.md`. **Every credential column
added across all five migrations (ExamTools + the four fast-follow stages) is left `NULL` on the
seeded `Team` row** (migrations must never contain real secrets, even ones already sitting in this
repo's user-secrets) — each integration is silently skipped (one quiet log line per poll, no error)
until its columns are set via direct DB edit (no admin UI yet):

- [x] ~~**Blocking:** `Team.ExamToolsUsername`/`ExamToolsPassword`~~ (confirmed done 2026-07-29,
  fully resolved same day) — ExamTools ingestion is the one hard dependency everything else needs.
  "Credentials set" turned out not to mean "credentials work": live-testing the actual login later
  the same day found Team 1 (WX0MIK) and Team 2 (MARC) both failing with ExamTools' own
  `"Username/password not found."` error every `SessionIngestion` tick (confirmed both from
  `JobRunHistories` and by posting directly to `/api/ve/login` with exactly what was stored), while
  Team 3 (HRCC) logged in fine. Root cause was simply stale/incorrect stored passwords, not a code
  bug. Resolved once the user re-saved both teams' credentials through the Team Settings UI — all
  three teams (WX0MIK, MARC, HRCC) now log in successfully, re-verified live the same way.
- [ ] `Team.ZoomAccountId`/`ZoomClientId`/`ZoomClientSecret` (`ZoomUserId` is pre-filled `"me"`)
- [ ] `Team.DiscordGuildId` is pre-filled with the real MARC server id (`1323140214008578111`) —
  only `Discord:BotToken` (still shared/global, user-secrets) needs setting if not already done
  from before the fast-follow.
- [ ] `Team.SquareAccessToken`/`SquareWebhookSignatureKey`/`SquareLocationId`/
  `SquareWebhookNotificationUrl` — `SquareWebhookNotificationUrl` must be the *team-specific* URL:
  `https://<host>/webhooks/square/1` for the seeded team (route changed from `/webhooks/square` to
  `/webhooks/square/{teamId}`).
- [ ] `Team.SmtpHost`/`SmtpPort`/`SmtpUsername`/`SmtpPassword`/`SmtpUseStartTls` — no baked-in
  default on any of these (deliberate, see CLAUDE.md's `IsConfigured` gotcha).
- [ ] Rename the seeded `Team.Name` (currently `"WX0MIK"`, copied from the old `ExamTools:Team`
  appsettings value as a placeholder) to something more human-readable if desired — purely
  cosmetic, `ExamToolsTeamCode` is the value that actually matters functionally.
- [x] ~~**Found while cleaning up appsettings.Production.json**: the seeded team's
  `ExamToolsTeamCode` was copied from the *dev* value (`WX0MIK`)~~ (stale as of 2026-07-29 — a code
  review flagged this bullet as no longer matching reality). This was originally written assuming a
  single "the seeded team" would eventually be repointed at prod by changing its
  `ExamToolsTeamCode` from `WX0MIK` to `HRCC`. That's not what happened: Team 3 (`HRCC`) is now its
  own separate, fully-configured `Team` row (`ExamToolsTeamCode='HRCC'`, base URL
  `alpha.exam.tools`), distinct from Team 1 (`WX0MIK`, `examtools.dev`) — real production polling
  already happens via Team 3, not by renaming Team 1.
- [x] ~~Onboard the second team~~ (done — there are now 3 real `Team` rows: WX0MIK, MARC, HRCC).
- [x] ~~Live test: with two real `Team` rows configured, confirm every per-team job loop correctly
  isolates each team's data~~ (confirmed 2026-07-29 across many live Worker ticks this session, now
  with 3 teams not just 2) — ingestion, VE roster sync, and the new exam-result sync all correctly
  ran independently per team (WX0MIK/MARC/HRCC) tick after tick with no cross-team data mixing
  observed. Square webhook routing and per-team SMTP are still only partially exercised (only MARC
  has Square configured; no team has SMTP configured yet — see the Email/SMTP section above), so
  treat those two pieces specifically as still unverified live, not this whole item.

## GitHub Issues — feature requests / questions (not yet triaged, added 2026-07-28)

- [x] ~~**Pull past sessions, up to a month old**~~ (issue [#22](https://github.com/MikeWills/VeSessionManager/issues/22), closed 2026-07-28, see `docs/examtools-api.md`'s "Completed-session backfill" section). `SessionIngestionService` now also first-ingests a `"done"` session up to ~30 days past its start (previously never ingested at all) — the existing 1-day `"pend"` grace window is untouched. New `Session.HasEnded` helper keeps `SessionEventSchedulingService` from live-scheduling Zoom/Discord for a backfilled session and keeps `CandidateNotificationService`'s automatic scan from sending a "you're registered!" email for one — payment-link generation and VE roster sync are deliberately left running as normal.
- [x] ~~**Add filter for sessions so you can view only one team at a time**~~ (issue [#17](https://github.com/MikeWills/VeSessionManager/issues/17), `enhancement`, closed 2026-07-28) and ~~**Support a Session Manager belonging to multiple teams**~~ (issue [#19](https://github.com/MikeWills/VeSessionManager/issues/19), closed 2026-07-28) — done together, see `docs/admin-auth.md`'s "Team scoping" section. Replaced the single, nullable `User.TeamId` with a real many-to-many `UserTeam` join table; the session list now shows a Team column and a `?teamId=` filter-pill (reusing the existing SystemAdmin team-picker convention, extended to work for a multi-team TeamAdmin/SessionManager too); `SessionAccessScope`/`AdminAccessScope` moved from scalar equality to set-membership throughout, with explicit cross-team-leak regression tests per role.
- [x] ~~**Move ExamTools config URL to the web/admin side**~~ (issue [#18](https://github.com/MikeWills/VeSessionManager/issues/18), closed 2026-07-28, see `docs/examtools-api.md`'s "Per-team host override" section). Went with the free-text `Team.ExamToolsBaseUrl` nullable override column (not the alternative `Team.ExamToolsEnvironment` enum design) since it matches the existing per-team-credential-column pattern and needs no code change if a third ExamTools host ever shows up. Editable on the admin Team Settings page; blank clears back to the global `ExamTools:BaseUrl` default.
- [x] ~~**Clarify/reconsider the "hello world" job**~~ (issue [#20](https://github.com/MikeWills/VeSessionManager/issues/20), closed 2026-07-28). It was leftover Phase 0 scaffolding — logged "Hello, world!" and wrote a `JobRunHistory` row every 60 seconds, forever, with no real function; the pattern it demonstrated (`JobRunHistoryLogger` + `PeriodicTimer`) is now used by 7 real jobs. Removed `HelloWorldJob.cs`, its `AddHostedService<HelloWorldJob>()` registration, and the `Jobs:HelloWorldIntervalSeconds` appsettings key.
- [x] ~~**Surface which part of the pipeline is currently running in the Worker log**~~ (issue [#21](https://github.com/MikeWills/VeSessionManager/issues/21), closed 2026-07-28) — `JobRunHistoryLogger.RunAsync` (the one chokepoint every job step in every job file already calls) now logs a "Starting job: X" line before the step runs and a "Finished job: X (Nms)" line after, so every job/phase across every job file got this for free with a single change — no per-job-file edits needed.

## Bugs / known issues

- [ ] **`SessionEventScheduling` repeats a real `[ERR]` every tick, forever, when a cancelled
  session's stale Zoom meeting can't be cleaned up because the team's Zoom credentials aren't set**
  (found 2026-07-29 live-reviewing the Worker log — `worker-20260729.log`). Team 1 (WX0MIK) has a
  cancelled session (`6a5d8773c95bc8b311994c76`, tied to `manual-test-session-1` test data) that
  still has a `ZoomMeetingId` set, but `Team.ZoomAccountId`/`ZoomClientId`/`ZoomClientSecret` were
  never configured for that team (tracked separately in the Multi-Team checklist above).
  `SessionEventSchedulingService.CleanupZoomAndDiscordAsync` (`src/VeSessionManager.Core/Scheduling/SessionEventSchedulingService.cs`)
  deliberately does *not* gate cleanup on `IsConfigured` — the code comment explains this is
  intentional ("an existing event needs tearing down even if the team's Zoom setup changed since it
  was created") and throws a clear `InvalidOperationException` instead of a confusing null-credential
  call. That reasoning is fine for a one-off, but there's no backoff/dedup on it, so it re-throws and
  re-logs a full `[ERR]` on every single tick indefinitely (12 times in one day locally) — cuts
  against this app's own established "one quiet log line, never a repeating ERROR" pattern for
  every other unconfigured-integration case (see CLAUDE.md's Optional-integration pattern). Options
  worth weighing: log once and stop retrying until credentials are set (matches the existing
  pattern, but risks silently never cleaning up a real meeting if credentials do get added later);
  or keep retrying but only escalate to `[ERR]` once, `[WRN]`/throttled after that. Low real-world
  urgency right now since this is dev/test data on Team 1, not real HRCC/MARC data, but worth fixing
  before Team 1 (or any team) is ever a real production team with this same gap.

- [x] ~~**Completed-session backfill (issue #22) never actually ingested anything against real data**~~
  (found 2026-07-28 running the Worker against real HRCC ExamTools data as a live end-to-end test;
  fixed same day, see `docs/examtools-api.md`'s "Closed sessions are a separate feed" section).
  Ingestion ran clean with zero errors but a real session from the night before never showed up —
  `GetTeamSessionsAsync` (the only feed `SessionIngestionService` read from) turns out to **never**
  return a closed (`"done"`) session in real data, no matter its age; confirmed live against 40 real
  HRCC sessions back to 2024, all `"pend"`. Closed sessions only exist behind a separate date-range
  endpoint (`GET /api/veUser/sessions/{start}/{end}?group=all&team={teamId}`), discovered by
  comparing the browser UI's own session list (which correctly showed recent closed sessions) against
  the raw API response (which had a multi-month gap exactly where those sessions should have been).
  Fixed by adding `IExamToolsClient.GetTeamClosedSessionsAsync` and merging its results into
  `SessionIngestionService`'s new-session feed, deduped by `_id` against the pend list. Also confirmed
  live: `alpha.exam.tools` (not `exam.tools`) is the real production ExamTools host.

- [x] ~~**Duplicate Discord scheduled events**~~ — found ~6 duplicate events in the Discord server (reported 2026-07-21). Root cause and code fix landed 2026-07-21: `IDiscordEventClient` gained `ListEventsAsync`; `SessionEventSchedulingService.SyncZoomAndDiscordAsync` now checks for an existing guild event matching the session by name + start time (within a minute) before calling `CreateEventAsync`, adopting its id instead of creating a duplicate if found — covered by `NewSession_MatchingEventAlreadyExistsInGuild_AdoptsIt_DoesNotCreateDuplicate` in `SessionEventSchedulingServiceTests`. **Manual cleanup done (2026-07-28):** the ~6 pre-existing duplicate events in the real Discord server were deleted by hand — this fix only ever prevented new duplicates going forward, it never touched past ones itself.
  - **Same-day follow-up self-audit (2026-07-21) found the identical unfixed bug class in two more places, both now fixed:** Zoom meeting creation (same `SyncZoomAndDiscordAsync` method — added `IZoomClient.ListMeetingsAsync`, same name/time dedup pattern, see `NewSession_MatchingMeetingAlreadyExistsInZoom_AdoptsIt_DoesNotCreateDuplicate`) and Square payment link generation (`PaymentGenerationService.GenerateLinkAsync` — added `Payment.SquareIdempotencyKey`, persisted before calling Square and reused on retry so Square's own idempotency guarantee prevents the duplicate, migration `Phase9dPaymentSquareIdempotencyKey`). No live duplicates found for either yet (unlike Discord's confirmed ~6) — these were caught proactively, not from a reported incident.

- [x] ~~**Remove "Add walk-in candidate" — redundant with ExamTools**~~ (reported 2026-07-21, removed 2026-07-21). Walk-in registration is already handled by ExamTools itself, so Phase 9b's own walk-in action was unnecessary — a walk-in registered directly in ExamTools already flows in through the normal `SessionIngestionService` polling, same as any other candidate. Removed `CandidateActionService.AddWalkInAsync`, its `OnPostAddWalkInAsync` handler and modal/button in `Pages/SessionManager/Detail.cshtml(.cs)`, its test coverage in `CandidateActionServiceTests`, and the corresponding spec.md Session Manager bullet-list entry.

- [x] ~~**Remove "Move candidate to a different session" — redundant with ExamTools**~~ (reported 2026-07-21, removed 2026-07-21). Same reasoning as the walk-in item above: moving a candidate between sessions is already handled in ExamTools itself, so a move made there is already reflected the next time `SessionIngestionService` polls. Removed `CandidateActionService.MoveAsync`/`CandidateMoveResult`, its `OnPostMoveAsync` handler, the `CanMove`/`MoveTargetSessions` UI plumbing and modal/menu-item in `Pages/SessionManager/Detail.cshtml(.cs)`, its test coverage in `CandidateActionServiceTests`, and the corresponding spec.md Session Manager bullet-list entry.

- [x] ~~**TeamLead has no real view yet**~~ (found during Phase 9d's self-audit against 9a-9c, 2026-07-21; fixed 2026-07-22). Added `TeamLead` to `[Authorize]` on `Pages/SessionManager/Index.cshtml.cs`/`Detail.cshtml.cs`/`VeRoster.cshtml.cs`/`VecSubmission.cshtml.cs`, added a new `SessionAccessScope.CanView` (view-only, unlike `CanEdit` doesn't carve out TeamLead) to gate page *display* separately from the existing `CanEdit` write-gate, and hid every write control (buttons/forms/kebab menu/modals) in `Detail.cshtml`/`VecSubmission.cshtml` behind a `CanEdit` flag exposed from the page model. `RoleLandingPages` now sends TeamLead to `/SessionManager/Index` like every other role; the `Pages/TeamLead/Index.cshtml` placeholder was removed. **Found and fixed a second, previously-latent bug in the same change:** `SessionAccessScope.GetEffectiveTeamId`'s TeamLead branch needs `User.ManagedByUser` eager-loaded, but `UserManager.GetUserAsync(ClaimsPrincipal)` (used everywhere) never loads it — since no page had ever exercised the TeamLead path before, this had never been caught; a TeamLead would have signed in successfully and silently seen zero sessions. Fixed with a new `CurrentUserLoader.GetUserWithManagerAsync` extension (`Web/CurrentUserLoader.cs`) that all four pages now use instead of the bare `userManager.GetUserAsync`. Live-verified in a real browser: `teamlead@example.com` lands on Sessions, sees only their assigned team's sessions/roster/submission status with zero write controls anywhere; `sessionmanager@example.com` confirmed unaffected (full edit access still present) as a regression check.

- [x] ~~**No real fix yet for youth-rate underpayments getting auto-matched**~~ (found during a security review of the Square unmatched-payment-matching feature, 2026-07-21/22; built 2026-07-27, see `docs/youth-payment-confirmation.md`). Built as designed below, with two changes from the researched plan: the COPPA checkbox was simplified out entirely (just plain informational text/link now, no checkbox, nothing submitted or stored — not even a timestamp) and `Candidate.CoppaFormConfirmedUtc` was never added. A candidate now gets a second, youth-specific link in the registration confirmation email; confirming self-identifies them as a youth, deletes their standard-rate Square link, generates a new one at `FeeConfiguration.YouthExamFeeAmount`, and redirects straight to the new checkout. **Still an accepted risk, not fixed by this:** the separate Square-hosted checkout page (the other, unrelated path a candidate could use to underpay) was not retired — `AmountMismatchFlaggedUtc` remains the backstop for that path, per the original design note.
  - Design researched 2026-07-23, built 2026-07-27 — full rationale, data model, and known limitations in `docs/youth-payment-confirmation.md`.

- [x] ~~**Home page (`/`) had no styling or navigation**~~ (reported 2026-07-22, fixed and live-verified same day). `Pages/Index.cshtml` was still the untouched scaffold-default Bootstrap page — no app styling, no working navigation — since nothing had ever restyled it (the same class of gap Phase 9's polish pass already fixed for `Login`/`AccessDenied`/`Logout`/`ExternalLoginCallback`). Now uses `_PublicLayout` (matches Login/Privacy) with a "Log in" link, and `IndexModel.OnGetAsync` redirects an already-signed-in visitor straight to `RoleLandingPages.GetPath(user.Role)` instead of showing them a dead end. Live-verified in a real browser: anonymous visitor sees the styled card with working Log in/Privacy links; SystemAdmin, SessionManager, and TeamLead (the one role with a distinct landing page) all redirect correctly when already signed in.

- [x] ~~**No way to log out from within the app itself**~~ (found 2026-07-22 while browser-verifying the home page fix above, fixed same day). `_AppLayout` — the layout every SessionManager/Admin page actually uses — never included `_LoginPartial` (which has the real POST-form Log out button), so the only working logout control left anywhere in the app was on the near-vestigial scaffold `/Error` page. Fixed by adding a `.user-menu` kebab dropdown (same component `.help-menu` already uses) wrapping the existing role/team display in `_AppLayout`'s header, with a `Log out` POST form inside — live-verified: opens the dropdown, submits the logout POST, redirects to the now-styled `/`.

- [x] ~~**`claude-review` GitHub Action errors out on larger PRs instead of completing**~~ (found 2026-07-22 on PR #6; fixed 2026-07-22). `.github/workflows/claude-code-review.yml` ran the `code-review` plugin's `/code-review:code-review` command with no explicit tool-permission configuration for the sandboxed run — on a PR of any real size, the review agent needed things like `dotnet build`/`dotnet test` to review meaningfully, got denied every time (`permission_denials_count: 46` on PR #6's run), and the whole run reported `is_error: true` and failed — not because it found real issues, just an infra/config gap. `build-and-test` (the real quality gate) was unaffected. Fixed by adding `claude_args: --allowedTools "Bash(dotnet build *)" "Bash(dotnet test *)" "Bash(dotnet restore *)" "Bash(git diff *)" "Bash(git log *)" "Bash(git show *)"` — permission rule syntax (space before the wildcard) confirmed against this repo's own real `.claude/settings.local.json`, not guessed. Not blocking merges today (no branch protection on this private/free repo), but the check should now actually complete instead of erroring out — worth watching the next PR's run to confirm.

- [ ] **Session list filter row is confusing — the Status filter doesn't match the Status column, and the Team filter behaves differently than the other two** (reported 2026-07-29). `Pages/SessionManager/Index.cshtml(.cs)`. Three related fixes requested:
  1. The Status filter checkboxes (Upcoming/NeedsReview/Past) don't correspond to the labels actually shown in the table's Status chip (`ToRow`'s `statusLabel`: Active/Reschedule flagged/Completed/Cancelled) — align the filter's options to the same set of labels the column actually shows.
  2. Move "Upcoming" out of the Status filter and into the Date range dropdown instead (it's a time-window concept, not a lifecycle-status one).
  3. Reorder the filter row: Status, Date range, Team, then Page size — and move Page size to the right side of the table (near pagination) instead of sitting in the filter row.
  4. The Team `<select>` currently auto-submits on `onchange` with no Apply button, while Status and Date range are dropdown menus with an explicit Apply button — inconsistent and confusing per the report. Make Team match whichever pattern the other two end up using once (1)/(2)/(3) are settled.

## Feature requests (not yet triaged)

- [ ] **Link to the candidate's pending FCC *application* (not just the granted license) on the
  Candidate Detail page** (requested 2026-07-29, alongside the license link below). The license
  link (`https://wireless2.fcc.gov/UlsApp/UlsSearch/license.jsp?licKey=...`) is confirmed and
  shipped — ExamTools itself links to exactly this URL/param shape, and `FccUlsWatcherService`
  already captures the FCC ULS "Unique System Identifier" it needs (`Candidate.FccUlsLicenseKey`,
  set from `FccUlsLicenseRecord.UniqueSystemIdentifier`). The equivalent *application* (pre-grant,
  `Received` status) deep-link URL could not be verified live — `wireless2.fcc.gov`'s Application
  Search pages (`searchAppl.jsp`, and the site generally) returned Akamai "Access Denied" for both
  automated browsing and the user's own manual browser request during this investigation, so the
  `applView.jsp?applKey=...`-shaped guess was deliberately not shipped rather than risk a wrong
  link (see CLAUDE.md's "verify, don't guess" instruction). `FccUlsApplicationRecord` already
  carries the same `UniqueSystemIdentifier` needed once the URL shape is confirmed — revisit once
  ExamTools' own application-detail link (or a working manual FCC ULS search) can be observed
  directly, the same way the license link was confirmed this time.

- [x] ~~**Applicant Status page — rolling list of candidates awaiting their FCC grant**~~ (requested
  2026-07-29, built same day — see `docs/exam-result-license-class.md`'s "Applicant Status page"
  section, PR [#44](https://github.com/MikeWills/VeSessionManager/pull/44) open, not yet merged).
  New `Pages/SessionManager/ApplicantStatus.cshtml(.cs)`, team-wide (not scoped to one session):
  a "Pending FCC grant" worklist (`Tested=true`, not `Failed`/`NotTested`/`Granted`) that drops a
  candidate the instant they're `Granted`, plus the refined "Recently issued" section (`Granted` in
  the last 7 days, window not configurable yet) so a Session Manager can confirm a specific person's
  license/upgrade actually came through. No new backing fields — built entirely on
  `InitialLicenseClass`/`NewLicenseClass`.

- [ ] **User-facing documentation needs to be started** (requested 2026-07-29) — everything written so
  far is either developer/design-rationale docs (the `/docs/*.md` files — API shapes, architecture
  decisions, troubleshooting) or `TODO.md`/`CHANGELOG.md`'s own operational tracking. There's no
  actual guide yet for the people who use the app day-to-day (a Session Manager running a test
  night, a TeamAdmin doing first-time team setup). Also genuinely missing, per CLAUDE.md's own
  Documentation Structure table: `ARCHITECTURE.md` and `SECURITY.md` are named as the intended home
  for a system overview and a vulnerability-reporting policy, but neither file exists yet (only
  `README.md`/`CONTRIBUTING.md`/`CHANGELOG.md` do). No scope/format decided yet — revisit once
  ready to figure out what a real Session Manager/TeamAdmin actually needs walked through.

- [ ] **TeamAdmin (and SystemAdmin) need the ability to delete a session outright** (requested
  2026-07-29, prompted by the orphaned walk-in-candidate rows found while verifying the
  license-class backfill — see `docs/exam-result-license-class.md`). Session Manager already can't
  delete a session; nothing in the admin backend can either. Scope it to TeamAdmin/SystemAdmin only
  (not SessionManager) since this is a destructive, hard-to-reverse cleanup action, not a routine
  session-management one — see CLAUDE.md's "Executing actions with care" guidance on this class of
  action generally. Not a trivial delete: `Candidate.SessionId` and
  `SessionVolunteerExaminer.SessionId` are both `DeleteBehavior.Restrict` in `AppDbContext`, so
  cascading (or blocking-with-a-clear-error, e.g. "N candidates still attached") needs a deliberate
  decision, not just removing the FK constraint. Should audit-log the deletion
  (`AuditLogExtensions.AddAuditLog`) same as every other destructive action in this app, and likely
  wants a confirmation step given it can't be undone. Also worth deciding whether re-ingestion would
  silently recreate the session next poll if it's still present in ExamTools' feed (probably should
  — this is for cleaning up genuinely orphaned/stale local rows, not fighting the source of truth).

## Carried over from earlier phases

- [x] ~~Confirm the production ExamTools host~~ (confirmed 2026-07-28) — `alpha.exam.tools` is the real production host (already correctly set in `appsettings.json`), not `exam.tools`. See `docs/examtools-api.md`.
- [ ] Review `DevDataSeeder`'s $15/$7 ARRL fee amounts against the real current fee schedule before this touches real candidates
- [x] ~~Retest payment reminders~~ (flagged in spec.md's Phase 6 section; fixed 2026-07-22, see `docs/payment-reminders.md`'s own "Retest payments" section). The 5-/10-day reminder logic was gated purely on `ApplicationDateEnteredUtc` (`Received`), which a retest `Candidate` never gets — it's permanently `Failed` (terminal) with no FCC application of its own. Both `PaymentReminderService` passes now carry a second branch for `Reason=Retest && ApplicationStatus=Failed`, anchored on `Candidate.ResultMarkedUtc` instead — exactly the spec's own suggested fix ("gate retest reminders on the Session Manager having marked *some* result, not FCC status"). 6 new unit tests (`PaymentReminderServiceTests`), including a regression guard confirming a *Failed candidate's original InitialExam payment* is still correctly excluded — only the `Retest` reason gets the exception.

## Deferred (no urgency, revisit when ready)

- [x] ~~Deployment: no systemd unit file or working GitHub Actions deploy step exists yet~~ — **stale, this was actually finished 2026-07-21** (see `CLAUDE.md`'s "Known Constraints"/"Deploy topology" bullets; this TODO entry just never got updated to match). `.github/workflows/deploy.yml` is a fully working deploy pipeline (GitHub-hosted runner + ephemeral Tailscale join, no self-hosted runner needed), and `docs/deployment.md` documents the systemd unit files and one-time server setup in full.
  - [ ] **Genuinely still open:** the public domain, `ve.wx0mik.radio`, was decided 2026-07-22 (see `docs/deployment.md`'s "Apache Virtual Host" section) but the Apache vhost + Let's Encrypt cert haven't actually been provisioned on the real server yet. A second domain for a second team is possible later but not needed now — purely cosmetic branding, no code/deploy change required either way.
  - [ ] **Genuinely still open:** the one-time server-side setup (`vesessionmanager` service account, sudoers file, app/data directories, 5 GitHub repo secrets — all documented step-by-step in `docs/deployment.md`) hasn't been run against the real server yet. Operational work, not code.
- [x] ~~**Purge stale unpaid Square payment links**~~ (parked 2026-07-23, built 2026-07-28, see `docs/payment-link-purge.md`). Threshold resolved as **per-Team configurable, `Team.PurgeUnpaidLinkDays`, default 30** — not tied to the existing fixed 10-day `Payment.ExpiredUnpaid` window. `SquarePaymentLinkPurgeService`/`SquareLinkPurgeJob` clear our own DB reference (`PaymentLinkUrl`/`SquarePaymentReferenceId`/`SquarePaymentLinkId`) in the same save as the Square delete call, and the new `Payment.SquareLinkPurgedUtc` field closes the auto-regen loop risk by also excluding purged rows from `PaymentGenerationService`'s link-generation scan.
- [x] ~~**Zoom meeting templates**~~ — **dead end, closed 2026-07-28/29.** Original idea: use a saved
  meeting template (`template_id`) instead of the manually-specified settings `ZoomMeetingRequest`
  sends, picked from a dropdown populated live via `GET /users/{userId}/meeting_templates`.
  - Real constraint found via Zoom's own devforum: `POST .../meetings`'s `template_id` param only
    works with **Admin**-type templates — personal ones fail, and Admin templates aren't enabled by
    default on every plan.
  - Blocked 2026-07-23 on missing `meeting:read:list_templates` scopes; scopes were added and the
    diagnostic script (`scripts/check-zoom-meeting-templates.py`) re-run 2026-07-28 against the real
    per-team credentials — it found exactly **one** template, and it's **personal-type, not Admin**.
  - Per this item's own stated exit condition ("if it turns up zero Admin-type templates, this whole
    feature is a dead end... and should be dropped"): that condition is met. Not worth building a
    template-picker UI around a feature this Zoom plan doesn't actually support. If a future Zoom
    plan/account change ever enables Admin templates, this can be revisited then — nothing about
    tonight's Zoom breakout-rooms feature (see the Change Log) depends on or blocks this.
- [x] ~~**Audit the candidate email flow**~~ (requested 2026-07-23, audited and closed out 2026-07-27). Confirmed the current cadence (registration confirmation, day-before reminder, payment reminder/expiration, each gated by its own `...SentUtc`) is sane as-is — no gaps or changes needed.
