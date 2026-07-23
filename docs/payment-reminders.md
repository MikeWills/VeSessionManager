# Payment Reminder & Expiration Job (Phase 6)

> See `docs/email-reference.md` for these two templates alongside all four of the app's other
> outbound emails in one place (full placeholder tables, send pipeline, per-team config). This doc
> remains the right place for the reminder/expiration *logic* detail below (thresholds, exclusions).

What `PaymentReminderService` (`VeSessionManager.Core/Payments/`) does and depends on. No new
external API — this phase is pure date/status logic over data every earlier phase already
produces, plus two more `EmailTemplate` rows and one more `EmailSettings` field.

## Three independent passes, one run

`PaymentReminderJob` runs `PaymentReminderService.RunAsync` daily (`Jobs:PaymentReminderIntervalHours`,
default 24h). Each pass is scan-based and idempotent — its own tracking field is both the "needs
action" filter and the guard against double-sending/double-flagging on the next run:

1. **5-day reminder.** An `Unpaid` `Payment` whose `Candidate.ApplicationStatus = Received` and
   `ApplicationDateEnteredUtc` is 5+ days old gets `PaymentReminder5Day` (placeholders:
   `{{CandidateName}}`, `{{ZoomJoinUrl}}`, `{{PaymentLinkUrl}}`) sent to the **candidate**.
   `Payment.PaymentReminderSentUtc` is the guard.
2. **10-day expiration.** An `Unpaid` `Payment` whose `Candidate.ApplicationDateEnteredUtc` is 10+
   days old (and isn't already `ExpiredUnpaid`) gets `Payment.ExpiredUnpaid` set true and
   `PaymentExpirationNotice` (placeholders: `{{CandidateName}}`, `{{SessionDate}}`,
   `{{PaymentAmount}}`) sent to **`EmailSettings.AdminNotificationEmail`** — the Session Manager's
   own inbox, not the candidate's, per the spec.
3. **Unmatched review flag.** A `Candidate` still `Unmatched` more than
   `PaymentReminder:UnmatchedReviewWindowDays` (default 5) past `DateRegisteredUtc` gets
   `Candidate.UnmatchedReviewFlaggedUtc` set and a `WARNING` log line — there's no admin UI yet
   (Phase 9) to surface this list anywhere else, so the log is the only visibility today.

All three share the spec's base exclusions: `NotApplicable` payments, a terminal
`Candidate.ApplicationStatus` (`Granted`/`Failed`/`NotTested`), and a `Cancelled` session. A
`PiiPurgedUtc`-set candidate is also excluded from every pass (no `Email` left to notify, and
nothing left worth flagging). **Failed is a carved-out exception for a `Reason=Retest` payment —
see "Retest payments" below.**

**Why Unmatched candidates never trigger the reminder/expiration passes without a separate status
check:** `ApplicationDateEnteredUtc` is only ever set once Phase 5 marks a candidate `Received`
(or later `Granted`) — an `Unmatched` candidate's value is always `null`, so the `!= null` filter
on those two passes excludes them as a side effect, exactly matching the spec's "excluded from both
triggers... flag separately instead."

## Retest payments (fixed 2026-07-22, tracked since Phase 6's own spec note)

A retest payment's owning `Candidate` is always `ApplicationStatus = Failed` — `CandidateActionService.
CreateRetestPaymentAsync` requires `Failed` to create the payment, and nothing in this app ever
moves a `Candidate` off `Failed` once set (a passed retest shows up as a brand-new `Candidate` row
from ExamTools, not a mutation of the failed one). `Failed` is terminal, and a `Failed` candidate has
no FCC application of its own, so the two money-passes' normal `ApplicationDateEnteredUtc` gate can
never fire for it — a same-session retest fee would otherwise never get a reminder or expiration at
all, exactly the gap the spec flagged as an open question.

Both passes now carry a second, independent branch for exactly this case: `Payment.Reason ==
Retest && Candidate.ApplicationStatus == Failed`, anchored on `Candidate.ResultMarkedUtc` (set by
`CandidateActionService.MarkFailedAsync` the moment the Session Manager marks the result) instead of
`ApplicationDateEnteredUtc` — "the Session Manager marked a result" is the retest's real analogue of
"the FCC application was entered," per the spec's own suggested fix. The `InitialExam` branch is
unchanged.

**Same-run double-fire is expected, not a bug:** if the job is down for a while and a payment is
first evaluated at, say, 12 days old, both the 5-day reminder and the 10-day expiration notice fire
in the same run — each is independently idempotent via its own tracking field, and nothing in the
spec says they're mutually exclusive.

## Config

- `PaymentReminder:UnmatchedReviewWindowDays` (default `5`) — the one part of this phase the spec
  explicitly calls out as configurable. The 5-day/10-day thresholds themselves are **not**
  configurable — they're fixed by the feature's own name in the spec, so they're hardcoded
  constants in `PaymentReminderService`, not appsettings.
- `EmailSettings.AdminNotificationEmail` (new field, Phase 6) — hand-edited in the DB like every
  other `EmailSettings`/`EmailTemplate` field (see `docs/email-notifications.md`); seeded with a
  placeholder (`admin@example.org`) that must be replaced before a real expiration notice would
  reach anyone.

## Currency formatting gotcha caught before shipping

`{{PaymentAmount}}` is formatted as `$"${amount:F2}"` (a literal `$` prefix), **not**
`amount.ToString("C", CultureInfo.InvariantCulture)`. The invariant culture's currency symbol is
the generic `¤`, not `$` — `"C"` + `InvariantCulture` would have put `¤15.00` in a real admin
notice email. This app is US-only (FCC/ARRL), so an explicit `$` is simpler and correct where a
culture-driven format isn't.
