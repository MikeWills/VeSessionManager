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

1. **5-day FCC fee reminder.** A `Candidate` whose `FccPaymentStatus = PendingVerification` and
   whose `ApplicationDateEnteredUtc` is 5+ days old gets `FccFeeReminder5Day` (placeholders:
   `{{CandidateName}}`, `{{SessionDate}}`, `{{Frn}}`, `{{FccApplicationFileNumber}}`) sent to the
   **candidate**. `Candidate.FccFeeReminderSentUtc` is the guard. **This is the FCC's own
   application fee, paid at CORES — not the team's exam fee.** See "The fee correction" below.
2. **10-day expiration.** An `Unpaid` `Payment` whose `Candidate.ApplicationDateEnteredUtc` is 10+
   days old (and isn't already `ExpiredUnpaid`) gets `Payment.ExpiredUnpaid` set true and
   `PaymentExpirationNotice` (placeholders: `{{CandidateName}}`, `{{SessionDate}}`,
   `{{PaymentAmount}}`) sent to **`EmailSettings.AdminNotificationEmail`** — the Session Manager's
   own inbox, not the candidate's, per the spec.
3. **Unmatched review flag.** A `Candidate` still `Unmatched` more than
   `PaymentReminder:UnmatchedReviewWindowDays` (default 5) past `DateRegisteredUtc` gets
   `Candidate.UnmatchedReviewFlaggedUtc` set and a `WARNING` log line — there's no admin UI yet
   (Phase 9) to surface this list anywhere else, so the log is the only visibility today.

## The fee correction (#219, 2026-08-11)

Found by sending one and reading it — the first candidate-facing email this app produced end to end.

The 5-day reminder used to fire on an unpaid Square `Payment`: the **VEC exam session fee**. But that
fee is collected before or at the session, and the trigger cannot fire until the FCC has received the
application plus five days — by which point the money has been in hand for over a week. It could only
ever fire for a payment that slipped through, and it fired carrying a Square link for a bill the
candidate had usually already settled. The seeded copy read *"your FCC application has been received,
but we haven't seen your exam fee payment yet"*, which is what surfaced it: the sentence sounds like
it is about the FCC's fee, and the link underneath went somewhere else entirely.

**What is actually outstanding at that moment is FCC's application fee**, which the applicant pays
directly to the FCC through CORES and which this app never touches.

The signal was already being collected and read by nothing but a display column.
`UlsWatcherService.ResolvePaymentStatus` maps ULS's `FVPOFF` (fee validation open) to
`FccApplicationPaymentStatus.PendingVerification`, twice daily, per candidate — literally "the FCC fee
is due", from the FCC. The reminder now reads that instead of inferring a different fee's state.

Three consequences worth stating, because each one looks like an omission otherwise:

- **The tracking stamp moved from `Payment` to `Candidate`.** The fee is not the app's, and a team
  that collects no fees has no `Payment` row at all — its candidates still owe the FCC. Scanning
  Candidates is what makes them reachable, and it is why the stamp could not stay where it was.
- **The template carries no payment link, and no placeholder that could become one.** There is
  nothing to link to. This disposes of #218 by construction rather than by patch: an empty `href`
  cannot ship from a body with no link in it. The old bug went out under a green `sent 1, failed 0`,
  so the test asserts the rendered text, not just that a send happened.
- **The retest branch went with the payment it hung off.** A retest has no FCC application of its
  own, so it never has an FCC fee outstanding, and the `ResultMarkedUtc` anchoring that existed to
  make retests work here has nothing left to anchor. Its candidate is `Failed`, which the terminal
  exclusion already covers. The expiration pass still carries that branch, because it is still about
  the Square payment.

`PaymentReminder5Day` is **retired, not deleted** — `SeedTemplateIfMissingAsync` never removes rows,
so every deployment that ran the old version still has one, possibly customised. It is listed in
`EmailTemplateTriggers.Retired` and the Email Templates page labels it "No longer sent". A new key
rather than new copy under the old one, so a team's own wording is never silently repurposed to a
different fee.

**Still open:** whether the 10-day pass means anything now. It expires the Square payment and tells
the admin — but if day 10 is really FCC's dismissal deadline, the meaningful event is *the application
is about to be dismissed*, which is not the same thing and currently shares one code path. Mike also
wants that notice to reach the candidate as well as the Session Manager; that is deliberately not
built yet, because adding a candidate recipient to a notice about the *exam* fee would recreate the
exact wrong-fee error this issue fixed. See #219.

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

**As of #219 only the expiration pass carries this**, because the 5-day reminder no longer looks at
Payments at all — a retest has no FCC application, so it never has an FCC fee outstanding. What
follows describes the expiration pass; the reminder's copy of it is gone.

The expiration pass carries a second, independent branch for exactly this case: `Payment.Reason ==
Retest && Candidate.ApplicationStatus == Failed`, anchored on `Candidate.ResultMarkedUtc` (set by
`CandidateActionService.MarkFailedAsync` the moment the Session Manager marks the result) instead of
`ApplicationDateEnteredUtc` — "the Session Manager marked a result" is the retest's real analogue of
"the FCC application was entered," per the spec's own suggested fix. The `InitialExam` branch is
unchanged.

**Same-run double-fire is expected, not a bug:** if the job is down for a while and a candidate is
first evaluated at, say, 12 days old, both the 5-day reminder and the 10-day expiration notice can
fire in the same run — each is independently idempotent via its own tracking field, and nothing in
the spec says they're mutually exclusive. Since #219 they are also about different fees, so a
candidate could legitimately receive both: one saying the FCC is waiting, one saying the team's
payment link expired.

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
