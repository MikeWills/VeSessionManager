# Stale Unpaid Square Payment Link Purge

See also `docs/square-payments.md` (Square integration overview) and
`docs/youth-payment-confirmation.md` (which introduced `ISquareClient.DeletePaymentLinkAsync` and
`Payment.SquarePaymentLinkId`, both reused here).

Square payment links never auto-expire. Left alone, an Unpaid `Payment`'s link stays live and
clickable forever, even long after a session has come and gone — confirmed while researching this
feature, not assumed. `SquarePaymentLinkPurgeService` (`src/VeSessionManager.Core/Payments/`) +
`SquareLinkPurgeJob` (`src/VeSessionManager.Worker/`) close that gap with a daily, per-team,
scan-based pass, same shape as every other job in this app: diff stored state against a threshold,
use a tracking field as both the query filter and the idempotency guard.

## The one pass

For each Team, `SquarePaymentLinkPurgeService.RunAsync`:

1. Skips quietly (one aggregate `INFO` log line) if `!team.IsSquareConfigured` — same
   optional-integration posture as `PaymentGenerationService` and every other Square-touching
   service. `SquareLinkPurgedUtc` stays null, so the backlog purges automatically once configured.
2. Queries `Payment`s that are `Status == Unpaid`, still have a `PaymentLinkUrl`, haven't already
   been purged (`SquareLinkPurgedUtc == null`), and whose `CreatedUtc` is older than
   `Team.PurgeUnpaidLinkDays` (default **30**, admin-configurable per team — see Config below).
3. For each: if `SquarePaymentLinkId` is set, calls `ISquareClient.DeletePaymentLinkAsync` (added
   for the youth-payment-confirmation feature; treats a `NOT_FOUND` response as a no-op success,
   same idempotent pattern as `CompleteOrderAsync`'s already-Completed check). On success — or if
   `SquarePaymentLinkId` was never set at all (see the known gap below) — nulls
   `PaymentLinkUrl`/`SquarePaymentReferenceId`/`SquarePaymentLinkId` and sets `SquareLinkPurgedUtc`.
   On a genuine delete failure, the row is left untouched and retried on the next poll — standard
   per-item try/catch-and-continue, same as `PaymentReminderService`'s passes.
4. Saves after every item, so a crash mid-run or one item's failure never loses progress already
   made on others.

## Two gotchas resolved by design, not left as open questions

These were the two open questions `TODO.md` had parked (2026-07-23) alongside the threshold
question (now resolved: per-Team configurable, default 30 — see Config below).

- **Clearing our own DB reference, not just Square's side.** A purge that only calls Square's
  delete API without nulling `Payment.PaymentLinkUrl`/`SquarePaymentReferenceId` would leave the
  "Email history" modal and "Resend confirmation email" action still showing/sending a link that
  404s on Square. Step 3 above clears both together, in the same save.
- **The auto-regen loop.** `PaymentGenerationService`'s existing "Unpaid + no link → generate a new
  one" scan (`PaymentGenerationService.cs`'s `paymentsNeedingLink` query) would otherwise see a
  purged Payment's now-null `PaymentLinkUrl` and silently regenerate a fresh link on the very next
  poll — purge → regenerate → wait → purge again, forever. Fixed by adding
  `Payment.SquareLinkPurgedUtc` as a new field, checked in *both* places: it's the idempotency guard
  for the purge scan itself, and an explicit exclusion in `PaymentGenerationService`'s query.

## Known, accepted gap

A `Payment` whose link was generated **before** `SquarePaymentLinkId` existed (i.e. before the
youth-payment-confirmation migration) has no id to call Square's delete API with. For those rows,
the purge still clears the local `PaymentLinkUrl`/`SquarePaymentReferenceId` and sets
`SquareLinkPurgedUtc` (so the app stops offering/resending the link), but the link itself stays
live on Square's side — a `WARNING` log line is emitted so this is visible, not silent. This is a
one-time gap for pre-existing rows only; every `Payment` created after that migration always has
`SquarePaymentLinkId` set alongside `PaymentLinkUrl`.

## Config

- **`Team.PurgeUnpaidLinkDays`** (`int`, default `30`) — per-team, admin-editable on the Team
  Settings page (`Pages/Admin/TeamSettings.cshtml`'s "Payment link purge" section). Unlike every
  other numeric field on `Team` (e.g. `SmtpPort`, nullable with a code-level fallback, since those
  are credential-adjacent and null means "unset"), this stores a real default — it's a genuine
  per-team business setting, not a credential. The SQL column default is also `30` (set via
  `HasDefaultValue` in `AppDbContext`, not just the C# property initializer), so a row inserted
  outside EF or updated by the migration itself for pre-existing teams still gets `30`, not `0`.
- **`Jobs:SquareLinkPurgeIntervalHours`** (appsettings, default `24`) — global job cadence, same
  24-hour `PeriodicTimer` idiom as `PaymentReminderJob`/`DayBeforeReminderJob`. Not pinned to a
  specific wall-clock time; an extra same-day tick is a no-op thanks to `SquareLinkPurgedUtc`.
