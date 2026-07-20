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

## Email/SMTP (Phase 4) — not yet live-verified

- [ ] Get Mailgun's domain-specific SMTP username/password (Mailgun dashboard → Sending → Domain settings → SMTP credentials) — see `docs/email-notifications.md`
- [ ] Set `Email:SmtpUsername`, `Email:SmtpPassword` (via `dotnet user-secrets set`, run with `!`)
- [ ] Replace the seeded `EmailSettings` row's placeholder values (`FromAddress`/`FromDisplayName`/`ReplyToAddress`/`PrivacyPolicyUrl` are currently `noreply@example.org` / `https://example.org/privacy`) with real values — edit directly in the DB, see `docs/email-notifications.md`
- [ ] Review/rewrite the seeded `RegistrationConfirmation`/`DayBeforeReminder` template content — it's a real starting example (bullet points, `{{CandidateFirstName}}`, etc.) but the actual wording is a placeholder, not final copy
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

## Multi-Team Foundation — blocking real ExamTools polling

- [ ] **Blocking:** set the seeded `Team` row's `ExamToolsUsername`/`ExamToolsPassword` via direct DB edit — the migration deliberately leaves them `NULL` (migrations must never contain real secrets, even ones already sitting in this repo's user-secrets). ExamTools ingestion is silently skipped (one quiet log line per poll, no error) until this is done. See `docs/multi-team.md`.
- [ ] Rename the seeded `Team.Name` (currently `"WX0MIK"`, copied from the old `ExamTools:Team` appsettings value as a placeholder) to something more human-readable if desired — purely cosmetic, `ExamToolsTeamCode` is the value that actually matters functionally.
- [ ] **Found while cleaning up appsettings.Production.json**: the seeded team's `ExamToolsTeamCode` was copied from the *dev* value (`WX0MIK`, from the base `appsettings.json`) — the real production team code is `HRCC` (was in `appsettings.Production.json`'s now-removed `ExamTools:Team`, per `ExamToolsOptions`' original doc comment: "WX0MIK on dev, HRCC on prod"). If/when this team starts polling the production ExamTools host, set `Team.ExamToolsTeamCode = "HRCC"` instead of `WX0MIK` — don't just re-enter the dev value.
- [ ] Onboard the second team: add a new `Team` row (direct DB edit — no admin UI yet) with its own `Name`/`ExamToolsTeamCode`/`ExamToolsUsername`/`ExamToolsPassword`/Zoom/Discord Guild/Square credentials. `SessionIngestionJob` picks it up automatically on the next tick, no restart needed.
- [x] ~~Fast-follow: apply the same per-team pattern to Zoom/Discord/Square + the Web project's Square webhook route~~ — done. Zoom and Square are fully per-team (each team has its own account); Discord uses one shared bot with a per-team Guild (confirmed with the user — not per-team credentials). Only **Email/SMTP** still uses one shared global account — that's the one remaining fast-follow piece, not yet scoped as its own phase.
- [ ] Live test: with two real `Team` rows configured, confirm `SessionIngestionJob`'s per-team loop correctly ingests both teams' sessions into the one shared `Vec`/`FeeConfiguration` (if both teams work with the same VEC) without cross-team session cancellation false-positives (covered by a unit test, but worth confirming against the real ExamTools API too).

## Carried over from earlier phases

- [ ] Confirm the production ExamTools host — `exam.tools` vs `alpha.exam.tools` (only the dev site, `examtools.dev`, has been exercised so far)
- [ ] Review `DevDataSeeder`'s $15/$7 ARRL fee amounts against the real current fee schedule before this touches real candidates
- [ ] Retest payment reminders (flagged in spec.md's Phase 6 section): the 5-/10-day reminder logic is gated on `ApplicationStatus = Received`, which only happens once a candidate *passes* and their FCC application shows up. A candidate who fails and immediately retests within the same session may owe a fee before there's any FCC application to gate on — so today, a retest payment never gets a reminder/expiration at all. Spec's suggested fix if this turns out to matter in practice: gate retest reminders on the Session Manager having marked *some* result, not FCC status. Revisit once real sessions with retests are running through this.

## Deferred (no urgency, revisit when ready)

- [ ] Deployment: no systemd unit file or working GitHub Actions deploy step exists yet (`.github/workflows/build-and-deploy.yml`'s `deploy` job is a stub) — needs the self-hosted-runner/Tailscale-tailnet setup and a systemd unit, matching the NcsScheduler pattern
- [ ] Zoom: use a meeting template if one exists, instead of (or in addition to) the manually-specified settings `ZoomMeetingRequest` currently sends — Zoom supports creating a meeting from a saved template (`template_id`) so a team's preferred settings (waiting room, recording, etc.) don't need to be hardcoded here. Needs a `Team`-level "which template" setting once this is picked up.
