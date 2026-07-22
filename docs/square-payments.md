# Square Payment Links + Webhook (Phase 3)

What `PaymentGenerationService` and `SquareWebhookHandler` (`VeSessionManager.Core/{Payments,Square}/`)
rely on. Like Zoom/Discord, these are official, documented APIs — this records the exact shapes
and account setup this codebase depends on, with sources.

**Square is optional.** Unlike ExamTools/Zoom/Discord, nothing else in the app depends on it —
`ISquareClient.IsConfigured` (true once `Square:AccessToken` is set) gates whether
`PaymentGenerationService` even attempts a link. Skip this whole doc for now if you're not ready
to set Square up yet; `Payment` rows still get created normally (`Unpaid`, no link) and will
back-generate their links automatically, with no other config change, the moment credentials are
added later.

## Account Setup (one-time)

Account-dashboard setup only the account owner can do — not runnable from this repo.

1. Sign into the [Square Developer Dashboard](https://developer.squareup.com/) → **+ New
   Application** → name it (e.g. "VE Session Manager") → **Create Application**.
2. **Credentials** tab, **Sandbox** mode → **Show** the Sandbox Access Token → this is
   `Square:AccessToken` for local dev (`Square:Environment = Sandbox`). There's a separate
   **Production** mode token for real payments — see the note below before using it.
3. **Locations** tab → copy the Test Location ID (sandbox) or Live Location ID (production) →
   this is `Square:LocationId`.
4. **Webhooks** → **Subscriptions** → **Add subscription** → name it, enter the notification URL
   (must be HTTPS and publicly reachable — see the local-testing note below), pick an API version,
   check the **payment.updated** event → **Save**. This is `Square:WebhookNotificationUrl` —
   **must match exactly**, since it's a literal input to the HMAC signature, not just where Square
   happens to POST.
5. Open the subscription you just created → **Endpoint Details** → **Show** the Signature Key →
   this is `Square:WebhookSignatureKey`.

**Sandbox vs Production:** Sandbox uses fake test cards and never touches real money — safe for
end-to-end testing. Moving to `Square:Environment = Production` needs the application activated
in the dashboard with a real linked bank account; do that deliberately, not as a side effect of
flipping a config value.

**Local webhook testing:** Square requires HTTPS and a publicly reachable URL — `localhost` won't
work as a notification URL. Use a tunnel (e.g. `ngrok http https://localhost:5158`) and register
*that* URL as the subscription's notification URL for local testing; update
`Square:WebhookNotificationUrl` to match whenever the tunnel URL changes (it's not stable across
restarts unless the tunnel tool is configured for a fixed subdomain).

## Payment Links (Checkout API)

Client: `VeSessionManager.Core/Square/SquareClient.cs`, wrapping the official
[Square .NET SDK](https://github.com/square/square-dotnet-sdk) (`Square` on NuGet). Uses the
**Order**-based request shape, not QuickPay — only `Order` supports `ReferenceId`
([confirmed](https://developer.squareup.com/reference/square/objects/Order)), which the spec
calls for.

- `client.Checkout.PaymentLinks.CreateAsync(new CreatePaymentLinkRequest { Order = new Order { LocationId, ReferenceId, LineItems = [...] } })`.
  `ReferenceId` is set to the `Payment` row's own id — visible in Square's dashboard/reporting for
  cross-referencing, but **not** the join key the webhook uses (see below).
- `OrderLineItem.Quantity` is a **string** (`"1"`), not a number — easy to get wrong.
- `Money.Amount` is the integer count of the currency's smallest unit — cents for USD.
  `SquareClient` converts from `Payment.Amount` (decimal dollars) via
  `Math.Round(amountUsd * 100m, MidpointRounding.AwayFromZero)`.
- Response: `response.PaymentLink.{Id, OrderId, Url, LongUrl}`, plus a `response.Errors` list that
  can be populated even on a non-throwing call — `SquareClient` checks it and throws with the
  detail if non-empty, same reasoning as `docs/zoom-discord-scheduling.md`'s Zoom error-body fix.

## Webhook (`payment.updated`)

Handler: `VeSessionManager.Core/Square/SquareWebhookHandler.cs`. Endpoint:
`VeSessionManager.Web/SquareWebhookEndpoint.cs` (`POST /webhooks/square`).

- Signature: `x-square-hmacsha256-signature` header. Verified via the SDK's own
  `Square.WebhooksHelper.VerifySignature(rawBody, signatureHeader, signatureKey, notificationUrl)`
  — real HMAC-SHA256 over `notificationUrl + rawBody`, base64-encoded. Confirmed by decompiling
  the installed package's XML docs (an earlier web summary named a different, nonexistent method,
  `IsValidWebhookEventSignature` — don't trust that name) and by the test suite computing the same
  HMAC independently and having the SDK's real verifier accept it.
- **The raw body must be read before anything else touches it** — any JSON model binding upstream
  would consume the request stream and invalidate the signature check, which needs the exact bytes
  Square signed.
- **`reference_id` is not in the webhook payload** — only `data.object.payment.order_id` and
  `.status`. This is a deviation from a literal reading of the spec's "Include the Payment row's
  ID... as the reference ID... so retest payments and initial payments are distinguishable in the
  webhook": distinguishing still works, just via `order_id` instead. Each `Payment` gets its own
  `Order` (hence its own unique `order_id`) when its link is created, and that `order_id` — not
  the `Order.ReferenceId` — is what gets stored in `Payment.SquarePaymentReferenceId` and matched
  against on webhook receipt.
- Unconfigured `WebhookSignatureKey`/`WebhookNotificationUrl` make the SDK's verifier throw
  `ArgumentNullException` — `SquareWebhookHandler` checks for both up front and treats them the
  same as an invalid signature (401), rather than 500ing on every webhook attempt before setup is
  finished.
- Processing (a single indexed `Payment` lookup + update) is fast enough to run inline in the
  request — no background queue, unlike Square's own suggestion for "heavy processing." Idempotent
  against duplicate webhook deliveries: a `COMPLETED` event for an already-`Paid` `Payment` is a
  no-op (`Ignored`), not an error.

## Unmatched payments (post-launch addition)

Handler: `VeSessionManager.Core/Payments/SquarePaymentMatchingService.cs`. This team also takes
some payments through a separate Square-hosted page that isn't one of `PaymentGenerationService`'s
own generated links — a `COMPLETED` event for one of those has no matching `Payment.
SquarePaymentReferenceId`, so `SquareWebhookHandler` hands it off here instead of discarding it.

- **Email fallback match:** if Square's payload includes `buyer_email_address`
  ([present on the `Payment` object](https://developer.squareup.com/reference/square/objects/Payment)
  when checkout collected one), look for exactly one candidate on the team with a
  case-insensitive-matching `Email` and an outstanding `Unpaid` payment. Zero or multiple matches
  (e.g. a shared family email) don't guess — fall through to manual review, same as everywhere
  else in this app that would otherwise have to pick between ambiguous candidates.
- **Manual review:** no match (or no email in the payload at all) persists an
  `UnmatchedSquarePayment` row — `TeamId`, `SquareOrderId`, `SquarePaymentId`, `AmountUsd`,
  `BuyerEmailAddress`, `ReceivedUtc`. Unique `(TeamId, SquareOrderId)` index means a webhook
  redelivery for a still-unresolved row is a no-op, not a duplicate.
- A Session Manager resolves it on `/SessionManager/UnmatchedPayments` — the "match to candidate"
  dropdown is scoped to candidates with an outstanding `Unpaid` payment (the same eligibility rule
  the auto-match query itself uses), and always applies to that candidate's most recent `Unpaid`
  `Payment` row (same "one primary payment" simplification the Session Manager candidate-actions
  UI already uses elsewhere).
- Both the auto-match and manual-match paths set `Payment.SquarePaymentReferenceId` to the
  unmatched event's `order_id` and flip `Status` to `Paid` — from that point on, a *second*
  webhook event for the same `order_id` (e.g. a later `payment.updated` with more detail) matches
  normally through the primary lookup in `SquareWebhookHandler.ProcessAsync`, no special-casing
  needed.
- **Amount isn't validated against what's owed — deliberately.** The separate Square-hosted
  checkout page only offers two amounts, ARRL's $5 youth rate and $15 standard rate, but
  `Payment.Amount` is always set to the $15 standard rate at registration time — youth status
  isn't known until confirmed at test-day check-in, so a legitimate youth candidate paying $5
  through this page is a routine, expected outcome, not an error. Both match paths still mark the
  `Payment` `Paid` when the amount doesn't match, but record `Payment.SquareAmountPaidUsd` (the
  actual amount Square reported) and set `Payment.AmountMismatchFlaggedUtc`, surfaced as an
  amber "Paid $X against $Y owed" tag next to that candidate's payment chip on the session detail
  page — a Session Manager reviews it and follows up (e.g. collects the balance) if youth status
  doesn't hold up at test time. `SquareAmountPaidUsd` is null for a `Payment` matched the normal
  way (`SquareWebhookHandler`'s own `order_id` lookup), since that amount was already fixed by
  this app's own generated payment link.

## Order completion (post-launch addition)

`ISquareClient.CompleteOrderAsync` (Square [Orders API](https://developer.squareup.com/reference/square/orders-api),
`Get` then `Update` to `State = Completed`) automates this team's existing manual practice: once a
Square order is both paid and the session it's for has actually happened, mark it Completed so it
doesn't stay open in the Square dashboard indefinitely. Idempotent by design — a request against an
order already `Completed` is a no-op, so a caller doesn't need to guard against calling it twice.

`Payment.SquareOrderCompletedUtc` tracks whether this has happened. `SquarePaymentMatchingService`'s
private `CompleteOrderIfEligibleAsync` (requires `Status == Paid`, `SquarePaymentReferenceId` set,
`Session.TestingCompletedUtc` set, and `Team.IsSquareConfigured`) is the one eligibility check,
called from both directions since either can happen second:

- Right after a payment is matched (`ApplyMatchAsync`, from either the auto-match or manual-match
  path above) — covers "payment arrives after the session is already marked completed."
- Right after `SessionActionService.MarkCompletedAsync` flips a session to completed
  (`CompleteEligibleOrdersForSessionAsync`, scans that session's already-`Paid` payments) — covers
  "payment arrived and was matched before the session was marked completed."
- Right after the **normal** webhook match (`SquareWebhookHandler.ProcessAsync`'s primary
  `order_id` lookup, not the unmatched-order fallback above) marks a `Payment` `Paid` — via
  `SquarePaymentMatchingService.TryCompleteOrderAsync`, a thin public wrapper around the same
  private eligibility check. Covers "the session was already marked completed before this payment's
  own webhook arrived," the mirror image of the previous bullet.

A completion failure is logged (`SquareOrderCompletedUtc` stays null) but never blocks or unwinds
the `Payment`/session state, which is already correctly saved either way — no scan-based job
retries a failed completion today, so it needs a human to notice and complete the order manually in
the Square dashboard if it matters.
