# Youth Rate Payment Confirmation

## Problem

Before this feature, a candidate paying the ARRL youth ($5) rate instead of the standard exam fee
could only do so through a separate, manually-shared Square-hosted checkout page outside this app.
`Payment.Amount` stayed fixed at the standard rate set at registration time; when the $5 payment
came in via webhook, `SquarePaymentMatchingService` still marked it `Paid` but flagged
`AmountMismatchFlaggedUtc` for a Session Manager to manually review (see `docs/square-payments.md`'s
"Amount isn't validated against what's owed" note). That required a human to notice and reconcile
every youth payment by hand, and gave the candidate no in-app way to actually pay the correct amount
up front.

## Design

The registration confirmation email includes a second, youth-specific link alongside the standard
payment link. A candidate who self-identifies as a youth clicks it, lands on a public,
unauthenticated page (`/youth-confirm/{token}`), checks a single "I confirm I am a youth" box, and
submits. The app then:

1. Deletes the candidate's existing standard-rate Square payment link.
2. Generates a new link at the session's configured youth rate.
3. Updates the `Payment` row (`Amount`, `PaymentLinkUrl`, `SquarePaymentReferenceId`,
   `SquarePaymentLinkId`) to match.
4. Redirects the candidate straight into the new Square checkout — no intermediate confirmation
   screen, no follow-up email. The stale standard-rate link in their inbox becomes moot rather than
   needing explicit invalidation in the UI.

This is **honor-system only** — the same trust level the separate Square-hosted page already had.
No age data exists anywhere in this app to verify against, and none is added by this feature. The
page also carries informational copy about the ARRL scholarship reimbursement program (a link to an
existing external claim form — not built or hosted by this app) and a plain-text reminder about the
COPPA consent form ExamTools separately requires for candidates under 13. Both are informational
only: nothing about either is submitted, validated, or stored. An earlier design considered a second
checkbox to self-attest the COPPA form had been sent, with or without a timestamp recorded — this
was deliberately simplified to a single youth-confirmation checkbox with plain COPPA instructional
text, since the point is a reminder, not a record.

## Data model

- **`Payment.YouthConfirmationToken`** (`Guid?`, unique index) — the unguessable lookup key for the
  public page. Deliberately not `Payment.Id`, which is sequential and would let anyone
  enumerate/switch other candidates' payments. Generated once, when the `Payment` row is first
  created (`PaymentGenerationService.RunAsync`/`CreateRetestPaymentAsync`), only for sessions under a
  `Vec` with `SupportsYouthProgram = true` and fee collection enabled — a Payment under a
  non-youth-program `Vec`, or with fee collection disabled, never gets a token, so the youth link is
  simply never useful for it.
- **`Payment.SquarePaymentLinkId`** — Square's own payment-link id, distinct from
  `SquarePaymentReferenceId` (the Order id). Needed because deleting a Square payment link is keyed
  by the link id, not the order id, and nothing captured it before this feature.
- **`FeeConfiguration.YouthExamFeeAmount`** (`decimal?`) — sibling to `ExamFeeAmount`, same
  versioned-by-`EffectiveDate`/per-`Vec` shape. Nullable/unset means the youth flow isn't set up for
  that fee schedule yet, even if `SupportsYouthProgram` is true — surfaced to the candidate as a
  friendly "contact us" message rather than falling back to a hardcoded amount, since the youth rate
  is genuinely VEC-specific (ARRL's $5 is not universal). Editable on the admin Fee Configurations
  screen as a "Youth fee" field, sibling to the existing "Regular fee".

## App-wide public base URL

The confirmation link is built by the **Worker** process (`CandidateNotificationService`, no
`HttpContext` available) and must be an absolute URL back to the **Web** app's own public host. New
`AppOptions.PublicBaseUrl` (bound from `App:PublicBaseUrl` in appsettings), same
global/environment-level shape as `ExamTools:BaseUrl`/`FccUls:BaseUrl` — one deployment serves one
public host even though `Team` is otherwise multi-tenant. Registered and configured identically in
both the Worker (builds the link) and the Web project (the "resend confirmation email" admin action
also builds it, and needs the same value).

## Square client

`ISquareClient` gained `DeletePaymentLinkAsync`, calling the Square SDK's
`Checkout.PaymentLinks.DeleteAsync`. Treats a `NOT_FOUND` error as a no-op success (same idempotent
pattern as `CompleteOrderAsync`'s already-Completed check) so a retried call after a crash is safe.
`SquarePaymentLink` (the domain model returned by `CreatePaymentLinkAsync`) gained an `Id` field —
Square's own payment-link id, captured at creation time so it's available for a later delete.

## `YouthPaymentConfirmationService`

`src/VeSessionManager.Core/Payments/YouthPaymentConfirmationService.cs`. Two entry points:

- `CheckEligibilityAsync(token)` — read-only, no Square calls, used by the page's GET to decide
  whether to render the form or an explanatory message.
- `ConfirmAsync(token)` — the actual switch. Guards, in order: token not found; `Payment.Status`
  isn't `Unpaid` (already paid, or marked `NotApplicable`/expired — deliberately not guarded any
  more tightly than this; a candidate clicking both the standard and youth links is an accepted
  edge case handled manually, not defended against in code); `FeeConfiguration.YouthExamFeeAmount`
  is null; the team's Square isn't configured. If the existing link has a `SquarePaymentLinkId`, its
  delete is **best-effort** — a failure is logged and swallowed, not thrown, so an orphaned
  standard-rate link left live in Square (a manually-cleanable inconvenience) never blocks the
  candidate's youth checkout. The new link's `SquareIdempotencyKey` is generated and persisted
  *before* calling Square, mirroring `PaymentGenerationService.GenerateLinkAsync`'s existing
  crash-safety pattern. On success, an `AuditLog` row is written (`UserId = null`, self-service
  action, same pattern as the PII purge job) noting the fee switch.

### The idempotency key really is persist-once now (fixed 2026-08-03)

The paragraph above described the intent from the start, and the code carried a comment saying so —
but the assignment was `payment.SquareIdempotencyKey = Guid.NewGuid().ToString()`, **unconditional**,
so a fresh key was minted on every attempt. That is the exact "key generated fresh per attempt
(useless)" trap CLAUDE.md's Established Patterns warn to check for, and a comment claiming the
pattern is not evidence of it (audit finding T07).

The failure it allowed: Square accepts `CreatePaymentLink`, the process dies before the save at the
end of the method, the Payment is still `Unpaid` so the page lets the candidate confirm again — and
the retry mints a *different* key, producing a second live Square order with the first orphaned and
still payable.

The fix is not simply `??=`, because the key already on the Payment belongs to the **standard-rate**
link: reusing that would make Square replay the standard link at the standard price. So the key is
cleared in the same block that deletes the standard link (both `SquarePaymentLinkId` and
`SquareIdempotencyKey` go to null together, and that clearing is what makes the `??=` safe), then
`??=` generates and persists a youth-attempt key before Square is called. A retry then finds the key
already set, sends the same one, and gets an idempotent replay of the same link. A crash *during*
the delete leaves both fields as they were, and the delete is retried — harmlessly, since it is
already best-effort and 404-tolerant.

No new field tracks "this candidate confirmed youth status" beyond what already exists:
`Payment.Amount` correctly reflecting the youth rate *is* the record — financial reports already
read `Amount`.

## Public page

`src/VeSessionManager.Web/Pages/Public/YouthConfirm.cshtml(.cs)`, route `/youth-confirm/{token:guid}`.
No `[Authorize]`, `_PublicLayout`, following the same pattern as `Pages/Index.cshtml`/`Pages/Privacy.cshtml`.
GET renders the form (or an explanatory message per `CheckEligibilityAsync`'s outcome) without any
mutation. POST validates the single required checkbox server-side, calls `ConfirmAsync`, and on
success issues a redirect straight to the new Square checkout URL — never a page the candidate has
to click through.

The page's ARRL-scholarship and COPPA-instruction copy is a placeholder (marked `TODO` in the
markup) — same "real starting example, not final copy" caveat as the seeded email template bodies
elsewhere in this app. The scholarship reimbursement form itself is an existing external
link/document, not built or hosted here.

## Email template plumbing

`EmailTemplatePlaceholders.ByKey["RegistrationConfirmation"]` gained `YouthPaymentLinkUrl`.
`CandidateNotificationService` populates it (in both `SendRegistrationConfirmationsAsync` and
`ResendRegistrationConfirmationAsync`) as `{PublicBaseUrl}/youth-confirm/{token}` when the session's
`Vec.SupportsYouthProgram` and the `InitialExam` Payment has a token; otherwise blank. A Team's
template body for a session under a non-youth-program `Vec` just renders a blank line for this
token — there's no conditional-block templating engine here to hide it automatically, so template
copy needs to be written with that in mind (same as every other optional placeholder in this app).

The pre-existing `ArrlYouthProgramInstructions` template and `SendYouthProgramInstructionsAsync`
manual action are unrelated and unchanged — that's a separate, already-built, **post-exam** "how to
claim your scholarship reimbursement" email for candidates who already passed and have a `CallSign`.
This feature is entirely **pre-payment**.

## Known limitations / accepted risk

- No age verification of any kind — honor system, same as the app already had via the separate
  Square-hosted page this feature is meant to reduce reliance on (not retire — a candidate can still
  reach that page directly and underpay; `AmountMismatchFlaggedUtc` remains the backstop for that
  separate path).
- A candidate who clicks both the standard and youth links (in either order) is not specially
  guarded against — the `Status != Unpaid` check naturally blocks the youth flow once either payment
  completes, and any messier races are handled manually rather than defended against in code, per
  product decision.
- The old standard-rate Square link's deletion is best-effort; a failed delete leaves the old link
  live (clickable, but pointing at a stale amount) until manually cleaned up in the Square dashboard.
