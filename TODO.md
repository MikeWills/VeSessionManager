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

- [x] ~~**TeamLead saw an "Unmatched Payments" nav link that 403'd on click**~~ (found 2026-07-30
  while auditing the nav for the regrouping below; fixed same day). The link in `_AppLayout.cshtml`
  was rendered ungated for every role, but `UnmatchedPayments.cshtml.cs` is
  `[Authorize(Roles = "SystemAdmin,TeamAdmin,SessionManager")]` — it was the only nav link whose
  visibility didn't match its page's own roles, so a TeamLead could see it and get an Access Denied.
  Now gated on `Role is not UserRole.TeamLead`; live-verified the link is gone entirely for that
  role, not merely non-functional.

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

- [x] ~~**Remove the standalone VEC Submission page — redundant with the Sessions list**~~ (reported
  2026-07-30, removed same day). Same class of redundancy as the two ExamTools items above, but
  internal: the page listed *every* active session for a team, not just actionable ones — for HRCC
  that meant **40 rows of which only 8 were actionable** (16 future sessions that can't have anything
  to submit yet, 16 already submitted). Its header count (8) didn't match its own table (40), which
  is what made it feel broken. Everything it offered already existed elsewhere: the Sessions list has
  a VEC Submission column, session Detail has the Mark-submitted action, and the nav badge (added
  earlier the same day) surfaces the pending count.

  Replaced by a **"Pending VEC submission" option in the Sessions page's Status filter**
  (`Index.cshtml(.cs)`). It's deliberately a different axis from the other four checkboxes — those
  are mutually exclusive lifecycle states mirroring the Status column, this one cuts across them —
  but it lives in the same group because the group already ORs its members, so ticking only it yields
  exactly the worklist. Its predicate **must stay identical to
  `NavBadgeCountService.CountSessionsPendingVecSubmissionAsync`**, which backs the nav badge; there's
  a comment on both sides saying so. Verified live: filter returns 8 of 8, matching the badge.

  Removed `Pages/SessionManager/VecSubmission.cshtml(.cs)`, `VecSubmissionReportService` (only that
  page used it), and their DI registrations. **Kept `VecSubmissionService`** — session Detail still
  uses `MarkSubmittedAsync`. Its 6 predicate tests weren't deleted but retargeted onto
  `NavBadgeCountService` as `PendingVecSubmissionCountTests`, so the cancelled-session/non-terminal/
  mixed/team-scoping coverage survives. The pending-count badge moved onto the Sessions nav link with
  a `title` explaining what it counts. Also fixed on the way out (and still live in
  `UnmatchedPayments`): a bare `RedirectToPage()` after Mark-submitted dropped the `?teamId=` query
  string, stranding a multi-team user on the empty "no team context" page after every action.

- [x] ~~**FCC class upgrades never confirmed — 20 candidates stuck pending**~~ (found 2026-07-30,
  fixed same day). Full design in `docs/fcc-uls-watcher.md`'s "Confirming a class upgrade". The
  Grant-Date guard added earlier that day correctly killed false-positive grants but made upgrades
  *permanently* undetectable, since FCC pins Grant Date to the original license and never advances it
  on an upgrade — 20 of 21 pending candidates were upgrades, the oldest stuck 19 days. Two verified
  facts made it solvable: `AM.dat` (in every archive, previously never opened) carries the current
  operator class, and `HD`'s **Last Action Date does** advance on an upgrade. An upgrade now grants
  only when the class FCC reports equals `NewLicenseClass` **and** last action is on/after the
  session — both halves load-bearing, since class alone re-confirms someone who already held it and
  date alone matches any unrelated admin action.

  Recovered **11 candidates** in one `--run-fcc-weekly` pass; the remaining 10 are legitimately
  pending (sessions from 07-25 on, where FCC genuinely hasn't acted — spot-checked two by hand
  against the raw ULS snapshot). Post-run invariant held: zero granted candidates with a grant date
  predating their session, zero missing a call sign.

  Also landed: `--run-fcc-daily` / `--run-fcc-weekly` Worker switches, replacing the
  "temporarily rewrite `FccDailyWatcherStartHourEt` → restart → put it back" dance previously needed
  to force a run. **Use `--run-fcc-weekly` for historical recovery** — the weekly complete snapshot
  holds current state for every active license regardless of grant date, whereas a daily file only
  carries one day's transactions and can never recover a prior week's candidate.

- [x] ~~**Applicant Status / Unmatched Payments: days-pending anchor, 5/10-day colouring, and
  Sessions-style team filter**~~ (all reported 2026-07-30, done same day). Three related passes over
  the same two pages, no linked doc.

  **Days pending now counts only from `ApplicationDateEnteredUtc`** — FCC's Last Action Date on the
  matched application. While a candidate is still Unmatched ("VEC Processing") FCC has nothing on
  file, so there is no clock and the column shows an em dash rather than a number. Two earlier
  anchors were wrong for the same underlying reason — they measured time FCC wasn't responsible for:
  `DateRegisteredUtc` counted from sign-up (a same-day session already showed several days), and
  falling back to `Session.ScheduledStartUtc` still started at the exam, during the VEC's own
  processing window.

  **Day 5 / day 10 colouring** against `PaymentReminderService`'s own two passes — those constants
  are now `public` and referenced, not restated, so a tuned reminder can't silently leave the UI
  colouring rows on days when nothing happens. Escalation is gated on an outstanding **Unpaid**
  payment, mirroring what both passes actually require: with no unpaid payment no reminder is sent
  and nothing is ever marked `ExpiredUnpaid`, so a red row would warn about an impossible event
  (this also correctly covers `NotApplicable` payments and fee-free sessions). An earlier version
  gated on FCC's `FccPaymentStatus` instead, built on an invented "FCC dismisses at 10 days"
  rationale — wrong mechanism entirely, corrected once the real one was checked.

  **Team filter now matches the session list, including "All teams."** Both pages used
  `TryResolveViewableTeamId`, which collapses to one team and treats null as "no context, show
  nothing" — hence the empty page needing a re-pick after every action. New
  `SessionAccessScope.ResolveViewableTeamIds` (null = every team, extracted from the existing
  `Scope()` so all three pages share one definition; `Scope()` reimplemented on top of it) plus the
  same radio-dropdown component, with per-team counts on the menu items and the total on "All teams".

  Two bugs the merged view exposed, both fixed here: (1) **a SystemAdmin could never match an
  unmatched payment** — the guard read `GetEffectiveTeamIds(user)?.Contains(...) ?? false`, which is
  null for SystemAdmin and so always `false`; invisible while the page forced one team, and a
  standing comment wrongly described it as deliberate. (2) **cross-team matching became possible** —
  with several teams on screen nothing stopped attributing Team A's money to Team B's candidate; the
  dropdown now offers same-team candidates only and `OnPostMatchAsync` re-checks server-side. Also
  added a Team column to both pages (shown only when more than one team is available, as Sessions
  does) so merged rows are attributable.

  **Not covered by tests:** the days-pending/colouring logic lives in a Razor page model and there is
  no Web test project (`EasternTimeFormatter`/`LicenseClassFormatter` are uncovered for the same
  reason). Verified instead against a throwaway DB copy with backdated rows — which is how the
  `display:inline-block`-on-a-`<td>` bug got caught. `ResolveViewableTeamIds` is in Core and does
  have tests.

- [x] ~~**FCC upgrade recovery missed 4 candidates — the weekly snapshot arrives days stale**~~
  (found 2026-07-30 by the user spotting still-pending FRNs that were in fact issued, fixed same
  day). See `docs/fcc-uls-watcher.md`'s "The weekly snapshot is not a rolling backstop". Immediate
  follow-on to the AM.dat upgrade fix above: that pass recovered 11 and left 10 pending, which I
  reported as "legitimately pending" after hand-checking only 2 of the 10 and generalizing. Four had
  actually been upgraded days earlier.

  Root cause was file coverage, not matching. FCC's weekly `complete/l_amat.zip` stamps its own
  creation date inside the zip — the copy fetched Thursday read `Sun Jul 26`, nothing newer than
  07/25 — while `RunDailyAsync` reads only yesterday+today. Monday's and Tuesday's files fell in
  between and were read by neither. New `RunAllDailyFilesAsync` sweeps Mon-Sat (no Sunday file
  exists); `FccWeeklyCatchupJob` now runs it alongside the snapshot, and `--run-fcc-all-dailies`
  is the fast manual path (~8s / tens of KB, vs ~5min / 199MB).

  Recovered 4 more (pending 10 -> 6). The remaining 6 were then verified individually against all
  seven files rather than assumed: five appear only in the stale weekly snapshot still holding their
  pre-exam class, one has no FCC license record at all. Their sessions were Jul 27-30 and the newest
  published file is Wednesday's, so there is genuinely nothing to find yet.

  An existing test asserted the weekly catch-up must *not* call daily endpoints — that assertion
  encoded the broken premise, so it was narrowed with a comment rather than deleted.

- [x] ~~**Per-row action menu on the Sessions list**~~ (requested 2026-07-30, built same day). Direct
  follow-on to the VEC Submission removal above — once that worklist became a Sessions filter, every
  action still required opening each session's Detail page, so a filtered list of 8 pending sessions
  meant 8 round-trips. `Index.cshtml(.cs)` now renders a kebab `⋮` column reusing the same
  `.kebab`/`.menu` component the Detail page's candidate roster uses: **Mark submitted to VEC**,
  **Mark session completed**, **Clear reschedule flag**, and (below a rule) **Delete session** behind
  a per-row confirmation modal stating the candidate/payment/VE-roster rows it cascades to.

  Two things worth remembering:
  - The `SessionRow.Can*` flags only decide *what's worth rendering*; each of the four POST handlers
    independently re-resolves the user and re-checks `AdminAccessScope`/`SessionAccessScope`
    server-side. A hidden menu item is not an authorization control.
  - **`asp-page-handler` builds the form action from the route alone and drops the query string**, so
    every action silently reset the list back to unfiltered. Fixed with `BuildActionUrl(handler)`
    (`BuildPageUrl(PageNumber)` + `&handler=…`) and a matching `RedirectToCurrentView()`. But an
    explicit `action=` attribute also makes `FormTagHelper` stop emitting the antiforgery token —
    every POST then 400s before reaching the app, with **nothing logged server-side**. Both forms of
    this bite together: any form here needs `action="@Model.BuildActionUrl(…)"` *and* an explicit
    `asp-antiforgery="true"`.

- [x] ~~**TeamLead has no real view yet**~~ (found during Phase 9d's self-audit against 9a-9c, 2026-07-21; fixed 2026-07-22). Added `TeamLead` to `[Authorize]` on `Pages/SessionManager/Index.cshtml.cs`/`Detail.cshtml.cs`/`VeRoster.cshtml.cs`/`VecSubmission.cshtml.cs`, added a new `SessionAccessScope.CanView` (view-only, unlike `CanEdit` doesn't carve out TeamLead) to gate page *display* separately from the existing `CanEdit` write-gate, and hid every write control (buttons/forms/kebab menu/modals) in `Detail.cshtml`/`VecSubmission.cshtml` behind a `CanEdit` flag exposed from the page model. `RoleLandingPages` now sends TeamLead to `/SessionManager/Index` like every other role; the `Pages/TeamLead/Index.cshtml` placeholder was removed. **Found and fixed a second, previously-latent bug in the same change:** `SessionAccessScope.GetEffectiveTeamId`'s TeamLead branch needs `User.ManagedByUser` eager-loaded, but `UserManager.GetUserAsync(ClaimsPrincipal)` (used everywhere) never loads it — since no page had ever exercised the TeamLead path before, this had never been caught; a TeamLead would have signed in successfully and silently seen zero sessions. Fixed with a new `CurrentUserLoader.GetUserWithManagerAsync` extension (`Web/CurrentUserLoader.cs`) that all four pages now use instead of the bare `userManager.GetUserAsync`. Live-verified in a real browser: `teamlead@example.com` lands on Sessions, sees only their assigned team's sessions/roster/submission status with zero write controls anywhere; `sessionmanager@example.com` confirmed unaffected (full edit access still present) as a regression check.

- [x] ~~**No real fix yet for youth-rate underpayments getting auto-matched**~~ (found during a security review of the Square unmatched-payment-matching feature, 2026-07-21/22; built 2026-07-27, see `docs/youth-payment-confirmation.md`). Built as designed below, with two changes from the researched plan: the COPPA checkbox was simplified out entirely (just plain informational text/link now, no checkbox, nothing submitted or stored — not even a timestamp) and `Candidate.CoppaFormConfirmedUtc` was never added. A candidate now gets a second, youth-specific link in the registration confirmation email; confirming self-identifies them as a youth, deletes their standard-rate Square link, generates a new one at `FeeConfiguration.YouthExamFeeAmount`, and redirects straight to the new checkout. **Still an accepted risk, not fixed by this:** the separate Square-hosted checkout page (the other, unrelated path a candidate could use to underpay) was not retired — `AmountMismatchFlaggedUtc` remains the backstop for that path, per the original design note.
  - Design researched 2026-07-23, built 2026-07-27 — full rationale, data model, and known limitations in `docs/youth-payment-confirmation.md`.

- [x] ~~**Home page (`/`) had no styling or navigation**~~ (reported 2026-07-22, fixed and live-verified same day). `Pages/Index.cshtml` was still the untouched scaffold-default Bootstrap page — no app styling, no working navigation — since nothing had ever restyled it (the same class of gap Phase 9's polish pass already fixed for `Login`/`AccessDenied`/`Logout`/`ExternalLoginCallback`). Now uses `_PublicLayout` (matches Login/Privacy) with a "Log in" link, and `IndexModel.OnGetAsync` redirects an already-signed-in visitor straight to `RoleLandingPages.GetPath(user.Role)` instead of showing them a dead end. Live-verified in a real browser: anonymous visitor sees the styled card with working Log in/Privacy links; SystemAdmin, SessionManager, and TeamLead (the one role with a distinct landing page) all redirect correctly when already signed in.

- [x] ~~**No way to log out from within the app itself**~~ (found 2026-07-22 while browser-verifying the home page fix above, fixed same day). `_AppLayout` — the layout every SessionManager/Admin page actually uses — never included `_LoginPartial` (which has the real POST-form Log out button), so the only working logout control left anywhere in the app was on the near-vestigial scaffold `/Error` page. Fixed by adding a `.user-menu` kebab dropdown (same component `.help-menu` already uses) wrapping the existing role/team display in `_AppLayout`'s header, with a `Log out` POST form inside — live-verified: opens the dropdown, submits the logout POST, redirects to the now-styled `/`.

- [x] ~~**`claude-review` GitHub Action errors out on larger PRs instead of completing**~~ (found 2026-07-22 on PR #6; fixed 2026-07-22). `.github/workflows/claude-code-review.yml` ran the `code-review` plugin's `/code-review:code-review` command with no explicit tool-permission configuration for the sandboxed run — on a PR of any real size, the review agent needed things like `dotnet build`/`dotnet test` to review meaningfully, got denied every time (`permission_denials_count: 46` on PR #6's run), and the whole run reported `is_error: true` and failed — not because it found real issues, just an infra/config gap. `build-and-test` (the real quality gate) was unaffected. Fixed by adding `claude_args: --allowedTools "Bash(dotnet build *)" "Bash(dotnet test *)" "Bash(dotnet restore *)" "Bash(git diff *)" "Bash(git log *)" "Bash(git show *)"` — permission rule syntax (space before the wildcard) confirmed against this repo's own real `.claude/settings.local.json`, not guessed. Not blocking merges today (no branch protection on this private/free repo), but the check should now actually complete instead of erroring out — worth watching the next PR's run to confirm.

- [x] ~~**Session list filter row is confusing — the Status filter doesn't match the Status column, and the Team filter behaves differently than the other two**~~ (reported 2026-07-29, found already built when re-checked 2026-07-30 — this entry had just never been marked done; see `IndexModel.cs`'s own class doc comment and CLAUDE.md's Change Log, "Filter-row realignment"). `Pages/SessionManager/Index.cshtml(.cs)`. All four requested fixes confirmed present:
  1. Status filter checkboxes now exactly match the table's Status chip labels — `Active`/`RescheduleFlagged` ("Reschedule flagged")/`Completed`/`Cancelled`, replacing the old `Upcoming`/`NeedsReview`/`Past` set.
  2. "Upcoming" lives in the Date range dropdown (`Upcoming`, `Last7PlusUpcoming`, `Last7`/`14`/`30`/`60`/`90`, `Last6Months`, `Last12Months`, plus "Any time"), not the Status filter.
  3. Filter row order is Status → Date range → Team; Page size now sits in the pagination block (references the filter form via `form="sessionFilters"` rather than being physically inside it).
  4. Team is now the same radio-buttons-plus-explicit-Apply-button `.filter-dropdown` pattern as Status and Date range — no more auto-submitting `onchange`.

## Feature requests (not yet triaged)

- [x] ~~**Backfill license grants from ExamTools' ULS API for candidates the FCC files left pending**~~
  — **superseded and shipped 2026-07-31 as a full replacement, not a backfill.** Mike's follow-on
  decision: *"The more I think about it, the more I just want to use the ET API... I like the more
  simple approach logic-wise, even if the application information isn't timely/100% accurate."* The
  whole FCC bulk-file subsystem was deleted rather than kept as a primary path. See
  `docs/uls-watcher.md` and CLAUDE.md's Change Log. **Open follow-ups pulled out of this item are
  listed separately below.** Original research retained for context: ExamTools displays the issued call sign on its Manage Session screen
  (`0038704029 (issued: KC1ZYU)`) *hours* before FCC's bulk files carry it — confirmed live that
  morning: ET showed KC1ZYU at 08:08 while the call sign appeared in **zero** of the fourteen
  published FCC daily archives. Mike believes there's an ET API for call sign lookup; finding it is
  the first task.

  **Already ruled out: `export/basic.json`'s `callsign` field is NOT it** (checked live 2026-07-31
  against session `6a41e220f08c6c37d08459d5`, so nobody needs to re-run this). That field holds the
  call sign the applicant **supplied at registration**, and never refreshes after issuance:

  | Applicant | `basic.json` `callsign` | Reality |
  |---|---|---|
  | Bae | `WH6HJZ` | pre-existing, upgraded tech→gen |
  | Buehler | `KN6ISV` | pre-existing, upgraded gen→extra |
  | **Harnish** | **empty** | **granted KR4NZD 07/29 — two days before the check** |
  | Losada | empty | granted KC1ZYU per ET's own UI |

  Harnish is the decisive control: granted two days earlier, ET's UI shows "(issued: KR4NZD)", and
  the field is *still* empty. The full key set on an applicant record is `id, pin, firstname, middle,
  lastname, suffix, email, phone, frn, callsign, licenseClass, has_felony, created, addr, city,
  state, zip, finalized` — **no issued-call-sign field of any kind**. So this is a real API hunt.

  **FOUND IT — `GET https://exam.tools/api/uls/lookup/{callsignOrFrn}`** (Mike supplied the URL
  2026-07-31; verified live the same hour, **unauthenticated**, no session cookie needed). Returns a
  JSON ULS record far richer than what this app reconstructs from the bulk files:

  ```
  u_id, callsign, frn, license_status (Active|Pending), license_class, prev_license_class,
  grant_date, effective_date, expired_date, cancellation_date, bqqResponse, is_revoked,
  applicant_type, licensee_id, name/address fields,
  pendingApplications[]: { uls_filenumber, application_status, application_purpose, source,
                           receipt_date, history[]: { log_date, code, code_text }, comments[] }
  ```

  **This subsumes three separate pieces of hard-won FCC-file logic:**
  - `license_class` + `prev_license_class` replace the `AM.dat` operator-class join (added 2026-07-30).
  - **`effective_date` is the field that advances on an upgrade** — it is ExamTools' rendering of
    HD's Last Action Date. Verified on Nielsen: `grant_date` 2024-08-21 (stuck at the original
    license, exactly as FCC behaves) but `effective_date` 2026-07-30 with
    `prev_license_class: General` → `license_class: Amateur Extra`. The whole "confirming a class
    upgrade" problem is one field here.
  - `pendingApplications[].history[]` (`RDLCOM` etc.) and `bqqResponse` replace the `HS.dat`
    Red Light / BQQ / payment-verification parsing.
  - `u_id` is the ULS Unique System Identifier — i.e. `Candidate.FccUlsLicenseKey` for the existing
    "(FCC license ↗)" deep link.

  **Use `/api/uls/lookup2/{frnOrCallsign}` — NOT `/lookup/`.** Mike supplied `lookup2` minutes after
  `lookup`; it is strictly better and removes the only real obstacle. `/lookup/` by FRN returns a
  stale index — Anthony's FRN gave `license_status: "Pending"` with no `callsign` at all while his
  call sign gave the grant, a chicken-and-egg since the FRN is all we store. **`lookup2` by FRN
  resolves the same-day grant correctly**, so keying on `Candidate.Frn` just works. It also adds a
  `type` field: `existing` for a hit, **`notfound`** for an unknown FRN — a clean sentinel, so no
  exception handling or empty-body special case is needed.

  Verified live 2026-07-31 across five real candidates:

  | Candidate | `lookup2` by FRN | Reading |
  |---|---|---|
  | Losada (granted today) | Active, `KC1ZYU`, Technician, eff **2026-07-31** | same-day grant, hours before FCC's file |
  | cand 74 (tested today) | Active, KO6NUS, **Technician**, eff 2026-05-22 | pre-existing licence, upgrade **not** granted |
  | cand 77 (tested today) | Active, KF8DBR, **Technician**, eff 2025-09-30 | pre-existing licence, upgrade **not** granted |
  | Nielsen | Amateur Extra, prev `General`, eff 2026-07-30 | upgrade confirmed |
  | `0000000000` | `type: "notfound"` | sentinel, not an error |

  Candidates 74 and 77 are the useful negative case: both tested for General, both still report
  `license_class: Technician` with an `effective_date` months *before* their session — so class
  mismatch **and** stale effective date both say "not yet," which is exactly the two-part upgrade
  rule `FccUlsWatcherService.ProcessLicensesAsync` implements, obtainable in one call. This also
  independently confirmed the app's own "3 still pending" figure on the day.

  Parsing note: `prev_license_class` is **absent** on some records and **present-but-empty** on
  others (Losada absent, cand 74/77 `""`) — treat the two identically.

  **Risks to weigh before depending on it:** undocumented and unauthenticated, so it can change or
  start rate-limiting without notice; it is ExamTools' own ULS mirror, so it inherits their refresh
  lag (demonstrated above) rather than being FCC-authoritative. Keep `FccUlsWatcherService` as the
  source of truth and treat this as an accelerator — same constraint as the design note above.
  Be polite with request volume: one lookup per candidate per poll, not per tick.

  ### Decided approach (Mike, 2026-07-31) — gap-filler, not a replacement

  > "I trust the ET license grants, I don't trust the applications."

  **Process the FCC files exactly as today, then check this API only for the holes.** The FCC pass
  stays the primary path and keeps its current semantics; afterwards, any candidate still sitting
  unresolved gets one `lookup2` call. The goal Mike stated is explicitly **agreement with
  ExamTools** — ET is what a Session Manager actually looks at, so a candidate showing granted there
  and pending here is the defect being fixed, regardless of which system is "more correct."

  **Scope — grants only.** Per the quote above, take *only* the license-grant fields:
  `callsign`, `license_class`, `prev_license_class`, `effective_date`, `grant_date`, `u_id`
  (→ `Candidate.FccUlsLicenseKey`, which makes the existing "(FCC license ↗)" link work at the same
  time). **Deliberately out of scope: `pendingApplications[]`, its `history[]` codes, and
  `bqqResponse`** — that is application data, which Mike does not trust from this source. Do **not**
  wire it into `FccApplicationStatus`/`FccHoldReason`/`FccPaymentStatus`; those stay `HS.dat`-derived
  (and see the note above about how rarely they populate at all). An earlier draft of this entry
  listed those as bonuses — they are not, they are excluded.

  **Which candidates count as a "hole":** `Tested && ApplicationStatus is Unmatched or Received` —
  the same predicate the Applicant Status page's "Pending FCC grant" worklist already uses. Small
  by construction (3 on the day this was written), so one call each is cheap and no batching or
  rate-limit design is needed.

  **Reuse the existing grant rules — do not invent new ones.** The two-part upgrade test in
  `FccUlsWatcherService.ProcessLicensesAsync` (class equals `NewLicenseClass` **and** the advancing
  date is on/after `Session.ScheduledStartUtc`) must apply identically here, with `effective_date`
  as the advancing date — it is ET's rendering of HD's Last Action Date. Skipping that guard
  re-opens the false-positive grants that hit Nielsen/Schneider/Coffey on 2026-07-30. Candidates 74
  and 77 above are the ready-made regression case: `lookup2` returns them Active/Technician with an
  `effective_date` predating their session, and correct behaviour is to leave them pending.

  **Priority (Mike, 2026-07-31): "The issuance is the most important, the rest is just good
  information."** Grant/issuance is the critical function; application status, hold reasons and fee
  status are nice-to-have. Two consequences:

  - **The backfill must run independently of the FCC file pass, not chained behind it.** The
    download is the flaky half — `data.fcc.gov` 403'd on 2026-07-27 and left the weekly safety net
    dark for a full week, and the snapshot arrives days stale regardless. If a file download fails
    or returns nothing, the `lookup2` backfill should still run and still grant. Making the critical
    function a continuation of the unreliable one inherits its failure modes for nothing. Run both
    within the watcher tick, but let each succeed or fail on its own.
  - Effort and test coverage belong on the grant path. A gap in `FccHoldReason`/`FccPaymentStatus`
    is cosmetic (and those fields populate almost never anyway — see
    `docs/fcc-uls-watcher.md`); a missed issuance is the thing a Session Manager actually notices,
    because ExamTools is showing it on the same screen.

  Follow the established scan-based/idempotent job shape — the `ApplicationStatus` transition is
  itself the idempotency guard, so a crash mid-run cannot double-process.

- [ ] **Ask ExamTools whether `/api/uls/lookup2/` is a supported endpoint** (raised 2026-07-31, not
  yet done). The ULS watcher now depends on it entirely. It is undocumented and unauthenticated, so
  it can change shape, add auth, or start rate-limiting with no notice, and it is ExamTools' own
  mirror rather than FCC direct. Worth a short message asking (a) whether it's supported/stable,
  (b) whether automated polling is acceptable and at what rate — the app currently makes one request
  per non-terminal candidate, twice a day (7 requests on the day it shipped). A "yes" removes most of
  the risk; a "please don't" is far better learned now than after a silent breakage. Fallback if it
  goes away: the FCC file parser is recoverable from git history (deleted in the same commit).

- [ ] **Confirm FCC's ULS *application* deep-link URL shape** (carried over, still open). The licence
  link ships and is verified; the application equivalent does not, because `wireless2.fcc.gov` returns
  Akamai "Access Denied" to automated requests *and* to a manual browser attempt, so
  `applView.jsp?applID=…` was never confirmed. Applicant Status currently links to FCC's Application
  Search landing page and renders `Candidate.UlsApplicationFileNumber` beside it for paste-in lookup.
  To close: observe a working ULS application URL from a browser that can reach the site (or from
  ExamTools' own applicant link) and replace `FccUlsLinks.ApplicationSearch`.

- [ ] **Per-team, per-integration enable/disable switches** (requested 2026-07-31). Goal: run MARC as
  a real production team, HRCC as the live-monitoring team, and a personal team pointed at ExamTools'
  development environment for reproducing issues — **all in one deployment on one code base** — while
  being able to exercise one integration at a time without the others emitting anything public or
  leaving a mess that's awkward to clean up.

  **Not a single Test/Production mode.** An earlier draft of this item proposed one binary per-team
  flag; that was rejected on 2026-07-31 for being too coarse. The unit of control is the
  **individual integration**, independently switchable per team, so "test Square link generation
  tonight with Discord and email silent" is expressible.

  **A master switch gates the whole group, and collapses it in the UI** (decided 2026-07-31). One
  per-team master toggle sits above the per-integration switches; while it's off the individual
  options are collapsed out of sight, so an ordinary production team's settings page doesn't grow a
  wall of controls nobody touches. **Master off must also mean the individual switches don't
  apply** — enforcement reads the master first — otherwise a switch left set from an old testing
  session stays hidden behind a collapsed panel and silently mutes a team that's since gone
  production. Corollary: turning the master off restores full normal operation in one action, which
  is also the recovery path if a team was accidentally left muted.

  **Visibility: TeamAdmin and SystemAdmin only** (decided 2026-07-31 — supersedes this item's earlier
  SystemAdmin-only suggestion). SessionManager and TeamLead never see the control at all. As
  everywhere else in this app, hiding the control is not the authorization check: the page handler
  re-resolves the user and re-checks `AdminAccessScope.CanManageTeam` server-side, same rule as the
  session-delete action.

  **Off means off — not redirected.** Explicitly decided, and the opposite of what the existing
  global test mode does: `SystemSettings.TestModeEnabled` / `TestModeOverrideEmail` (migration
  `TestMode`, applied in `SmtpEmailSender` via `TestModeEmailRedirector`) only ever *redirects*
  email to an override inbox. These switches suppress the call outright. The two are independent
  layers and both must be honoured — a disabled integration is never redirected, it simply doesn't
  happen.

  **Four switches** — one per outbound system, each covering *every* call that system makes, not
  just the obvious one:
  - **Zoom** — `CreateMeetingAsync` / `UpdateMeetingAsync` / `DeleteMeetingAsync`. (Not in the
    original ask; `SessionEventSchedulingService` creates real meetings on whatever account owns the
    Server-to-Server credentials, so a test team churning them is calendar clutter at best and a
    scheduling collision at worst.)
  - **Discord** — `CreateEventAsync` / `UpdateEventAsync` / `DeleteEventAsync`, all three.
  - **Square** — link creation is the obvious one, but `ISquareClient` has two more outbound calls
    that hit a real merchant account: `CompleteOrderAsync` (fires when a session is marked completed)
    and `DeletePaymentLinkAsync` (`SquareLinkPurgeJob`).
  - **Email** — **one switch covering all five kinds** (decided 2026-07-31; per-template granularity
    was considered and rejected as not worth the UI and settle-marker plumbing):
    `RegistrationConfirmation`, `DayBeforeReminder`, `PaymentReminder5Day`,
    `PaymentExpirationNotice` (goes to `EmailSettings.AdminNotificationEmail`, not a candidate —
    still noise), and the youth-rate confirmation link.

  **A switched-off integration deletes nothing** (decided 2026-07-31). Teardown is suppressed along
  with creation — a cancelled session's Zoom meeting and Discord event stay put, and
  `SquareLinkPurgeJob` leaves that team's links alone. Accepted consequence: anything already
  created before the switch went off is **orphaned in the real account permanently** and needs
  manual cleanup, since the no-backlog rule below means re-enabling won't retroactively tidy it
  either. The safe order of operations when muting a team that has live resources is therefore
  *clean up first, switch off second.*

  **No backlog on re-enable** (decided 2026-07-31). Work skipped while a switch was off is never
  queued — flipping it back on starts fresh from that moment, so a week of muted reminders can't
  suddenly fire at a real candidate. This is what makes the settle marker below need to record
  *why* something was skipped, not merely that it was.

  **Deliberately not switchable** — all confirmed 2026-07-31: ExamTools ingestion (read-only, and
  reproducing issues is the whole point — the dev host is already per-team via
  `Team.ExamToolsBaseUrl`); the FCC ULS watcher (read-only download of a public bulk file, and the
  job isn't per-team anyway); VE roster sync, exam-result sync, VEC submission marking, and
  session/candidate actions (local DB writes only); **the Square inbound webhook** (a delivery only
  arrives if someone acts in Square, which is deliberate by definition, and processing it is
  local-only — keeping it is what lets a muted team still verify the payment flow); and the **PII
  purge** (keeps running on every team — local deletes, and a muted team's data should age out like
  anyone else's).

  **Suppress the Worker's per-poll noise for a deliberately-disabled integration.** Explicitly
  requested: a dev-environment team shouldn't produce a log line every tick about something that's
  off on purpose. This forces a distinction the codebase doesn't currently draw —
  **"unconfigured" ≠ "disabled"**:

  | | Meaning | Log | Retry on next poll? |
  |---|---|---|---|
  | Unconfigured (`IsConfigured == false`) | admin hasn't finished setup | one quiet aggregate `INFO` per poll (existing behaviour) | yes — so adding credentials backfills automatically |
  | **Disabled (new)** | deliberate, indefinite | once at startup/state-change, then silent | **no** |

  That second row is the whole design problem. Every one of these gates sits right next to an
  existing `IsConfigured` check, whose established pattern is "skip quietly, leave the
  `...SentUtc`/`...Id` tracking field null so the next poll retries." Reusing it means a disabled
  integration re-attempts and re-logs forever and never settles. A disabled integration must mark
  the work *settled without doing it* — **but not by writing a real-looking `...SentUtc`**, or
  re-enabling the switch would leave a backlog of sessions that look already-handled. Same class of
  mistake as the `!IsConfigured || succeeded` gotcha in CLAUDE.md's Known Constraints. A dedicated
  test should pin both halves: no calls made, *and* no re-attempt next tick, *and* no repeated log
  line.

  **Related bug this would also fix:** the "`SessionEventScheduling` repeats a real `[ERR]` every
  tick, forever" entry in Bugs above is exactly this gap — cleanup of a cancelled session's stale
  Zoom meeting throws every tick because the team's Zoom credentials were never set. A real
  disabled-vs-unconfigured distinction gives that case somewhere sensible to land.

  **Make it impossible to mistake a muted team's data for real:** show which integrations are off
  wherever the team appears (session list Team column, team dropdown, Admin → Teams), and reuse the
  existing `_TestModeBanner` partial's styling for a per-team variant. Log each suppressed action
  once — not per tick — so the Worker log still shows what *would* have happened.

  **All six open design questions were resolved 2026-07-31** and are folded into the sections above
  — email granularity (one switch), teardown while off (deletes nothing), re-enabling (no backlog),
  the Square inbound webhook (keeps running), the PII purge (keeps running), and who sees the
  control (TeamAdmin + SystemAdmin). Nothing is blocking design here; this is ready to build
  whenever it comes up the list. **Do not "improve" any of the six back the other way** without a
  new decision — several of them look like oversights and aren't (particularly "deletes nothing,"
  which knowingly accepts orphaned Zoom/Discord/Square resources rather than letting a muted team
  reach into a real account for any reason).

- [x] ~~**Every team selector uses the session list's dropdown**~~ (requested 2026-07-30, done same
  day). The last four pages still on the old pills / `<select onchange>` were converted:

  | Page | Was | Now |
  |---|---|---|
  | VE Roster | `<select onchange=submit>` + disabled "Select a team…" | Dropdown **+ All teams**, Team column |
  | Admin → Users | pills incl. an "All teams" pill | Dropdown **+ All teams** |
  | Admin → Team Settings | pills, one per team | Dropdown, **no** All teams |
  | Admin → Email Templates | pills, one per team | Dropdown, **no** All teams |

  "All teams" is deliberately absent from the two admin config pages — each edits *one* team's
  configuration, so a merged view has no meaning; their trigger reads "Select a team…" until one is
  picked, matching the existing "Pick a team above" prompt instead of contradicting it.

  VE Roster needed real work rather than markup: `VolunteerExaminerReportService.GetSessionCountsAsync`
  took a single `int teamId` and now takes the same `IReadOnlyList<int>?` set (null = every team). A
  `VolunteerExaminer` is itself team-scoped, so a merged run yields **one row per VE-per-team** rather
  than silently combining the same person across two teams — hence the new Team column, and a test
  pinning exactly that (same callsign in two teams stays two rows). Converting it also retires the
  `<select>` placeholder hack CLAUDE.md warns about: that disabled "Select a team…" option existed
  only because a null TeamId used to mean "show nothing". Radio buttons have no equivalent trap.

- [x] ~~**User accounts carry a call sign**~~ (requested 2026-07-30, done same day). New nullable
  `User.CallSign` (migration `UserCallSign`, single additive column with a working `Down`), surfaced
  on Admin → Users as a column, a New User field, and a "Set call sign" row action.

  Stored upper-invariant and trimmed, with blank clearing to null rather than `""` — matching
  `VolunteerExaminer.CallSign`'s existing convention so the two are comparable, and so "no call sign"
  has one representation. Normalization lives in one private helper shared by create and update.

  **Deliberately not a foreign key to `VolunteerExaminer`:** a VE row is team-scoped and synced from
  ExamTools, whereas a User is a login that may span several teams or none, so the same person can
  legitimately be one User and several VE rows. Linking the two is a separate question from recording
  the call sign — parked, not decided.

- [ ] **Team Settings: mark "Zoom user ID" as optional** (asked 2026-07-30 — "I didn't need it for
  my Zoom Account"). `Pages/Admin/TeamSettings.cshtml`. The field sits in a row with Account ID /
  Client ID / Client secret, all of which *are* required, and its only hint that it's optional is a
  faint `me` placeholder — so it reads as something the admin forgot to fill in.

  It selects which Zoom user the meetings are created under (substituted into
  `/v2/users/{userId}/meetings`, and the same user's list is what `ListMeetingsAsync` queries for
  cleanup/duplicate detection). `SessionEventSchedulingService` already falls back to
  `team.ZoomUserId ?? "me"`, and `"me"` is Zoom's alias for whoever owns the Server-to-Server OAuth
  app — always correct on a single-license account. It only matters for a multi-user Zoom org where
  sessions should belong to someone other than the credential owner.

  Fix is a one-line caption under the Zoom row, in the same style the breakout-rooms field already
  has: "Leave blank to use the account that owns the API credentials — only needed if your Zoom
  organisation has multiple users and sessions should be hosted by a specific one."

- [x] ~~**Nav menu was cluttered again — regroup it**~~ (requested 2026-07-30, built same day).
  A SystemAdmin saw **9 top-level items** (5 flat links + 3 dropdowns + a flat System Settings), and
  the flat ones were an incoherent mix: the home base, two daily worklists, a periodic report
  (VE Roster), and an exception queue. Regrouped **by domain object** rather than by page type —
  now `Sessions | Applicants ▾ | VEs ▾ | VEC Submission | Unmatched Payments | Settings ▾`
  (**9 → 6**). `Settings ▾` merges the old Team + VEC & Fees + Reports menus *and* the flat System
  Settings link into one menu with four `<hr>`-separated sections (team/people · money/VEC · logs ·
  deployment-wide).

  **`Applicants ▾` and `VEs ▾` deliberately hold one page each today** (Applicant Status, VE Roster)
  — they're structural homes for more applicant-/VE-facing pages already planned, established now so
  the nav doesn't reshuffle under users when those land. Do **not** "fix" them back to flat links.
  `Unmatched Payments` deliberately stays top-level (an exception queue shouldn't hide in a menu).

  Also added pending-count badges on Applicants/VEC Submission/Unmatched Payments, backed by the new
  `NavBadgeCountService` (`src/VeSessionManager.Core/Navigation/`). **Gotcha worth remembering:** its
  `teamIds` parameter follows `SessionAccessScope.GetEffectiveTeamIds` — `null` means *every team*
  (SystemAdmin), an empty list means *no teams*. Inverting that would silently show a SystemAdmin an
  all-zero nav, which is exactly the kind of bug a badge can't self-report; there's a dedicated test
  for it. `VecSubmissionReportService` now delegates its pending-count predicate to the same service
  so the badge and the VEC Submission page can't drift apart. Badges hide entirely at zero rather
  than rendering a "0". See also the TeamLead 403 bug fixed alongside this, in Bugs above.

- [x] ~~**Applicant Status page — surface candidates currently held for FCC Red Light or Basic
  Qualification Question (BQQ) review**~~ (requested 2026-07-30, built and merged same day, PR
  [#53](https://github.com/MikeWills/VeSessionManager/pull/53)). Motivation, from someone with real
  FCC domain expertise: **every** application sits in Red Light status while its $35 fee is unpaid —
  that's normal, not a signal of a problem — the actionable case is an application still Red Light
  *after* payment, meaning something's actually wrong. BQQ/felony-disclosure character review is the
  more common cause of a genuine hold per the user, but both matter.

  Confirmed against FCC's own two reference docs (both blocked to automated fetches the same way
  `wireless2.fcc.gov` is elsewhere in this app; Mike pulled both manually 2026-07-30): `ULS Data File
  Formats` (record layouts) and `uls_code_definitions` (the code-value legend the layout doc doesn't
  include). Initial attempts to find this in `EN.dat`/`CO.dat`/license-file status fields, and a
  same-day guess at `AD.dat`'s Application Status field (values `G`/`2`/`D`/`W`/`R`, confirmed to mean
  Granted/Pending/Dismissed/Withdrawn/Returned — a generic status, not red-light/BQQ-specific), all
  turned out to be the wrong record type. The real signal is `HS.dat` (History, previously unused by
  this app) — `RDLOFF`/`RDLCOM` ("Offlined for Red Light"/"Redlight Review Completed") and
  `BQOFF`/`BQCOM` ("Offlined for Basic Qualification Review"/"...Completed"), each parsed as an
  OFF/COM toggle keyed by Unique System Identifier, walked in the file's own natural order (Log Date
  is day-granularity only, not reliably sortable within a day).

  Shipped: `FccUlsClient` now reads `HS.dat` alongside `HD.dat`/`EN.dat` (lenient — missing entry
  doesn't fail the download); `FccUlsRecordParser.ParseApplications` gained an optional `hsContent`
  param computing both `FccApplicationHoldReason` (None/RedLight/BasicQualification/Both) and, as a
  bonus second signal found in the same file, `FccApplicationPaymentStatus`
  (Unknown/PendingVerification/Paid) from `FVPOFF`/`FVPCNF`/`FVPCOM` ("Offlined for Payment
  Verification"/"Payment Confirmed"/"Payment Verification Completed"). Both new `Candidate` fields
  refresh every `FccUlsWatcherService` run, including for already-`Received` candidates (a hold can be
  placed or cleared after the initial match, not just at match time). Applicant Status page now shows
  "VEC Processing" (not in FCC's system yet) / "Application Received/Processing" / "Held — Red Light"
  / "Held — Basic Qualification" plus a separate Fee column (Paid/Pending/—). Also fixed in the same
  pass: "Days pending" was anchoring `Unmatched` candidates on `DateRegisteredUtc` (days before the
  actual exam) instead of the session date. `Candidate.HasFelonyDisclosure` is kept as-is (a
  pre-FCC-processing heads-up), not replaced — `HS.dat` is the authoritative "is FCC holding this up
  right now" signal, that field is "did the candidate self-report something that might trigger one."

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

- [x] ~~**TeamAdmin (and SystemAdmin) need the ability to delete a session outright**~~ (requested
  2026-07-29, prompted by the orphaned walk-in-candidate rows found while verifying the
  license-class backfill — see `docs/exam-result-license-class.md`; found already built when
  re-checked 2026-07-30, this entry had just never been marked done). Scoped to TeamAdmin/SystemAdmin
  only (`AdminAccessScope.CanManageTeam`, not the regular Session Manager `CanEdit`), per CLAUDE.md's
  "Executing actions with care" guidance for a destructive, hard-to-reverse action.
  `SessionActionService.DeleteAsync` (`src/VeSessionManager.Core/Sessions/SessionActionService.cs`)
  removes, in one transaction and FK-safe order, Payments → Candidates → SessionVolunteerExaminers →
  the Session itself, writes an `AuditLog` entry first, and **blocks** the delete
  (`SessionActionResult.Blocked`) if any of the session's payments are still referenced by an
  unresolved `UnmatchedSquarePayment.MatchedPaymentId` match. `Detail.cshtml(.cs)`'s
  `OnPostDeleteSessionAsync` wires it to a confirmation modal (`#deleteSessionModal`) listing exactly
  how many candidates/payments/VE assignments will be removed, gated behind `CanDeleteSession`. Not
  addressed: whether re-ingestion would recreate the session if still present in ExamTools' feed —
  untested, not confirmed either way.

## Carried over from earlier phases

- [x] ~~Confirm the production ExamTools host~~ (confirmed 2026-07-28) — `alpha.exam.tools` is the real production host (already correctly set in `appsettings.json`), not `exam.tools`. See `docs/examtools-api.md`.
- [ ] Review `DevDataSeeder`'s $15/$7 ARRL fee amounts against the real current fee schedule before this touches real candidates
- [x] ~~Retest payment reminders~~ (flagged in spec.md's Phase 6 section; fixed 2026-07-22, see `docs/payment-reminders.md`'s own "Retest payments" section). The 5-/10-day reminder logic was gated purely on `ApplicationDateEnteredUtc` (`Received`), which a retest `Candidate` never gets — it's permanently `Failed` (terminal) with no FCC application of its own. Both `PaymentReminderService` passes now carry a second branch for `Reason=Retest && ApplicationStatus=Failed`, anchored on `Candidate.ResultMarkedUtc` instead — exactly the spec's own suggested fix ("gate retest reminders on the Session Manager having marked *some* result, not FCC status"). 6 new unit tests (`PaymentReminderServiceTests`), including a regression guard confirming a *Failed candidate's original InitialExam payment* is still correctly excluded — only the `Retest` reason gets the exception.

## Deferred (no urgency, revisit when ready)

- [ ] **Self-update notification for admins** (requested 2026-07-30, low priority — scope
  deliberately not decided yet, revisit when someone has time to design it). The idea: a
  SystemAdmin should see some kind of indicator when a new version is available, especially a
  critical fix, and be able to trigger the update at a time of their choosing rather than
  discovering the app is stale only by accident. Nothing built yet — genuinely just a note that
  this would be useful, not a design. Whoever picks this up should start from the deploy pipeline
  that already exists rather than inventing a new one: `.github/workflows/deploy.yml` only
  triggers on a pushed version tag (`v*.*.*`), and `docs/deployment.md` documents the two-service
  (`vesessionmanager-worker`/`vesessionmanager-web`) systemd topology it deploys to — the simplest
  version of this feature might just be "compare the currently-running build's version against the
  latest GitHub tag, show a banner if behind, let an admin click a button that triggers the
  existing workflow" rather than building a whole separate update mechanism. Open questions this
  TODO deliberately leaves unanswered: how "critical" gets flagged (a tag suffix? a separate
  release-notes field?), whether the trigger comes from GitHub Actions, a webhook, or the app
  polling GitHub itself, and what "take the action" actually does given deploys currently need SSH
  access to a Tailscale-gated server (see CLAUDE.md's Known Constraints).

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
