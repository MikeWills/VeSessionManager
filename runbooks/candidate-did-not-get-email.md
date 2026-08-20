# Runbook — "The candidate never got the email"

**When:** somebody reports a missing registration confirmation, reminder, or instruction email — or
an email arrived with a blank Zoom or payment link.

**Why it works this way:** [`docs/email-reference.md`](../docs/email-reference.md),
[`docs/email-notifications.md`](../docs/email-notifications.md),
[`docs/trigger-points.md`](../docs/trigger-points.md).

---

## 1. Is the whole deployment redirecting mail?

**Admin → System Settings.** If **test mode** is on, *every* send from *every* team goes to the
override address instead, with `[TEST MODE]` on the subject and the original recipient noted in the
body. A red banner shows on every layout, including the login page.

This is the first thing to check — it is deployment-wide, not per team.

## 2. Is SMTP configured for that team?

`SmtpEmailSender.IsConfigured` requires **`SmtpUsername`**, not just `SmtpHost` — because `SmtpHost`
has a real default baked into `appsettings.json`, so a host check alone reads as "configured" the
instant the repo is cloned.

An unconfigured team skips quietly with one aggregate INFO line and leaves the tracking field null,
so the next poll retries automatically once credentials are added. No backfill step is needed.

⚠️ **Exception:** a muted team is an **error**, not a quiet success, on the hand-composed
"Email candidates" path — somebody is waiting at a button there.

## 3. Which email was it, and does it retry?

| Email | Trigger | Guard | Resendable? |
|---|---|---|---|
| Registration confirmation | pipeline (scheduled + manual refresh) | `RegistrationConfirmationSentUtc` null, session not ended | **Yes** — per-candidate "Resend confirmation email" button (re-stamps, no guard) |
| Day-before reminder | `MessageRuleJob` — `BeforeSessionStart` rule | a `MessageRuleRun` row | No |
| FCC fee reminder | `MessageRuleJob` — `FccFeeOutstanding` rule | a `MessageRuleRun` row | No |
| Payment expiration notice | `MessageRuleJob` — `PaymentUnpaid` rule | admin-facing, not to the candidate | No |
| Felony disclosure instructions | per-candidate button | timestamp is display-only | Yes |
| Youth program instructions | per-candidate button | display-only | Yes, by design |
| "Getting started locally" and other templates | hand-composed on Session Detail → Email candidates | a `CandidateEmailSend` row per delivery | Sending again is a decision somebody makes |

Only `Sent` and `Suppressed` are terminal for a rule run — a **failed** send is logged *and*
retried, with the retry updating the row rather than inserting past the unique index.

## 4. The one-shot gotcha — blank links

⚠️ `RegistrationConfirmationSentUtc` is set the instant the send succeeds, **not** conditioned on
`ZoomJoinUrl` / `PaymentLinkUrl` actually having a value.

So if Zoom or Square was unconfigured, or either API call happened to fail at that exact moment, the
candidate got an email with a **blank link** and nothing retries — the guard field is already set,
so neither the next poll nor another "Refresh candidates" click resends it.

**The only recovery is a Session Manager clicking "Resend confirmation email" by hand.** Fix the
integration first, then resend.

## 5. Did the person unsubscribe?

For **VE** mail (not candidate mail): an unsubscribe stops **session invitations too**. That is
deliberate — a partly-honoured unsubscribe is one that filtered rather than stopped — and it costs
somebody a phone call. Check the VE's subscription state before assuming a delivery failure.

Note the opt-out page changes nothing on a **GET**, because mail clients and scanners prefetch links.

## 6. A rescheduled session

**A reschedule does not re-send anything, and cannot** — nothing in the codebase ever clears
`RegistrationConfirmationSentUtc`. That is safe only because a session with candidates refuses to
move: it sets `RescheduleFlaggedForReview` and leaves the stored time alone. A date change with
candidates is always a human-handled event, and the human's tool is per-candidate
"Resend confirmation email".

## 7. Nothing sends for a newly added message rule

`MessageRule.CreatedUtc` bounds every scan. That is what stands between adding a rule and mailing
everybody already past its moment — so a new rule does not fire retroactively, by design.

Also: `PaymentEligibilityWindow` (30 days from session start) silently caps `FccFeeOutstanding` and
`PaymentUnpaid`. A rule set beyond that window **never fires and nothing fails** — no send, no
error, no marker. The Message Rules form shows a caution rather than refusing.

## 8. What the email actually said

If the complaint is about content rather than delivery — times in particular — candidate-facing
times render as `10:00 AM ET / 7:00 AM PT` via `SessionTimeFormatter.ForCandidate`. If a candidate
email is showing UTC, that is a real bug: the Web project's `EasternTimeFormatter` is unreachable
from Core, which is how candidate mail once spent months rendering UTC while every screen rendered
ET.
