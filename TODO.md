# TODO

Outstanding testing/configuration items for what's already built (Phases 0–6 of
[`docs/spec.md`](docs/spec.md)). This tracks operational follow-ups, not the phase roadmap itself
— see `docs/spec.md` for what's planned but not yet built.

Reminder: Square, Zoom, Discord, and Email/SMTP are all **optional integrations** — the app runs
fine with any subset of these unconfigured (one quiet log line per poll, no errors), so none of
the items below are blocking further phase work. They're blocking *live end-to-end verification*
of Phases 2–4's actual deliverables.

## Square (Phase 3) — not yet live-verified

- [ ] Create the Square app + get sandbox credentials — see `docs/square-payments.md`'s Account Setup section
- [ ] **Updated by multi-team (see below): these now go on the seeded `Team` row (direct DB edit), not `Square:*` appsettings/user-secrets** — set `Team.SquareAccessToken`/`SquareWebhookSignatureKey`/`SquareLocationId`/`SquareWebhookNotificationUrl`. `Square:Environment` is the only value still in `appsettings.json` (Sandbox/Production, whole-deployment).
- [ ] `SquareWebhookNotificationUrl` must be the *team-specific* URL now: `https://<host>/webhooks/square/1` for the seeded team (route changed from `/webhooks/square` to `/webhooks/square/{teamId}` — see `docs/multi-team.md`).
- [ ] For local testing, tunnel the Web project's webhook endpoint to a public HTTPS URL (e.g. `ngrok http https://localhost:5158`) and register `https://<tunnel-host>/webhooks/square/1` as the Square webhook subscription's notification URL
- [ ] Live test: let the Worker generate a real payment link for a test candidate, pay it with a Square sandbox test card, confirm the webhook flips `Payment.Status` to `Paid`
- [ ] Live test the post-launch unmatched-payment-matching feature (`docs/square-payments.md`'s "Unmatched payments"/"Order completion" sections): pay via a separate Square-hosted page (not one of this app's own generated links) with a buyer email matching exactly one candidate's outstanding Unpaid payment, confirm auto-match; repeat with no matching candidate and confirm it shows up on `/SessionManager/UnmatchedPayments` for manual matching; confirm a Paid order's Square Order actually flips to `COMPLETED` in the dashboard once its session is marked completed.

## Email/SMTP (Phase 4) — not yet live-verified

- [ ] Get Mailgun's domain-specific SMTP username/password (Mailgun dashboard → Sending → Domain settings → SMTP credentials) — see `docs/email-notifications.md`
- [ ] **Updated by multi-team (see below): these now go on the seeded `Team` row (direct DB edit), not `Email:*` user-secrets** — set `Team.SmtpHost`/`SmtpPort`/`SmtpUsername`/`SmtpPassword`/`SmtpUseStartTls`. No column has a baked-in default (deliberate — see CLAUDE.md's `IsConfigured` gotcha), so all five need setting even to match Mailgun's usual `smtp.mailgun.org:587`+STARTTLS defaults.
- [ ] Replace the seeded `EmailSettings` row's placeholder values (`FromAddress`/`FromDisplayName`/`ReplyToAddress`/`PrivacyPolicyUrl` are currently `noreply@example.org` / `https://example.org/privacy`) with real values for **each team's own `EmailSettings` row** (one per team now, not a singleton) — edit directly in the DB, see `docs/email-notifications.md`
- [ ] Review/rewrite the seeded `RegistrationConfirmation`/`DayBeforeReminder` template content — it's a real starting example (bullet points, `{{CandidateFirstName}}`, etc.) but the actual wording is a placeholder, not final copy. Templates are now per-team (`EmailTemplate.TeamId`) so each team can have its own wording if desired.
- [ ] Live test: confirm a test candidate actually receives both emails with correctly substituted placeholders

## FCC ULS Watcher (Phase 5) — not yet live-verified

- [ ] Live test: find (or wait for) a real candidate whose FRN appears in an actual FCC daily application file and confirm `FccDailyWatcherJob` flips them to `Received` with a sane `ApplicationDateEnteredUtc`
- [ ] Live test: confirm the same candidate's eventual license grant flips them to `Granted` with the correct `CallSign`/`LicenseGrantDateUtc`
- [ ] Let `FccWeeklyCatchupJob` actually run on a real Monday at least once and confirm it hits `complete/a_amat.zip`/`complete/l_amat.zip` successfully (these are ~190MB+ files — first real run will validate both the download time and memory footprint of loading them fully into memory, not just the small daily files exercised so far)
- [ ] Revisit the deferred "upgrade exam" (existing licensee) matching logic once real ULS + ExamTools/HamStudy sample data for an upgrade candidate is available — see `docs/fcc-uls-watcher.md`'s Open Item

## Payment Reminders (Phase 6) — not yet live-verified

- [ ] Replace `EmailSettings.AdminNotificationEmail`'s seeded placeholder (`admin@example.org`) with a real inbox — this is where every `PaymentExpirationNotice` goes, so it silently goes nowhere useful until changed
- [ ] Review/rewrite the seeded `PaymentReminder5Day`/`PaymentExpirationNotice` template content — same "real starting example, not final copy" caveat as Phase 4's templates
- [ ] Live test: let a real candidate's Unpaid payment age past 5 days (`Received` status, `ApplicationDateEnteredUtc` from Phase 5) and confirm the reminder actually sends with correct placeholders
- [ ] Live test: let a real candidate's Unpaid payment age past 10 days and confirm `Payment.ExpiredUnpaid` flips and the admin notice arrives at the configured `AdminNotificationEmail`
- [ ] Decide whether `PaymentReminder:UnmatchedReviewWindowDays` (default 5) is the right value once real sessions are running through Phase 1/5 — the spec calls this "some reasonable window," not a fixed number

## Admin Backend Auth (Phase 9a) — not yet live-tested with real accounts

- [ ] Create a Google OAuth app + set `Authentication:Google:ClientId`/`ClientSecret` (user-secrets locally, `Authentication__Google__ClientId`/`Authentication__Google__ClientSecret` env vars in prod) — see `docs/admin-auth.md`
- [ ] Create a Microsoft/Entra app registration + set `Authentication:Microsoft:ClientId`/`ClientSecret` the same way
- [ ] Live test: sign in with a real Google account and a real Microsoft account once credentials are set, confirm the account-linking flow (matches by email to an existing seeded/admin-created `User` row — no self-service registration) works end to end
- [ ] The four dev test users (`sysadmin`/`teamadmin`/`sessionmanager`/`teamlead@example.com`) only exist in Development via `DevAuthSeeder` — Production needs real `User` rows created by hand (direct DB edit, no admin UI yet) until Phase 9c ships user management
- [ ] Decide whether Apple Sign-In is ever worth its $99/year Developer account cost — deliberately deferred in Phase 9a, see `docs/admin-auth.md`
- [ ] Review the password policy set in `Program.cs` (`RequiredLength = 10`, no non-alphanumeric requirement) once real accounts exist — picked as a reasonable default, not something specifically requested

## Multi-Team Foundation — consolidated per-team setup checklist

All four fast-follow stages (Zoom, Discord, Square, Email) are done — every integration except
Discord's bot token is now fully per-team; see `docs/multi-team.md`. **Every credential column
added across all five migrations (ExamTools + the four fast-follow stages) is left `NULL` on the
seeded `Team` row** (migrations must never contain real secrets, even ones already sitting in this
repo's user-secrets) — each integration is silently skipped (one quiet log line per poll, no error)
until its columns are set via direct DB edit (no admin UI yet):

- [ ] **Blocking:** `Team.ExamToolsUsername`/`ExamToolsPassword` — ExamTools ingestion is the one
  hard dependency everything else needs; nothing else runs meaningfully without real sessions.
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
- [ ] **Found while cleaning up appsettings.Production.json**: the seeded team's
  `ExamToolsTeamCode` was copied from the *dev* value (`WX0MIK`, from the base
  `appsettings.json`) — the real production team code is `HRCC` (was in
  `appsettings.Production.json`'s now-removed `ExamTools:Team`, per `ExamToolsOptions`' original
  doc comment: "WX0MIK on dev, HRCC on prod"). If/when this team starts polling the production
  ExamTools host, set `Team.ExamToolsTeamCode = "HRCC"` instead of `WX0MIK` — don't just re-enter
  the dev value.
- [ ] Onboard the second team: add a new `Team` row (direct DB edit — no admin UI yet) with its
  own `Name`/`ExamToolsTeamCode`/`ExamToolsUsername`/`ExamToolsPassword`/Zoom/Square/Email
  credentials and its own `DiscordGuildId` (the shared bot needs to be invited into that team's
  Discord server first). Every job picks it up automatically on the next tick, no restart needed.
- [ ] Live test: with two real `Team` rows configured, confirm every per-team job loop (ingestion,
  Zoom/Discord scheduling, Square payment generation + webhook routing, registration/reminder
  emails) correctly isolates both teams' data — ingestion into the one shared `Vec`/
  `FeeConfiguration` without cross-team session cancellation false-positives, Square webhooks
  routing to the right team via `/webhooks/square/{teamId}`, emails using each team's own SMTP
  credentials and template wording — all covered by unit tests, but worth confirming against the
  real APIs too.

## Bugs / known issues

- [x] ~~**Duplicate Discord scheduled events**~~ — found ~6 duplicate events in the Discord server (reported 2026-07-21). Root cause and code fix landed 2026-07-21: `IDiscordEventClient` gained `ListEventsAsync`; `SessionEventSchedulingService.SyncZoomAndDiscordAsync` now checks for an existing guild event matching the session by name + start time (within a minute) before calling `CreateEventAsync`, adopting its id instead of creating a duplicate if found — covered by `NewSession_MatchingEventAlreadyExistsInGuild_AdoptsIt_DoesNotCreateDuplicate` in `SessionEventSchedulingServiceTests`. **Still outstanding — needs a human with Discord access:** the ~6 already-existing duplicate events in the real Discord server still need manually deleting; this fix only prevents new duplicates going forward, it doesn't clean up past ones.
  - **Same-day follow-up self-audit (2026-07-21) found the identical unfixed bug class in two more places, both now fixed:** Zoom meeting creation (same `SyncZoomAndDiscordAsync` method — added `IZoomClient.ListMeetingsAsync`, same name/time dedup pattern, see `NewSession_MatchingMeetingAlreadyExistsInZoom_AdoptsIt_DoesNotCreateDuplicate`) and Square payment link generation (`PaymentGenerationService.GenerateLinkAsync` — added `Payment.SquareIdempotencyKey`, persisted before calling Square and reused on retry so Square's own idempotency guarantee prevents the duplicate, migration `Phase9dPaymentSquareIdempotencyKey`). No live duplicates found for either yet (unlike Discord's confirmed ~6) — these were caught proactively, not from a reported incident.

- [x] ~~**Remove "Add walk-in candidate" — redundant with ExamTools**~~ (reported 2026-07-21, removed 2026-07-21). Walk-in registration is already handled by ExamTools itself, so Phase 9b's own walk-in action was unnecessary — a walk-in registered directly in ExamTools already flows in through the normal `SessionIngestionService` polling, same as any other candidate. Removed `CandidateActionService.AddWalkInAsync`, its `OnPostAddWalkInAsync` handler and modal/button in `Pages/SessionManager/Detail.cshtml(.cs)`, its test coverage in `CandidateActionServiceTests`, and the corresponding spec.md Session Manager bullet-list entry.

- [x] ~~**Remove "Move candidate to a different session" — redundant with ExamTools**~~ (reported 2026-07-21, removed 2026-07-21). Same reasoning as the walk-in item above: moving a candidate between sessions is already handled in ExamTools itself, so a move made there is already reflected the next time `SessionIngestionService` polls. Removed `CandidateActionService.MoveAsync`/`CandidateMoveResult`, its `OnPostMoveAsync` handler, the `CanMove`/`MoveTargetSessions` UI plumbing and modal/menu-item in `Pages/SessionManager/Detail.cshtml(.cs)`, its test coverage in `CandidateActionServiceTests`, and the corresponding spec.md Session Manager bullet-list entry.

- [x] ~~**TeamLead has no real view yet**~~ (found during Phase 9d's self-audit against 9a-9c, 2026-07-21; fixed 2026-07-22). Added `TeamLead` to `[Authorize]` on `Pages/SessionManager/Index.cshtml.cs`/`Detail.cshtml.cs`/`VeRoster.cshtml.cs`/`VecSubmission.cshtml.cs`, added a new `SessionAccessScope.CanView` (view-only, unlike `CanEdit` doesn't carve out TeamLead) to gate page *display* separately from the existing `CanEdit` write-gate, and hid every write control (buttons/forms/kebab menu/modals) in `Detail.cshtml`/`VecSubmission.cshtml` behind a `CanEdit` flag exposed from the page model. `RoleLandingPages` now sends TeamLead to `/SessionManager/Index` like every other role; the `Pages/TeamLead/Index.cshtml` placeholder was removed. **Found and fixed a second, previously-latent bug in the same change:** `SessionAccessScope.GetEffectiveTeamId`'s TeamLead branch needs `User.ManagedByUser` eager-loaded, but `UserManager.GetUserAsync(ClaimsPrincipal)` (used everywhere) never loads it — since no page had ever exercised the TeamLead path before, this had never been caught; a TeamLead would have signed in successfully and silently seen zero sessions. Fixed with a new `CurrentUserLoader.GetUserWithManagerAsync` extension (`Web/CurrentUserLoader.cs`) that all four pages now use instead of the bare `userManager.GetUserAsync`. Live-verified in a real browser: `teamlead@example.com` lands on Sessions, sees only their assigned team's sessions/roster/submission status with zero write controls anywhere; `sessionmanager@example.com` confirmed unaffected (full edit access still present) as a regression check.

- [ ] **No real fix yet for youth-rate underpayments getting auto-matched** (found during a security review of the Square unmatched-payment-matching feature, 2026-07-21/22, see `docs/square-payments.md`'s "Amount isn't validated against what's owed" note). `Payment.Amount` is always created at ARRL's $15 standard rate — youth status isn't known until confirmed at test-day check-in — but the separate Square-hosted checkout page lets anyone pay the $5 youth rate instead, and `SquarePaymentMatchingService` auto-matches by buyer email with no amount check, so it can't tell a legitimate youth payment from an outright underpayment. Current stopgap (landed, not a fix): the `Payment` still gets marked `Paid` either way, but `AmountMismatchFlaggedUtc`/`SquareAmountPaidUsd` are set and surfaced as a tag on the session detail page for a Session Manager to manually follow up on — this still relies on a human noticing and chasing down the balance if youth status doesn't hold up at test time. Mike's exploring having the registration/payment email include a link to pay the correct fee once youth status is known (rather than the flat $5/$15 buttons on the separate page), but hasn't decided on an approach yet — pending discussion with the rest of the team. Revisit once that's decided; the flag/tag mechanism may end up replaced entirely rather than extended.

- [x] ~~**Home page (`/`) had no styling or navigation**~~ (reported 2026-07-22, fixed and live-verified same day). `Pages/Index.cshtml` was still the untouched scaffold-default Bootstrap page — no app styling, no working navigation — since nothing had ever restyled it (the same class of gap Phase 9's polish pass already fixed for `Login`/`AccessDenied`/`Logout`/`ExternalLoginCallback`). Now uses `_PublicLayout` (matches Login/Privacy) with a "Log in" link, and `IndexModel.OnGetAsync` redirects an already-signed-in visitor straight to `RoleLandingPages.GetPath(user.Role)` instead of showing them a dead end. Live-verified in a real browser: anonymous visitor sees the styled card with working Log in/Privacy links; SystemAdmin, SessionManager, and TeamLead (the one role with a distinct landing page) all redirect correctly when already signed in.

- [x] ~~**No way to log out from within the app itself**~~ (found 2026-07-22 while browser-verifying the home page fix above, fixed same day). `_AppLayout` — the layout every SessionManager/Admin page actually uses — never included `_LoginPartial` (which has the real POST-form Log out button), so the only working logout control left anywhere in the app was on the near-vestigial scaffold `/Error` page. Fixed by adding a `.user-menu` kebab dropdown (same component `.help-menu` already uses) wrapping the existing role/team display in `_AppLayout`'s header, with a `Log out` POST form inside — live-verified: opens the dropdown, submits the logout POST, redirects to the now-styled `/`.

- [x] ~~**`claude-review` GitHub Action errors out on larger PRs instead of completing**~~ (found 2026-07-22 on PR #6; fixed 2026-07-22). `.github/workflows/claude-code-review.yml` ran the `code-review` plugin's `/code-review:code-review` command with no explicit tool-permission configuration for the sandboxed run — on a PR of any real size, the review agent needed things like `dotnet build`/`dotnet test` to review meaningfully, got denied every time (`permission_denials_count: 46` on PR #6's run), and the whole run reported `is_error: true` and failed — not because it found real issues, just an infra/config gap. `build-and-test` (the real quality gate) was unaffected. Fixed by adding `claude_args: --allowedTools "Bash(dotnet build *)" "Bash(dotnet test *)" "Bash(dotnet restore *)" "Bash(git diff *)" "Bash(git log *)" "Bash(git show *)"` — permission rule syntax (space before the wildcard) confirmed against this repo's own real `.claude/settings.local.json`, not guessed. Not blocking merges today (no branch protection on this private/free repo), but the check should now actually complete instead of erroring out — worth watching the next PR's run to confirm.

## Carried over from earlier phases

- [ ] Confirm the production ExamTools host — `exam.tools` vs `alpha.exam.tools` (only the dev site, `examtools.dev`, has been exercised so far)
- [ ] Review `DevDataSeeder`'s $15/$7 ARRL fee amounts against the real current fee schedule before this touches real candidates
- [ ] Retest payment reminders (flagged in spec.md's Phase 6 section): the 5-/10-day reminder logic is gated on `ApplicationStatus = Received`, which only happens once a candidate *passes* and their FCC application shows up. A candidate who fails and immediately retests within the same session may owe a fee before there's any FCC application to gate on — so today, a retest payment never gets a reminder/expiration at all. Spec's suggested fix if this turns out to matter in practice: gate retest reminders on the Session Manager having marked *some* result, not FCC status. Revisit once real sessions with retests are running through this.

## Deferred (no urgency, revisit when ready)

- [ ] Deployment: no systemd unit file or working GitHub Actions deploy step exists yet (`.github/workflows/build-and-deploy.yml`'s `deploy` job is a stub) — needs the self-hosted-runner/Tailscale-tailnet setup and a systemd unit, matching the NcsScheduler pattern
- [ ] Zoom: use a meeting template if one exists, instead of (or in addition to) the manually-specified settings `ZoomMeetingRequest` currently sends — Zoom supports creating a meeting from a saved template (`template_id`) so a team's preferred settings (waiting room, recording, etc.) don't need to be hardcoded here. Needs a `Team`-level "which template" setting once this is picked up.
- [ ] **Audit the candidate email flow** (requested 2026-07-23, alongside the "Refresh candidates" button replacing the surge-poll — see CLAUDE.md): confirm how many emails a candidate actually receives and when, across registration confirmation, day-before reminder, and payment reminder/expiration paths — especially now that a Session Manager can trigger a registration-confirmation send on demand via the new button, not just once per background poll. Refine/consolidate the flow once actual send counts/timing are known, rather than assuming the current per-path `...SentUtc` guards already produce a sane candidate-facing cadence.
