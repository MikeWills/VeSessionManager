# TODO

Outstanding testing/configuration items for what's already built (Phases 0–5 of
[`docs/spec.md`](docs/spec.md)). This tracks operational follow-ups, not the phase roadmap itself
— see `docs/spec.md` for what's planned but not yet built.

Reminder: Square, Zoom, Discord, and Email/SMTP are all **optional integrations** — the app runs
fine with any subset of these unconfigured (one quiet log line per poll, no errors), so none of
the items below are blocking further phase work. They're blocking *live end-to-end verification*
of Phases 2–4's actual deliverables.

## Square (Phase 3) — not yet live-verified

- [ ] Create the Square app + get sandbox credentials — see `docs/square-payments.md`'s Account Setup section
- [ ] Set `Square:AccessToken`, `Square:WebhookSignatureKey` (via `dotnet user-secrets set`, run with `!` so the value stays local)
- [ ] Set `Square:LocationId`, `Square:WebhookNotificationUrl` in `appsettings.json`
- [ ] For local testing, tunnel the Web project's webhook endpoint to a public HTTPS URL (e.g. `ngrok http https://localhost:5158`) and register that URL as the Square webhook subscription's notification URL
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

## Carried over from earlier phases

- [ ] Confirm the production ExamTools host — `exam.tools` vs `alpha.exam.tools` (only the dev site, `examtools.dev`, has been exercised so far)
- [ ] Review `DevDataSeeder`'s $15/$7 ARRL fee amounts against the real current fee schedule before this touches real candidates
- [ ] Multi-team support (raised 2026-07-20): if this app ever needs to serve more than one independent VE team, each with its own Discord/Square/Zoom account, those three clients need reworking from a single global-credential singleton to per-`Vec` credential resolution. The FCC watcher already needs no changes for this (its matching is inherently team-agnostic). Not scoped as a phase yet — revisit if/when a second team is actually onboarding.

## Deferred (no urgency, revisit when ready)

- [ ] Deployment: no systemd unit file or working GitHub Actions deploy step exists yet (`.github/workflows/build-and-deploy.yml`'s `deploy` job is a stub) — needs the self-hosted-runner/Tailscale-tailnet setup and a systemd unit, matching the NcsScheduler pattern
