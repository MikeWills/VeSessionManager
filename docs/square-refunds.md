# Refunds through Square

Issue [#375](https://github.com/MikeWills/VeSessionManager/issues/375). Built 2026-08-15.

Refunds used to be a manual job in the Square dashboard. `Payment.RefundRequested` was a note saying
somebody intended to do one — its own comment said as much — and the dismiss modal on Unmatched
Payments had to state **in bold** that dismissing refunds nothing, because there was no alternative
to point at.

Now a Session Manager can issue one, in full or in part, from two places: a candidate's payment row
on the applicant detail page, and "Refund and dismiss" on Unmatched Payments.

## The one thing to know before changing any of this

**A refund is not finished when the API call returns.** Square answers immediately with a status of
`PENDING` and then processes it: up to 14 days for a card or bank transfer, and it can still end
`REJECTED` or `FAILED`. Every other outbound Square call in this app finishes when it returns, so
this is the assumption most likely to be carried in by accident.

Two consequences run through the whole design:

- `RefundStatusService` polls unsettled refunds hourly until a terminal state is observed. Without
  it, a refund Square later rejected would sit on the screen as issued forever.
- The success message says **"submitted"**, not "refunded", unless the status is `Completed`. Telling
  a Session Manager the money went back when it has not is how the candidate gets told the same
  thing, and then nobody looks again. `ActionOutcomesTests.IssueRefund_OnlyClaimsTheMoneyWentBackWhenSquareSaysCompleted`
  pins it.

## What was actually blocking this

`RefundPayment` is keyed by Square's **payment** id. `Payment.SquarePaymentReferenceId` holds the
**order** id despite its name — a consequence of the known constraint that `payment.updated` does not
echo `reference_id` back, only `order_id`.

The issue treated this as needing a schema change plus an unproven Orders API lookup. Half of that
was right. `SquareWebhookHandler` **already parsed the payment id** and passed it down the unmatched
branch; the matched branch simply discarded it. So the fix was one assignment plus a nullable column,
`Payment.SquarePaymentId`.

What the issue got right is that it only helps going forward. **Nothing backfills it**, and nothing
should until the order-id → payment-id lookup is proven against a real order. Every payment matched
before this shipped is un-refundable from inside the app, permanently, and the UI says so in those
words rather than hiding a disabled button.

`UnmatchedSquarePayment.SquarePaymentId` has existed since that entity was written, which is why that
half needed no schema change at all and was the one worth doing first.

## Retry safety

The established pattern, and this is the case it was written for. `Refund` is persisted **before**
Square is called, carrying its idempotency key; the key is reused on every subsequent attempt, so a
crash between the call succeeding and the response landing cannot produce a second refund.

Three states, and the difference between the last two is the whole thing:

| State | What it means | What happens next |
|---|---|---|
| `Submitting`, no `SquareRefundId` | The call never came back. Square may or may not have made the refund. | Re-sent with the **same** key — by a re-click, or by the status job, which is the only thing that would otherwise ever complete it (the user saw an error and has no reason to try again). |
| `Pending` | Square accepted it and is processing. | Polled until terminal. |
| `Failed` via `SquareRefundException` | Square answered, and the answer was no. | Settled. The same key earns the same refusal. |

`SquareRefundException` exists to keep those apart. A transport failure must **not** settle the
refund — settling is what makes the status job stop looking, which would strand a refund Square had
accepted. Both directions are mutation-tested.

`ISquareClient.RefundPaymentAsync` is also the one call in that interface that does **not** swallow
Square's errors as idempotent no-ops, unlike `DeletePaymentLinkAsync` and `CompleteOrderAsync`. Their
already-done cases are genuinely no-ops. Here every error means no money moved, and a swallowed
`REFUND_AMOUNT_INVALID` would report success.

## Square's rules, verified

Read from the shipped `Square.xml` for SDK 45.0.1 and from Square's docs, not from memory:

- **One year.** Square will not refund a payment whose original date is more than a year ago.
- **20 refunds** maximum against one payment.
- The payment must be `COMPLETED` — an `APPROVED` card payment cannot be refunded.
- The amount may not exceed the payment total minus refunds already completed.
- `PAYMENTS_WRITE` is the required permission. Teams here paste a per-team access token rather than
  using an OAuth grant, so this is very likely already present — but **it fails live, per team**.
  **Confirmed live on WX0MIK, 2026-08-25** (issue #431): a real $15 card-not-present payment was
  taken and refunded from inside the app; Square accepted the refund call and returned `PENDING`
  rather than an auth failure. Still worth checking per-team the first time a new team's token is
  used for a refund, since this only proves WX0MIK's own token carries the scope.

Both limits are checked locally before the call, so the user gets a sentence rather than Square's
error code.

## Eligibility lives in one place

`RefundEligibility.For(...)` answers "can this be refunded, and for how much" for both the service
(to refuse the call) and the pages (to decide whether to offer the button and what to say when they
do not). Two copies of that rule drifting is exactly [#274](https://github.com/MikeWills/VeSessionManager/issues/274),
where one copy of the youth-program check tested the VEC flag and the other tested nothing.

Two decisions inside it are deliberate and would look like bugs otherwise:

- **In-flight refunds count against the remaining balance**, which is stricter than Square's own rule
  ("minus refunds already *completed*"). A `PENDING` refund can take a fortnight, and Square would
  happily accept a second full refund during it. The worst case here is a Session Manager waiting;
  the worst case the other way is paying a candidate twice.
- **A missing paid date does not block the refund.** It is unknown, not old. Square holds the real
  date and will refuse it there if need be; refusing locally would block rows that are perfectly
  refundable.

The refundable ceiling is `SquareAmountPaidUsd ?? Amount` — what Square actually took, not what was
owed. A $5 ARRL youth payment against a $15 `Payment.Amount` is routine here (see
`Payment.SquareAmountPaidUsd`), and offering to refund $15 against it would have Square refuse the
whole thing. That same gap, already tracked by `AmountMismatchFlaggedUtc`, is the case partial
refunds exist for.

## What refunding does not do

**It does not move the `Payment` off `Paid`.** `Unpaid` is a live state: `PaymentGenerationService`
scans "Unpaid and no link" and would generate a fresh Square checkout link for the candidate whose
money was just returned, and `PaymentReminderService` would then chase them for it. Refunded-ness is
derived from `Refund` rows instead. There is no `PaymentStatus.Refunded` and there should not be.

**It does not resolve an unmatched payment by itself.** "Refund and dismiss" refunds first and
dismisses only if that succeeded — dismissing clears the row from the one screen that lists this
money, so dismissing after a failed refund would hide a live payment from the only place anyone would
look for it.

**`RefundRequested` survives.** It is still the right tool for money this API cannot reach: anything
over a year old, cash, or taken outside Square. The two are not redundant.

## Authorization

`RoleGroups.SessionStaff` — SystemAdmin, TeamAdmin, SessionManager. Matches the pages the action sits
on; TeamLead cannot reach either. This was a judgment call: refunding moves real money outward and
cannot be undone from here, so `Admins` was the alternative. SessionStaff won because Session Managers
already match and dismiss these payments, and splitting the action from the screens that lead to it
means the person who found the problem cannot finish it.

Both handlers re-check the posted id against `ResolveViewableTeamIds` — never `GetEffectiveTeamIds`,
which returns null for a SystemAdmin and would 403 them on every attempt.

## The status job

`RefundStatusJob`, hourly, per team, on the `PerTeamDailyJob` scaffold. Hourly rather than the daily
cadence `SquareLinkPurgeJob` uses: most refunds settle within hours, and a rejected one is money a
candidate is still owed with nothing else watching for it. The scan is one indexed query per team and
returns nothing whenever no refund is in flight, which is nearly always.

A rejected or failed refund is logged at `Error`, because it is the one outcome here that needs a
human and no screen is being watched for it.

## Tests

`RefundEligibilityTests` (pure), `RefundServiceTests` (EF InMemory), `RefundStatusJobTests`
(`WorkerTickHarness` over real SQLite), plus the webhook's payment-id capture in
`SquareWebhookHandlerTests` and the message mapping in `ActionOutcomesTests`.

Six mutations were run against the guards that matter, each failing exactly the test that names it:
removing the in-flight resume, settling a transport failure as `Failed`, mapping an unknown Square
status to `Completed`, counting refused refunds against the balance, re-polling settled refunds, and
dropping the webhook's payment-id assignment.

## Not built

- **No backfill** of `Payment.SquarePaymentId` for existing rows — the Orders API lookup is unverified.
- **No refund action on the session detail roster.** The roster has one primary payment per row and no
  room for an amount box; refunding is consequential enough to belong on the page that shows the full
  payment history. "Flag refund requested" stays available there.
- **No live verification.** Everything here is verified by build and test only. The first real refund
  should be a Sandbox one on WX0MIK, which is also what will confirm the `PAYMENTS_WRITE` question
  above.
- **No void/cancel path for a still-`APPROVED` payment — investigated 2026-08-26, not needed.** Mike
  flagged Square's own advice that a card payment sitting `APPROVED` (authorized, not yet settled)
  must be voided via `CancelPayment` rather than refunded, since `RefundPayment` rejects it with
  `REFUND_ERROR_PAYMENT_NEEDS_COMPLETION`. That case cannot happen here: `SquareWebhookHandler`
  (`payment.updated`) discards every event whose status isn't already `COMPLETED`
  (`if (payment.Status != CompletedStatus) return Ignored;`), so a `Payment` never reaches
  `PaymentStatus.Paid`, and an `UnmatchedSquarePayment` row is never created, until Square has already
  told this app the payment settled. Every payment id `RefundService` can ever be asked to refund is
  therefore already `COMPLETED` at Square by construction — there is no code path into this app's own
  data that could hand `RefundPaymentAsync` a still-`APPROVED` id. (The `APPROVED`-then-void concern
  is real for card-present/Terminal or delayed-capture flows; this app only ever takes payment through
  Square-hosted Payment Links, which auto-complete.) Confirmed the error code's wire value against the
  Square SDK (`Square.ErrorCode.RefundErrorPaymentNeedsCompletion` = `"REFUND_ERROR_PAYMENT_NEEDS_COMPLETION"`)
  before concluding this, rather than reasoning from the general advice alone.
