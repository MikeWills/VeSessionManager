# Candidate & Admin Email Reference


> **Superseded in part by `docs/trigger-points.md` (#401, 2026-08-16.)** The four messages this app
> sent automatically are per-team `MessageRule` rows now, with their thresholds expressed in hours,
> so "when does this go out" is no longer answered by the code described below. Everything here about
> *what* each message says, who receives it and which placeholders resolve is still accurate.

Single reference for every outbound email this app sends: what triggers each one, who receives it,
every `{{Tag}}` available to its template, and the gotchas worth knowing before editing content or
debugging why a candidate did (or didn't) get an email.

This consolidates and supersedes the scattered detail in `docs/email-notifications.md` (Phase 4,
now stale on multi-team specifics) and `docs/payment-reminders.md` (Phase 6, still accurate for its
own two templates) — both are still worth reading for their phase-specific implementation notes,
but this doc is the one place with the full picture across all seven templates. If this doc and the
code ever disagree, trust the code — specifically
`src/VeSessionManager.Core/Email/EmailTemplatePlaceholders.cs`, a registry hand-collected from the
real send-time code and guarded by `EmailTemplatePlaceholdersTests.cs` so it can't silently drift.
That registry is also what the message editor (`Pages/Admin/MessageRuleEdit.cshtml`,
`SystemAdmin`/`TeamAdmin` only) draws its clickable tag chips from — though what it offers is the
chosen *trigger's* tags rather than the whole registry, which is the point of authoring a message
against its trigger.

## The seven templates

| `EmailTemplate.Key` | Recipient | Trigger | Idempotency guard |
|---|---|---|---|
| `RegistrationConfirmation` | Candidate | Candidate ingested from ExamTools — either the ~5-minute poll tick, or a Session Manager clicking "Refresh candidates" | `Candidate.RegistrationConfirmationSentUtc` |
| `DayBeforeReminder` | Candidate | Their session's `ScheduledStartUtc` falls on tomorrow's UTC calendar date — checked by a separate 24-hour job | `Candidate.DayBeforeReminderSentUtc` |
| `FccFeeReminder5Day` | Candidate | **The FCC's** application fee is still outstanding — `FccPaymentStatus = PendingVerification` and FCC entered the application 5+ days ago. Not the team's exam fee; see #219 | `Candidate.FccFeeReminderSentUtc` |
| `PaymentExpirationNotice` | **Session Manager** (`EmailSettings.AdminNotificationEmail`), not the candidate | The team's `Unpaid` exam-fee payment, 10+ days after FCC entered the application | `Payment.ExpiredUnpaid` |
| `FelonyDisclosureInstructions` | Candidate | **Manual** — "Send felony disclosure instructions" on the candidate's row, for anyone with `HasFelonyDisclosure = true`. Usually sent *before* the session (#221) | None — a repeat click is a deliberate re-send; `Candidate.FelonyDisclosureInstructionsSentUtc` records the latest |
| `ArrlYouthProgramInstructions` | Candidate | Session Manager clicks "Send Youth Program instructions" on the candidate row (only shown when the session's Vec has `SupportsYouthProgram`) | None — manual action, can be clicked more than once |
| `GettingStartedLocally` | Candidate | **Never sent by code.** Starting text for a message composed by hand on Session Detail → "Email candidates", edited before sending (#144). See [`docs/candidate-email.md`](candidate-email.md) | None — a `CandidateEmailSend` row records each delivery, and re-sending is a decision somebody makes |

One of these can be re-triggered by hand: a Session Manager's "Resend confirmation email" button on
the session detail page re-sends `RegistrationConfirmation` regardless of whether it was already
sent, and refreshes the guard timestamp. There's no equivalent manual resend for the others.

> **The two reminders are about two different bills, and that is the whole point of #219.**
> `FccFeeReminder5Day` chases the **FCC's** application fee, which the candidate pays directly at
> CORES and which this app never handles. `PaymentExpirationNotice` is about the **team's** exam fee,
> collected through Square. The 5-day reminder used to be about the team's fee too — money already in
> hand by the time it could fire — and it carried a Square link for a bill usually already settled.

## Every placeholder tag, by template

Pulled straight from `EmailTemplatePlaceholders.cs` — this is the authoritative list of tags each
template can actually use. A `{{Tag}}` not in this list for a given template will render as the
literal, un-substituted text `{{Tag}}` in the sent email (see "Unknown/typo'd tags" below).

**`RegistrationConfirmation`**
| Tag | Value | Notes |
|---|---|---|
| `{{CandidateName}}` | Full name | |
| `{{CandidateFirstName}}` | First name only | |
| `{{SessionDate}}` | e.g. `10:00 AM ET / 7:00 AM PT` | Eastern **and** Pacific, via `SessionTimeFormatter.ForCandidate` — this said "always UTC" until 2026-08-16, which stopped being true at #205 and was wrong in the doc for months afterwards. Never format a candidate-facing time any other way |
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

**`FccFeeReminder5Day`** (about the FCC's fee — deliberately carries no payment link)
| Tag | Value |
|---|---|
| `{{CandidateName}}` | Full name |
| `{{SessionDate}}` | Same formatting as above |
| `{{Frn}}` | The candidate's FRN — what CORES asks for, so omitting it sends the reader hunting |
| `{{FccApplicationFileNumber}}` | ULS application file number, when known |

There is no payment-link tag here and there must not be one. The FCC bills the applicant directly;
the team's Square link pays a different bill, and offering it was the original defect (#218/#219).

**`PaymentExpirationNotice`** (goes to the Session Manager, not the candidate)
| Tag | Value |
|---|---|
| `{{CandidateName}}` | Full name |
| `{{SessionDate}}` | Same formatting as above |
| `{{PaymentAmount}}` | Literal `$` prefix + 2 decimals, e.g. `$15.00` — deliberately not `"C"`/`InvariantCulture` formatting, which renders `¤` instead of `$` |

**`FelonyDisclosureInstructions`** (manual, per candidate — see above)
| Tag | Value |
|---|---|
| `{{CandidateName}}` | Full name |

**`ArrlYouthProgramInstructions`**
| Tag | Value |
|---|---|
| `{{CandidateName}}` | Full name |
| `{{CallSign}}` | Candidate's call sign |

**`GettingStartedLocally`** (composed by hand — see [`docs/candidate-email.md`](candidate-email.md))
| Tag | Value |
|---|---|
| `{{CandidateName}}` | Full name |
| `{{CandidateFirstName}}` | First name only |
| `{{CallSign}}` | Candidate's call sign — **usually empty here**, since a new licensee's arrives from the FCC days after the session. The compose screen warns when it applies to anyone selected |
| `{{SessionDate}}` | Same formatting as above |
| `{{TeamName}}` | The team's own name |

The odd one out in this section: these are resolved by `CandidatePlaceholderValues`, not by a
dictionary at a send site, because the message is whatever somebody typed rather than a known
template. `EmailTemplatePlaceholdersTests` asserts the two lists agree, since this table is also what
the compose screen prints as insertable chips. **No payment-link tag, deliberately** — a hand-composed
message goes out whenever somebody decides to send it, so a checkout link that is expired or already
paid is worse than none.

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
Ingestion → VE roster sync → Exam result sync → Zoom/Discord scheduling → Square payment link generation → Registration confirmation
```

The authoritative list is `TeamPipeline` (`Core/Ingestion`), which exists because this order was
written out three times and drifted — exam-result sync was missing from the manual path for weeks.
**Exam result sync was missing from the diagram above for the same reason**, found auditing #193.

This runs two ways, both executing the identical sequence for a given `Team`:

1. **`SessionIngestionJob`** — the background poll tick, on `SystemSettings.SessionIngestionIntervalMinutes`
   (flat cadence per team, default 60 minutes; there is no "surge near session start" behavior —
   that was removed in favor of item 2 below).
2. **"Refresh candidates"** — a button on the session detail page
   (`Pages/SessionManager/Detail.cshtml`, `OnPostRefreshCandidatesAsync`, wired to
   `ManualCandidateRefreshService`). A Session Manager who sees a new registrant in ExamTools can
   pull them in — and trigger their confirmation email — immediately instead of waiting for the
   next poll. **Scoped to the session being viewed** — changed 2026-08-03, and this document said
   the opposite until #193 checked it: it previously ran the team's whole pipeline, so one click
   could mint payment links and email candidates for every *other* session the team had. The rest of
   the team catches up on the next scheduled tick; Team Maintenance's "Refresh now" is the
   deliberately team-wide button. Job names in `JobRunHistory` are prefixed `Manual*` so this run is
   distinguishable from a scheduled tick on the ops dashboard.

`DayBeforeReminder` and the two payment-reminder passes are **not** part of this pipeline — they're
separate daily jobs (`MessageRuleJob` and `PaymentReminderJob` since #401, both 24-hour `PeriodicTimer`s
from Worker startup, not pinned to a specific wall-clock time). By the time either runs, the
session's Zoom link has normally existed for a while, so there's no equivalent ordering concern.

`FelonyDisclosureInstructions` isn't scan-based at all — it is a per-candidate button, so it fires
exactly when somebody presses it.

**It used to be automatic, and that was the bug (#221, 2026-08-11).** `SessionActionService.
MarkCompletedAsync` sent it to every candidate whose `Tested` flag that same call flipped, with no
button and no confirmation. Two things were wrong. The email tells someone their felony disclosure
means extra FCC paperwork, which is not a thing to send as a side effect of a bulk status flip. And
because the trigger was "session completed", it could only ever arrive **after** the exam — the point
at which the candidate can no longer easily ask anyone about it. The useful time is beforehand, so
the condition is now simply that a disclosure was declared, and `Tested` is not consulted at all.

Two consequences of removing an automatic send, both deliberate:

- **The disclosure check moved into the service.** While there was one caller it could trust that
  caller to have filtered; the id comes from a form now, and the wrong recipient here is not a
  cosmetic error. `CandidateEmailSendResult.NoFelonyDisclosure` is the refusal.
- **The candidate is marked, not just counted.** Nothing sends this now unless a human does, so
  "declared a disclosure, instructions not sent" is shown on the session's candidate row and on the
  candidate page, and `MarkCompletedAsync` returns how many are still waiting so its status message
  can say so. A number in a one-off message is gone on the next click; the row marker is not.

### What one click of "Refresh candidates" actually sends

Audited for #193, because the button's own `TODO` worried it would train Session Managers to expect
"one click, one email". The answer is narrower and safer than that:

**At most one registration confirmation per candidate, ever** — and only for candidates who have
never been confirmed, on a session that has not already ended, in the one session being viewed.
Clicking it a second time sends nothing, because the guard field is already stamped. A Session
Manager who presses it after every ExamTools change is not generating mail.

Nothing else in the pipeline emails a candidate at all. The reminders and the payment passes are
separate daily jobs, and the three per-candidate emails are buttons.

| Email | Trigger | Guard | Sent by "Refresh candidates"? |
|---|---|---|---|
| Registration confirmation | pipeline (scheduled + manual) | `RegistrationConfirmationSentUtc` null, and session not ended | **Yes**, once per candidate |
| Registration confirmation (resend) | per-candidate button | none — deliberate, and re-stamps | No |
| Day-before reminder | `MessageRuleJob` (`BeforeSessionStart` rule) | a `MessageRuleRun` for that rule | No |
| FCC fee reminder | `MessageRuleJob` (`FccFeeOutstanding` rule) | a `MessageRuleRun` for that rule | No |
| Payment expiration notice | `MessageRuleJob` (`PaymentUnpaid` rule) | admin-facing, not to the candidate | No |
| Felony disclosure instructions | per-candidate button | timestamp is display-only | No |
| Youth program instructions | per-candidate button | display-only; repeatable by design | No |

**A reschedule does not re-send anything, and cannot.** Nothing in `src/` ever clears
`RegistrationConfirmationSentUtc`. That is safe rather than a gap only because
`ApplyRescheduleRules` refuses to move a session that has candidates — it sets
`RescheduleFlaggedForReview` and leaves the stored time alone, so a date change with candidates is
always a human-handled event, and the human has "Resend confirmation email" per candidate. If that
policy ever changes, this becomes a real hole: candidates would hold a stale date with nothing to
correct it.

The send-once properties are pinned by `RegistrationConfirmation_AlreadySent_IsNotResent`,
`DayBeforeReminder_AlreadySent_IsNotResent` and `PreSessionReminder_RunTwiceInsideTheWindow_SendsOnce`.

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
- **Message content**: `MessageRule.Subject`/`Body`, per team — each team can word things
  differently. Seeded once per team with real starting-example content (not final copy), tracked by
  `Team.MessageRulesSeededUtc` so a deleted message never comes back. Edit via
  `Pages/Admin/MessageRuleEdit.cshtml` (`SystemAdmin`/`TeamAdmin`) or directly in the DB.
  ⚠️ **Until 2026-08-21 this was an `EmailTemplate` table keyed by `(TeamId, Key)`, and a rule
  pointed at one by key.** That split is gone: the tags a message may use depend on its trigger, and
  a template had none, so the editor could never say which were available. See
  `docs/trigger-points.md`.
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
- `CandidateEmailSend` rows — every hand-composed send (#144), labelled with the template it started
  from. A table rather than a column, because a team writes its own templates and a column per
  template cannot be added by somebody at runtime; see [`docs/candidate-email.md`](candidate-email.md)

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
