# TODO

Outstanding testing/configuration items for what's already built. This tracks operational
follow-ups and untriaged requests, not the phase roadmap itself — see [`docs/spec.md`](docs/spec.md)
for what's planned but not yet built.

**Completed items are not kept here.** They move to `CHANGELOG.md` as a one-line pointer (design
rationale lives in the linked `/docs/*.md` file), the same policy CLAUDE.md's own Change Log
follows. Anything closed before 2026-07-31 was pruned out in that pass — git history has the full
text if an old entry's detail is ever needed.

Reminder: Square, Zoom, Discord, and Email/SMTP are all **optional integrations** — the app runs
fine with any subset unconfigured (one quiet log line per poll, no errors), so none of the items
below block further phase work. They block *live end-to-end verification* of those integrations.

## Square (Phase 3) — partially live-verified

The core flow is verified end to end (see `CHANGELOG.md`); what's left is the post-launch matching
feature.

- [ ] Live test the post-launch unmatched-payment-matching feature (`docs/square-payments.md`'s
  "Unmatched payments"/"Order completion" sections): pay via a separate Square-hosted page (not one of
  this app's own generated links) with a buyer email matching exactly one candidate's outstanding
  Unpaid payment, confirm auto-match; repeat with no matching candidate and confirm it shows up on
  `/SessionManager/UnmatchedPayments` for manual matching; confirm a Paid order's Square Order actually
  flips to `COMPLETED` in the dashboard once its session is marked completed.

## Email/SMTP (Phase 4) — not yet live-verified

**No team has SMTP configured yet**, so nothing in this app has ever sent a real email. As of
2026-08-01 that now also blocks **password reset**, which sends from the new deployment-wide
Admin -> System Settings -> System Email sender (see `docs/password-reset.md`) rather than a team's
SMTP. Until that is filled in, the forgot-password page tells the user reset isn't set up. Verifying
one real reset end to end with a throwaway account is the first thing to do once any SMTP works.

- [ ] Get Mailgun's domain-specific SMTP username/password (Mailgun dashboard → Sending → Domain
  settings → SMTP credentials) — see `docs/email-notifications.md`
- [ ] Set `Team.SmtpHost`/`SmtpPort`/`SmtpUsername`/`SmtpPassword`/`SmtpUseStartTls` per team (direct
  DB edit or Team Settings UI, not `Email:*` user-secrets). No column has a baked-in default
  (deliberate — see CLAUDE.md's `IsConfigured` gotcha), so all five need setting even to match
  Mailgun's usual `smtp.mailgun.org:587` + STARTTLS defaults.
- [ ] Replace each team's `EmailSettings` row placeholders (`FromAddress`/`FromDisplayName`/
  `ReplyToAddress`/`PrivacyPolicyUrl`) with real values — one row per team now, not a singleton; see
  `docs/email-notifications.md`. **Partially done (checked 2026-07-29):** Team 2 (MARC) has a real
  `FromAddress`, but `PrivacyPolicyUrl` is still the literal `https://example.org/privacy` for **all
  three teams, including MARC**.
- [ ] Review/rewrite the seeded `RegistrationConfirmation`/`DayBeforeReminder` template content — a
  real starting example, but the wording is placeholder, not final copy. Templates are per-team
  (`EmailTemplate.TeamId`) so each team can differ.
- [ ] Live test: confirm a test candidate actually receives both emails with correctly substituted
  placeholders

## Payment Reminders (Phase 6) — not yet live-verified

Blocked on the Email/SMTP section above — none of these can be tested until a team can send mail.

- [ ] Replace `EmailSettings.AdminNotificationEmail`'s seeded placeholder (`admin@example.org`) with a
  real inbox — every `PaymentExpirationNotice` goes there, so it silently goes nowhere useful until
  changed. **Partially done (checked 2026-07-29):** Teams 1 (WX0MIK) and 2 (MARC) have a real inbox;
  Team 3 (HRCC) is still the placeholder.
- [ ] Review/rewrite the seeded `PaymentReminder5Day`/`PaymentExpirationNotice` template content —
  same "real starting example, not final copy" caveat as Phase 4's templates
- [ ] Live test: let a real candidate's Unpaid payment age past 5 days and confirm the reminder sends
  with correct placeholders
- [ ] Live test: let a real candidate's Unpaid payment age past 10 days and confirm
  `Payment.ExpiredUnpaid` flips and the admin notice arrives at `AdminNotificationEmail`
- [ ] Decide whether `PaymentReminder:UnmatchedReviewWindowDays` (default 5) is the right value once
  real sessions are running — the spec calls this "some reasonable window," not a fixed number

## Admin Backend Auth (Phase 9a) — not yet live-tested with real accounts

Both providers are already wired in `Program.cs` and `ExternalLoginCallback` handles each one's
email-verification correctly — **this is configuration, not development.** Note the client secrets
are *app-level*, not per-`Team` DB columns, so they need `Authentication__*` env vars in the systemd
units; and OAuth needs HTTPS, so the vhost/cert work under "Deployment — beta server" below comes
first.

- [ ] Create a Google OAuth app + set `Authentication:Google:ClientId`/`ClientSecret` (user-secrets
  locally, `Authentication__Google__ClientId`/`ClientSecret` env vars in prod) — see
  `docs/admin-auth.md`
- [ ] Create a Microsoft/Entra app registration + set `Authentication:Microsoft:ClientId`/`ClientSecret`
  the same way
- [ ] Live test: sign in with a real Google account and a real Microsoft account once credentials are
  set, confirm the account-linking flow (matches by email to an existing admin-created `User` row — no
  self-service registration) works end to end
- [ ] **Create the first production SystemAdmin user** — `DevAuthSeeder` never runs outside
  Development, so prod's `AspNetUsers` table starts empty. Needs a one-off script/direct DB insert with
  a real `PasswordHash` (`PasswordHasher<User>`), or a hand-inserted `User` row (`Role = SystemAdmin`)
  linked via `ExternalLoginCallback`'s email-match logic. **Blocking: nobody can sign into prod at all
  until this exists.**
- [ ] The four dev test users (`sysadmin`/`teamadmin`/`sessionmanager`/`teamlead@example.com`) only
  exist in Development via `DevAuthSeeder` — Production needs real `User` rows created through the
  Phase 9c admin UI or by hand.

## Bugs / known issues

None currently open.

## Feature requests

**Feature requests now live in GitHub issues, not here** (moved 2026-07-31) — that's where new ones
should be filed, so they can be tracked, labelled, and closed by a PR. Listed here only as pointers:

- [ ] [#63 — Stats page: VEC testing volume and applicants](https://github.com/MikeWills/VeSessionManager/issues/63)
  — **v1 design sketch added 2026-08-01** (see the issue comment), parked rather than started. Key
  constraint: **aggregate-only, no per-VE session counts** — the VE Roster page was restricted to
  admin roles the same day precisely because it exposes a per-person leaderboard, and keeping stats
  aggregate is what allows the page to stay visible to non-admin roles. Three questions still open
  (charting library, exact audience, per-VEC breakdown).
- [ ] [#64 — Per-team, per-integration enable/disable switches](https://github.com/MikeWills/VeSessionManager/issues/64)
  — **design complete**, all six open questions resolved 2026-07-31; ready to build. The issue carries
  the full decision record, including the "unconfigured ≠ disabled" problem that is the hard part.

## Documentation

- [ ] [#65 — User-facing documentation needs to be started](https://github.com/MikeWills/VeSessionManager/issues/65)
  — no guide yet for a Session Manager or TeamAdmin, only developer/design docs. Also covers the
  missing `ARCHITECTURE.md` and `SECURITY.md`, both named in CLAUDE.md's Documentation Structure
  table but never created.

## Deployment — beta server

The pipeline exists and works; what's left is one-time operational setup that has never been run
against a real server. See `docs/deployment.md` for step-by-step instructions for both.

- [ ] **Provision the public domain.** `ve.wx0mik.radio` was decided 2026-07-22 (see
  `docs/deployment.md`'s "Apache Virtual Host" section) but the Apache vhost + Let's Encrypt cert
  haven't been set up on the real server. **Google/Microsoft SSO depends on this** — OAuth requires an
  HTTPS redirect URI for anything that isn't `localhost`, and the auth cookie pins
  `CookieSecurePolicy.Always` outside Development. A second domain for a second team is possible later
  but not needed — purely cosmetic branding, no code/deploy change either way.
- [ ] **Run the one-time server-side setup**: `vesessionmanager` service account, sudoers file,
  app/data directories, and the 5 GitHub repo secrets. Operational work, not code.
- [ ] **Back up the Data Protection key ring** as part of that setup. `Team`'s credential columns are
  encrypted at rest against it — if the key ring is lost, every stored credential is permanently
  unrecoverable, so it needs the same backup discipline as the DB file itself. Web and Worker must
  also agree on application name *and* key ring path; drift doesn't throw, it silently reads as
  "never migrated" (see CLAUDE.md's Known Constraints).

## Deferred (no urgency, revisit when ready)

- [ ] **Self-update notification for admins** (requested 2026-07-30; **re-scoped 2026-07-31, design
  explicitly deferred** — Mike: *"get past a solid beta first."* Do not design or build this until
  beta has proven solid and Mike raises it).

  The idea: a SystemAdmin should see an indicator when a new version is available, especially a
  critical fix, and be able to trigger the update at a time of their choosing rather than discovering
  the app is stale by accident.

  **Important correction to this item's original framing (2026-07-31):** it previously assumed the
  update could "trigger the existing workflow." That does not work for the real target. The production
  HRCC server is **not attached to any pipeline and Mike does not control it** — it's effectively a
  third-party install shipped *to*, not an environment deployed *at*. So this has to **pull**: the app
  fetches a release and updates itself, with no inbound SSH, no runner, and no Tailscale. The
  tag-triggered `deploy.yml` only ever targets Mike's own beta server (and that trigger may move to
  merge-on-main, reserving tags/releases for this mechanism).

  Consequences to work through whenever it is picked up, none of them decided:
  - `deploy.yml` builds on the runner and rsyncs — **nothing is ever published**. A pull-based updater
    needs a versioned, self-contained artifact attached to the GitHub release. That's a new job.
  - **The repo is private on a free plan**, so a release asset can't be fetched anonymously. Either
    issue and rotate a token, or publish artifacts somewhere public. A decision, not a detail.
  - Both Web and Worker call `Database.Migrate()` at startup, so a self-update applies migrations
    **unattended on a machine nobody can reach**, with no down-migration path. Backup-before-migrate
    almost certainly belongs inside the updater.
  - If the updater ever replaces the app directory, the Data Protection key ring must already live
    outside it (as the DB already does) or every encrypted credential dies with the update.
  - Still undecided from the original note: how "critical" gets flagged, and whether the trigger is
    the app polling GitHub or something pushed to it.
