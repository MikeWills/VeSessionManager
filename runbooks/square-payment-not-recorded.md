# Runbook — "The candidate says they paid, and the app says unpaid"

**When:** a payment shows as outstanding after the candidate has a Square receipt; or reminders keep
going out to somebody who has paid.

**Why it works this way:** [`docs/square-payments.md`](../docs/square-payments.md),
[`docs/reconciliation.md`](../docs/reconciliation.md).

⚠️ **This failure is silent by design of the parties involved.** Square retries the webhook, gives
up, and payments stop being recorded with **nothing logged on this side**. The first symptom is a
candidate insisting they paid.

---

## 1. Was it a payment against one of our links, or a separate page?

This team also takes some payments through a Square-hosted page that is not one of
`PaymentGenerationService`'s links.

- **Admin → Reconciliation** and the **Unmatched payments** screen list money Square knows about
  that this app could not attach to a candidate. If it is there, **match it** (or dismiss it, if it
  genuinely is not ours) — that is the intended resolution, not a manual "mark paid".
- The nav **bell** carries reconciliation alerts and links straight at the row (`?highlight=<id>`).

## 2. Is the webhook subscription in the right Square mode?

The single most common cause. Subscriptions are **separate per Sandbox/Production, each with its own
signature key**:

- A subscription registered under **Production** receives **zero delivery attempts** for Sandbox
  events — not a 401, no attempt at all.
- Reusing one mode's signature key against the other mode's subscription makes every delivery fail
  signature verification (**401**) even though the URL and event config look correct.

Check, in the Square dashboard, that all of these are the same mode, and that `Team.SquareEnvironment`
agrees with them:

| Value | Where |
|---|---|
| Access token | Developer Dashboard → Credentials (Sandbox **or** Production tab) |
| Location ID | Locations tab, **same mode as the token** |
| Webhook subscription (with `payment.updated` checked) | Webhooks → Subscriptions, **same mode** |
| Webhook signature key | That subscription's Endpoint Details → Show |
| `Team.SquareEnvironment` | Admin → Team Settings → Square |

Two teams on one deployment can legitimately be in **different** modes — this is per-team, so check
the team in question, not "the deployment".

## 3. Is the notification URL character-for-character right?

The notification URL is a **literal input to the HMAC signature**, not just where Square happens to
POST. A trailing slash difference is enough to fail every delivery. It must match exactly between
the Square subscription and `Team.SquareWebhookNotificationUrl`.

## 4. Is the endpoint reachable and anonymous?

`POST /webhooks/square` is a minimal-API endpoint, and the app has an authorization
**`FallbackPolicy`** — which applies to minimal-API endpoints too, not just Razor Pages. It carries
an explicit `.AllowAnonymous()` for exactly this reason.

⚠️ You **cannot** probe this with a status code: the handler answers a missing signature with
**401**, the same status authorization produces. A 401 from a bare `curl` proves nothing either way.
The endpoint's exemption is asserted on endpoint metadata in `PageSmokeTests`, not by probing.

Check Square's own **delivery log** for the subscription — that tells you whether Square is even
attempting, and what it got back.

## 5. Symptoms that point elsewhere

| Symptom | Likely cause |
|---|---|
| Payment links **fail to generate** for one team | A Production token being rejected by the Sandbox host (or vice versa) — `Team.SquareEnvironment` disagrees with the credentials. Shows up as failed link generation in Job Run History |
| The candidate has **no link at all** | Square unconfigured for that team — it skips quietly and `PaymentLinkUrl` stays null, so the next poll retries once configured |
| A **refund** was issued and the fee still shows as owed | Refunds deliberately do **not** move a Payment off `Paid` (otherwise the "unpaid and no link" scan reissues a checkout link). The VEC remittance figure nets refunds — but only when `Payment.Refunds` is loaded. See [`docs/square-refunds.md`](../docs/square-refunds.md) |
| A refund "succeeded" but the money has not moved | A Square refund returns `PENDING` for up to **14 days** on card/bank transfer. Read `Refund.Status`, not the fact that the call was made |

## Resolution of last resort

"Mark paid manually" exists on the applicant detail page. Prefer **matching the unmatched payment**
where one exists — that keeps the money and the candidate attached to each other for the VEC fee
arithmetic. A manual mark with no Square record behind it is the thing that makes reconciliation
findings later.
