# Candidate & Admin Email Reference

Single reference for every outbound email this app sends: what triggers each one, who receives it,
every `{{Tag}}` available to its template, and the gotchas worth knowing before editing content or
debugging why a candidate did (or didn't) get an email.

This consolidates and supersedes the scattered detail in `docs/email-notifications.md` (Phase 4,
now stale on multi-team specifics) and `docs/payment-reminders.md` (Phase 6, still accurate for its
own two templates) — both are still worth reading for their phase-specific implementation notes,
but this doc is the one place with the full picture across all six templates. If this doc and the
code ever disagree, trust the code — specifically
`src/VeSessionManager.Core/Email/EmailTemplatePlaceholders.cs`, a registry hand-collected from the
real send-time code and guarded by `EmailTemplatePlaceholdersTests.cs` so it can't silently drift.
That registry is also what the Admin UI's template editor (`Pages/Admin/EmailTemplates.cshtml`,
`SystemAdmin`/`TeamAdmin` only) shows as placeholder chips next to the editor.

## The six templates

| `EmailTemplate.Key` | Recipient | Trigger | Idempotency guard |
|---|---|---|---|
| `RegistrationConfirmation` | Candidate | Candidate ingested from ExamTools — either the ~5-minute poll tick, or a Session Manager clicking "Refresh candidates" | `Candidate.RegistrationConfirmationSentUtc` |
| `DayBeforeReminder` | Candidate | Their session's `ScheduledStartUtc` falls on tomorrow's UTC calendar date — checked by a separate 24-hour job | `Candidate.DayBeforeReminderSentUtc` |
| `PaymentReminder5Day` | Candidate | Their `Unpaid` payment: `ApplicationStatus = Received` and FCC entered the application 5+ days ago | `Payment.PaymentReminderSentUtc` |
| `PaymentExpirationNotice` | **Session Manager** (`EmailSettings.AdminNotificationEmail`), not the candidate | Same payment, at 10+ days | `Payment.ExpiredUnpaid` |
| `FelonyDisclosureInstructions` | Candidate | Session marked completed, and this candidate was just flipped `Tested = true` with `HasFelonyDisclosure = true` | None — fires once, inside the one-shot "mark session completed" action, not a repeatable scan |
| `ArrlYouthProgramInstructions` | Candidate | Session Manager clicks "Send Youth Program instructions" on the candidate row (only shown when the session's Vec has `SupportsYouthProgram`) | None — manual action, can be clicked more than once |

Two of these (`RegistrationConfirmation`, `PaymentReminder5Day`) can also be re-triggered by hand:
a Session Manager's "Resend confirmation email" button on the session detail page re-sends
`RegistrationConfirmation` regardless of whether it was already sent, and refreshes the guard
timestamp. There's no equivalent manual resend for the others.

## Every placeholder tag, by template

Pulled straight from `EmailTemplatePlaceholders.cs` — this is the authoritative list of tags each
template can actually use. A `{{Tag}}` not in this list for a given template will render as the
literal, un-substituted text `{{Tag}}` in the sent email (see "Unknown/typo'd tags" below).

**`RegistrationConfirmation`**
| Tag | Value | Notes |
|---|---|---|
| `{{CandidateName}}` | Full name | |
| `{{CandidateFirstName}}` | First name only | |
| `{{SessionDate}}` | e.g. `Friday, July 24, 2026 at 5:00 PM UTC` | Always UTC — no per-session timezone in the data model |
| `{{ZoomJoinUrl}}` | The session's Zoom join link | Blank if Zoom isn't configured for this team, or the meeting hasn't been created yet |
| `{{PaymentLinkUrl}}` | The candidate's InitialExam Square payment link | Blank if the VEC's `FeeConfiguration` doesn't collect a fee, or Square hasn't generated the link yet |
| `{{PrivacyPolicyUrl}}` | From this team's `EmailSettings.PrivacyPolicyUrl` | |

**`DayBeforeReminder`**
| Tag | Value | Notes |
|---|---|---|
| `{{CandidateName}}` | Full name | |
| `{{CandidateFirstName}}` | First name only | |
| `{{SessionDate}}` | Same formatting as above | |
| `{{ZoomJoinUrl}}` | Session's Zoom join link | |
| `{{OutstandingPaymentLinkUrl}}` | Most recent `Unpaid` payment's link | Blank if nothing's outstanding |

**`PaymentReminder5Day`**
| Tag | Value |
|---|---|
| `{{CandidateName}}` | Full name |
| `{{ZoomJoinUrl}}` | Session's Zoom join link |
| `{{PaymentLinkUrl}}` | The unpaid payment's link |

**`PaymentExpirationNotice`** (goes to the Session Manager, not the candidate)
| Tag | Value |
|---|---|
| `{{CandidateName}}` | Full name |
| `{{SessionDate}}` | Same formatting as above |
| `{{PaymentAmount}}` | Literal `$` prefix + 2 decimals, e.g. `$15.00` — deliberately not `"C"`/`InvariantCulture` formatting, which renders `¤` instead of `$` |

**`FelonyDisclosureInstructions`**
| Tag | Value |
|---|---|
| `{{CandidateName}}` | Full name |

**`ArrlYouthProgramInstructions`**
| Tag | Value |
|---|---|
| `{{CandidateName}}` | Full name |
| `{{CallSign}}` | Candidate's call sign |

### Unknown/typo'd tags

A `{{Tag}}` in a template's `Subject`/`Body` that isn't in that template's dictionary above (almost
always a typo, e.g. `{{CandidateFistName}}`) is **left as that literal text** in the sent email and
logged as a `WARNING` — deliberately not silently dropped, so a broken template is visibly broken
instead of mailing out a mysteriously missing word. A tag that *is* recognized but happens to have
an empty value (no outstanding payment, fee not collected) substitutes cleanly to nothing — no
warning.

### HTML encoding

`Body` is real HTML — `Subject` is plain text. Placeholder values substituted into `Body` are
HTML-encoded first; values substituted into `Subject` are not. This matters because several tags
(`CandidateName`, etc.) ultimately come from ExamTools' public registration intake — i.e.,
registrant-controlled data — so an unescaped HTML/script-bearing name can't get injected into the
HTML a recipient's mail client renders.

## The send pipeline, in order

`RegistrationConfirmation` is deliberately the *last* step of a per-team pipeline, so its Zoom and
payment links have their best chance of already existing by the time it renders:

```
Ingestion → VE roster sync → Zoom/Discord scheduling → Square payment link generation → Registration confirmation
```

This runs two ways, both executing the identical sequence for a given `Team`:

1. **`SessionIngestionJob`** — the background poll tick, on `SystemSettings.SessionIngestionIntervalMinutes`
   (flat cadence per team, default 60 minutes; there is no "surge near session start" behavior —
   that was removed in favor of item 2 below).
2. **"Refresh candidates"** — a button on the session detail page
   (`Pages/SessionManager/Detail.cshtml`, `OnPostRefreshCandidatesAsync`, wired to
   `ManualCandidateRefreshService`). A Session Manager who sees a new registrant in ExamTools can
   pull them in — and trigger their confirmation email — immediately instead of waiting for the
   next poll. Runs for every session under that candidate's team, not just the one being viewed,
   same scope as the background job. Job names in `JobRunHistory` are prefixed `Manual*` so this
   run is distinguishable from a scheduled tick on the ops dashboard.

`DayBeforeReminder` and the two payment-reminder passes are **not** part of this pipeline — they're
separate daily jobs (`DayBeforeReminderJob`, `PaymentReminderJob`, both 24-hour `PeriodicTimer`s
from Worker startup, not pinned to a specific wall-clock time). By the time either runs, the
session's Zoom link has normally existed for a while, so there's no equivalent ordering concern.

`FelonyDisclosureInstructions` isn't scan-based at all — it fires synchronously, once, from inside
`SessionActionService.MarkCompletedAsync`, for each candidate whose `Tested` flag that same call
just flipped to `true`.

### The one-shot gotcha

`RegistrationConfirmation`'s guard field (`RegistrationConfirmationSentUtc`) is set the instant the
send succeeds — **not** conditioned on `ZoomJoinUrl`/`PaymentLinkUrl` actually having a value. If
Zoom or Square is unconfigured, or either API call happens to fail at that exact moment, the
candidate gets an email with a blank link, and nothing automatically retries — the guard field is
already set, so neither the next poll nor another "Refresh candidates" click will resend it. The
only recovery today is a Session Manager noticing and clicking "Resend confirmation email" by hand.

## Per-team configuration

Every piece of this is per-`Team`, hand-edited in the DB (or via the Admin UI where noted) — never
shared across teams, confirmed with the user during the multi-team build-out:

- **SMTP credentials**: `Team.SmtpHost`/`SmtpPort`/`SmtpUsername`/`SmtpPassword`/`SmtpUseStartTls`.
  `Team.IsEmailConfigured` requires both `SmtpHost` *and* `SmtpUsername` to be non-blank — `SmtpHost`
  alone isn't enough, since `appsettings.json` ships a real-looking default (`smtp.mailgun.org`)
  that would otherwise read as "configured" before any real credentials exist.
- **From/Reply-To/Privacy policy link**: `EmailSettings` — one row per team (`TeamId` FK, not a
  singleton), holding `FromAddress`/`FromDisplayName`/`ReplyToAddress`/`PrivacyPolicyUrl`/
  `AdminNotificationEmail`. Seeded once per team with obviously-placeholder values
  (`noreply@example.org`, etc.) — never re-seeded over a real edit.
- **Template content**: `EmailTemplate`, keyed by `(TeamId, Key)` — each team can word things
  differently. Seeded once per team with real starting-example content (not final copy) the first
  time that `Key` doesn't exist yet for that team; never overwritten after. Edit via
  `Pages/Admin/EmailTemplates.cshtml` (`SystemAdmin`/`TeamAdmin`, edit-only — no create/delete,
  since the set of `Key`s is fixed by what the sending code actually looks up) or directly in the DB.
- If SMTP isn't configured for a team, every send attempt for that team is skipped quietly (one
  `INFO` log line naming the backlog count, never a repeating `ERROR`) and every guard field stays
  null — the moment credentials are set, the next poll/job tick sends everything backlogged with no
  other action needed.

## Checking what a candidate actually received

Every send tracked above (except the two Youth Program/Felony Disclosure fields added for this
purpose specifically) already had a `...SentUtc` column, but none of them were ever surfaced
anywhere in the UI — the only way to check was opening the SQLite DB directly. The session detail
page's candidate row kebab menu now has an **"Email history"** item, opening a modal listing every
tracked send for that candidate (label + timestamp), sourced from:

- `Candidate.RegistrationConfirmationSentUtc`
- `Candidate.DayBeforeReminderSentUtc`
- `Payment.PaymentReminderSentUtc` (one row per payment that got one — e.g. an initial exam fee and
  a separate retest fee both show up if both were reminded)
- `Candidate.FelonyDisclosureInstructionsSentUtc`
- `Candidate.YouthProgramInstructionsSentUtc`

The last two didn't exist before this modal — `SendFelonyDisclosureInstructionsAsync`/
`SendYouthProgramInstructionsAsync` sent successfully but tracked nothing. They're purely display
timestamps, not idempotency guards (unlike `RegistrationConfirmationSentUtc`): the felony send is
already one-shot by construction (fired once from inside `SessionActionService.MarkCompletedAsync`'s
own "candidates just tested" set), and the Youth Program send has no cap at all — clicking it again
overwrites the timestamp with the latest send rather than keeping the first.

`PaymentExpirationNotice` is deliberately absent from this list — it goes to the Session Manager's
own inbox, not the candidate's, so it isn't "what did this candidate receive."

## Deployment-wide test mode

`SystemSettings.TestModeEnabled`/`TestModeOverrideEmail` — a single deployment-wide switch (not
per-team), enforced in `SmtpEmailSender` below every other layer: when on, **every** real send this
app makes, for every team, gets silently redirected to `TestModeOverrideEmail` instead of its real
recipient, with the original recipient noted in the redirected body and `[TEST MODE]` prefixed on
the subject. No calling service needs to know this exists. Useful for confirming "how many emails
would this candidate actually get" without risking a real send to a real person while testing.
