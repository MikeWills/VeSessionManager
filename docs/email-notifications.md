# Candidate Notification Emails (Phase 4)

> **See `docs/email-reference.md` for the current, complete picture** — every template (including
> the ones added after this doc was written: `PaymentReminder5Day`, `PaymentExpirationNotice`,
> `FelonyDisclosureInstructions`, `ArrlYouthProgramInstructions`), every placeholder tag, and the
> full send pipeline including the "Refresh candidates" manual trigger. This doc's SMTP-config
> section below also predates the multi-team change — `Email:*` is no longer read from
> appsettings/user-secrets, it's per-`Team` DB fields now (see `docs/multi-team.md`). Kept here for
> its still-accurate Phase 4 implementation notes (Mailgun setup, editing templates via SQLite).

What `CandidateNotificationService`, `EmailTemplateRenderer`, and `SmtpEmailSender`
(`VeSessionManager.Core/{Notifications,Email}/`) rely on, and — since this is the one integration
in the app that's meant to be **hand-authored by a human**, not generic content — how to actually
edit what gets sent, today, before Phase 9's admin UI exists to do it visually.

**Email is optional**, same as Zoom/Discord/Square: `IEmailSender.IsConfigured` (true once both
`Email:SmtpHost` and `Email:SmtpUsername` are set) gates whether `CandidateNotificationService`
even attempts to send. Candidates' send-tracking fields stay null while unconfigured, so nothing
is lost — the very next poll sends everything backlogged automatically once SMTP is set up.

## Editing template content right now (no admin UI needed)

Templates live in the `EmailTemplates` table — `Subject` and `Body` are plain columns holding raw
HTML. `Body` is sent as the email's HTML body verbatim, so anything an email client renders
(bold, headings, `<ul><li>` bullet lists, `<a href>` links, tables) just works — this is not
AI-generated at send time, it's exactly what's sitting in that row.

To edit today: open `vesessionmanager.db` with any SQLite browser (e.g.
[DB Browser for SQLite](https://sqlitebrowser.org/)) and edit the `Subject`/`Body` columns on the
`RegistrationConfirmation` or `DayBeforeReminder` row directly, or run a one-off `UPDATE`. The
seeded content (`EmailDefaultsSeeder`) is only ever inserted if a row for that `Key` doesn't
already exist — it will never overwrite your edits, on this deploy or any future one.

The **From/Reply-To addresses and the privacy policy link** are a separate singleton row in
`EmailSettings` (always `Id = 1`) — same story: seeded once with placeholder values on first run,
never overwritten after. Update `FromAddress`, `FromDisplayName`, `ReplyToAddress`, and
`PrivacyPolicyUrl` there before sending anything real; the seeded values are literally
`noreply@example.org` / `https://example.org/privacy` and will look wrong if left as-is.

## Available placeholders

| Template Key | Placeholders |
|---|---|
| `RegistrationConfirmation` | `{{CandidateFirstName}}`, `{{CandidateName}}` (full name), `{{SessionDate}}`, `{{ZoomJoinUrl}}`, `{{PaymentLinkUrl}}` (blank if the VEC doesn't collect a fee, or if Square hasn't generated one yet), `{{PrivacyPolicyUrl}}` |
| `DayBeforeReminder` | `{{CandidateFirstName}}`, `{{CandidateName}}`, `{{SessionDate}}`, `{{ZoomJoinUrl}}`, `{{OutstandingPaymentLinkUrl}}` (blank if nothing's unpaid) |

A placeholder your dictionary doesn't provide a value for (almost always a typo, e.g.
`{{CandidateFistName}}`) is left as that literal text in the sent email and logged as a
`WARNING` — deliberately not silently dropped, so a broken template is visibly broken rather than
mailing out a mysteriously missing word. A placeholder that *is* provided but with an
intentionally empty value (e.g. no outstanding payment) substitutes cleanly to nothing, no
warning.

`SessionDate` is rendered as e.g. `Friday, July 24, 2026 at 5:00 PM UTC` — always UTC, since
there's no per-session timezone anywhere in the data model and this app's audience may not share
one time zone (remote/Zoom sessions).

## Two trigger points

- **RegistrationConfirmation** fires as part of the same ~5-minute poll tick as ingestion (Phase
  1) → scheduling (Phase 2) → payment generation (Phase 3), deliberately last in that order, so
  by the time it renders, the session's Zoom link and (if applicable) the candidate's payment
  link have had their best chance to already exist.
- **DayBeforeReminder** is a separate daily job (`DayBeforeReminderJob`, 24-hour
  `PeriodicTimer` from Worker startup — not pinned to a specific wall-clock time). Finds
  candidates whose session's `ScheduledStartUtc` falls on tomorrow's UTC calendar date. Both are
  idempotent: `Candidate.RegistrationConfirmationSentUtc` / `DayBeforeReminderSentUtc` are only
  set after a real send succeeds, so a crash mid-run, an unconfigured SMTP server, or a job
  restarting the same day never produces a duplicate send.

## SMTP (Mailgun)

Client: `SmtpEmailSender`, wrapping [MailKit](https://github.com/jstedfast/MailKit)'s
`SmtpClient`. Connects fresh per send (no persistent connection) — this app's volume never
justifies the complexity of connection pooling/keep-alive.

Mailgun specifics ([docs](https://documentation.mailgun.com/docs/mailgun/user-manual/sending-messages/send-smtp)):
- Host `smtp.mailgun.org` (US) or `smtp.eu.mailgun.org` (EU region domains), port `587` with
  STARTTLS (Mailgun's recommended combination — port 465 SSL-on-connect and 2525 also work).
- Credentials are **per sending domain**, not your Mailgun account login — found (or reset) in
  the Mailgun dashboard under **Sending → Domain settings → SMTP credentials**. Default username
  is `postmaster@yourdomain.com`. The password is shown only once at creation/reset time.
- The `From` address (`EmailSettings.FromAddress`) must be on your verified Mailgun sending domain.

## SMTP settings reference

| Setting | Where | Notes |
|---|---|---|
| `Email:SmtpHost` | appsettings.json | `smtp.mailgun.org` by default |
| `Email:SmtpPort` | appsettings.json | `587` |
| `Email:UseStartTls` | appsettings.json | `true` |
| `Email:SmtpUsername` | user-secrets | Mailgun's domain-specific SMTP username |
| `Email:SmtpPassword` | user-secrets | Mailgun's domain-specific SMTP password |
