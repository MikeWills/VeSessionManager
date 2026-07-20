# TODO

Outstanding testing/configuration items for what's already built (Phases 0–3 of
[`docs/spec.md`](docs/spec.md)). This tracks operational follow-ups, not the phase roadmap itself
— see `docs/spec.md` for what's planned but not yet built.

## Square (Phase 3) — not yet live-verified

- [ ] Create the Square app + get sandbox credentials — see `docs/square-payments.md`'s Account Setup section
- [ ] Set `Square:AccessToken`, `Square:WebhookSignatureKey` (via `dotnet user-secrets set`, run with `!` so the value stays local)
- [ ] Set `Square:LocationId`, `Square:WebhookNotificationUrl` in `appsettings.json`
- [ ] For local testing, tunnel the Web project's webhook endpoint to a public HTTPS URL (e.g. `ngrok http https://localhost:5158`) and register that URL as the Square webhook subscription's notification URL
- [ ] Live test: let the Worker generate a real payment link for a test candidate, pay it with a Square sandbox test card, confirm the webhook flips `Payment.Status` to `Paid`

## Carried over from earlier phases

- [ ] Confirm the production ExamTools host — `exam.tools` vs `alpha.exam.tools` (only the dev site, `examtools.dev`, has been exercised so far)
- [ ] Review `DevDataSeeder`'s $15/$7 ARRL fee amounts against the real current fee schedule before this touches real candidates

## Deferred (no urgency, revisit when ready)

- [ ] Deployment: no systemd unit file or working GitHub Actions deploy step exists yet (`.github/workflows/build-and-deploy.yml`'s `deploy` job is a stub) — needs the self-hosted-runner/Tailscale-tailnet setup and a systemd unit, matching the NcsScheduler pattern
