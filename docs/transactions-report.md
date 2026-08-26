# Transactions Report

Requested 2026-08-26, right after live-verifying payments and refunds work end to end (#431):
*"we might want a report that shows all transactions refunds and payments and that will need to tie
to the candidate."* `Pages/SessionManager/TransactionsReport.cshtml`, Admin-only.

## Why this needed a schema change first

`Candidate.Name` does not survive PII purge — scheduled retention, or immediately on a no-show/
withdrawal — and a withdrawal is exactly the kind of event that produces a refund. A report reading
`Candidate.Name` directly would go blank for the rows most worth being able to see.

`Payment.CandidateNameSnapshot` exists to answer that: captured once, in `PaymentGenerationService`,
at the moment the `Payment` row is created (both the InitialExam and Retest paths) — the earliest
point a name could be captured, and the one that guarantees it happens before any future purge,
whether or not that payment is ever refunded. Same pattern `MessageRuleRun` already uses to snapshot
a rule's name so its history survives the rule being deleted — a fact copied onto the row that will
actually need it, not a change to what gets purged or when.

**Deliberately narrower than delaying PII purge.** The alternative — keep a withdrawn candidate's
name around longer — would reopen a privacy design this app got right on purpose (see
`docs/pii-purge.md`), for the sake of one report. Mike, on being asked to choose: *"I am comfortable
with"* keeping the name, but the snapshot gets there without touching purge policy at all. Nothing in
`CandidatePiiFields.Clear` touches `Payment.CandidateNameSnapshot` — it's a fact about what the
Payment was for, not about the Candidate, so it was never in scope to clear in the first place.

**The backfill is real, unlike most migrations that touch `Payments`.** `RemovePaymentExpiredUnpaid`
and `MessageEligibilityFloor` both deliberately backfilled nothing, because inventing a value would
have fabricated history. This one is different: for every Payment whose Candidate hasn't been purged
yet, the name is still sitting right there, so the migration copies it. A Payment whose candidate was
already purged stays null — there's nothing left to copy, and null means "unknown," not "never had a
name." Rehearsed against a copy of the real local database: 31 of 70 Payments backfilled, the other
39 correctly left null (already-purged candidates), `PRAGMA foreign_key_check`/`integrity_check` both
clean.

## What counts as a transaction

Only a `Paid` `Payment` is a row — `Unpaid`/`NotApplicable` never moved money, so they aren't
transactions, just payment rows that exist. A `Refund` is a row whatever its outcome
(`Pending`/`Completed`/`Rejected`/`Failed`): this is a record of what was *attempted*, not just what
settled, so a rejected refund is still worth being able to see. But only a `Completed` refund
subtracts from the totals line — a `Pending`/`Rejected`/`Failed` one hasn't (or won't) actually move
money back out, and counting it would understate what the team has.

Payment and refund rows are date-filtered independently. A payment from a month ago with a refund
issued today belongs in "today"'s range for the refund half only — the two are separate money events
that happen to share a `Payment` row underneath.

## Where the money-math actually lives

`TransactionsReportModel.BuildRows(payments, fromUtc, toUtc)` is `internal static` and pulled out of
`OnGetAsync` on purpose, so the flattening/signing logic — the part actually worth getting right — is
testable without a database, `HttpContext`, or a signed-in user. `VeSessionManager.Web`'s
`InternalsVisibleTo` for its test project is new for this (`VeSessionManager.Core` already had the
same arrangement for its own tests).

`SessionChips.Refund` (added alongside the roster's refund indicator the same day) supplies the
refund status chip, so this report and the session roster can never show a refund two different ways.

## What's still open

**Void-instead-of-refund for same-day transactions is not handled** (flagged 2026-08-26, right after
this report). A card payment that hasn't yet settled to Square's `COMPLETED` state cannot be refunded
via the Refunds API at all — it has to be cancelled via `CancelPayment` instead, or the call fails
outright. `RefundService` has no branch for this today; it always calls `RefundPaymentAsync` and
surfaces whatever Square says. Tracked as follow-up work, not built here.
