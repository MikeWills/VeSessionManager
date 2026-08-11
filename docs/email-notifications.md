# Candidate Notification Emails (Phase 4)

> **See `docs/email-reference.md` for the current, complete picture** — every template (including
> the ones added after this doc was written: `FccFeeReminder5Day`, `PaymentExpirationNotice`,
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

### `SessionDate` gives Eastern and Pacific (2026-08-10, supersedes UTC)

Rendered as e.g. `Saturday, August 15, 2026 at 10:00 AM ET / 7:00 AM PT`, by
`SessionTimeFormatter.ForCandidate` in Core.

**It used to be UTC, and that was a decision rather than an oversight** — this document previously
recorded the reasoning: there is no per-session timezone in the data model, and a remote session's
audience may not share one zone, so UTC was chosen as the neutral option. The flaw is that neutral
is not the same as *useful*. Every screen in the app shows Eastern with an "ET" suffix, so the one
surface speaking to a member of the public was the only one speaking a zone almost none of them
use — and a candidate who reads "2:00 PM" as local time misses their exam by hours, in a way that
looks like their own mistake.

**Two zones rather than one**, per Mike: Eastern and Pacific are the outer edges of the contiguous
US, and the gap between them is always exactly three hours (both observe DST and switch on the same
dates), so a reader in Central or Mountain can place themselves without being told. That answers the
original "may not share one zone" concern better than UTC did, rather than ignoring it.

Two details worth keeping:

- **When the zones fall on different calendar days** — any start before 3:00 AM Eastern — the
  Pacific side carries its own date instead of inheriting Eastern's. No real session runs then, but
  one date printed beside two times would be quietly wrong for every Pacific reader if one ever did.
- **`EasternTimeFormatter` lives in the Web project and must not be used for email.**
  `SessionTimeFormatter` is the Core one. That project boundary is why the UTC spelling was easy to
  leave in place for so long: the shared formatter simply was not reachable from where emails are
  built.

**How this survived so long is worth remembering:** the notification tests use `{{SessionDate}}` in
template subjects and never asserted what it rendered to. The whole suite passed while every
candidate email carried the wrong timezone — the tests agreed with the bug by not looking at it.
`SessionTimeFormatterTests` now asserts the rendered string, in summer and winter (a fixed offset
passes one and fails the other).

## Watching what actually goes out: the per-team BCC (2026-08-10)

`EmailSettings.BccAddress`, set on Admin → Team Settings. When present, every **candidate-facing**
email that team sends is blind-copied there, so someone can see what the app really sends instead of
waiting for a candidate to report that something looked wrong. Issue #207.

It exists because #205 — candidate email giving the session time in UTC — survived for months
precisely because nobody sees outgoing mail.

**⚠️ It deliberately does not apply to every send.** Three of the seven `IEmailSender.SendAsync`
call sites carry access tokens:

| Sender | Carries |
|---|---|
| `PasswordResetService` | a password reset token |
| `VeSelfServiceLinkService` | a self-service link — the app's only unauthenticated route to personal data |
| `VeEmailChangeService` | an email-change confirmation token |

A copy of any of those in a shared monitoring inbox is an account-takeover path, not a convenience.

**The rule is enforced by which call sites populate `EmailMessage.BccAddress`, not by a runtime
flag**, so it cannot be switched on for the wrong sender by mistake. `CandidateEmailBccTests` reads
those three source files and fails if `BccAddress` appears in any of them — source inspection rather
than behaviour, because the case actually worth guarding is someone "finishing the job" later by
wiring BCC into the remaining senders, and a behavioural test would not catch a *fourth*
token-bearing sender added tomorrow.

Other decisions:

- **Test Mode wins.** It already redirects everything to one inbox, so keeping the BCC would deliver
  the same message there twice — and the copy would be the *unredirected* one, with no `[TEST MODE]`
  marking, reading like real mail that had genuinely reached a candidate.
- **Bcc, not Cc** — a candidate must not see that anyone else got a copy, or reply-all into a team's
  internal inbox.
- **The payment-expiration notice does not carry it** — that already goes to the team's own
  `AdminNotificationEmail`.
- **Blank stores null**, so "is monitoring on?" is one check everywhere. The audit entry records
  whether it is on or off, never the address, matching how `TestModeOverrideEmail` is handled.

**Privacy caveat, stated in the UI as well as here:** a blind-copied confirmation contains a
candidate's name and email address, and once delivered it lives in that mailbox indefinitely.
`PiiPurgeService` clears database columns, not mail archives — so a purged candidate still exists in
the monitoring inbox. It is meant as a temporary diagnostic, and should be cleared when it has served
its purpose. Whether `Privacy.cshtml` should name it is an open question, deliberately left to Mike.

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
