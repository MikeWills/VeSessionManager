# Applicant detail page

`Pages/SessionManager/CandidateDetail.cshtml(.cs)` — a dedicated per-applicant page, linked from
the candidate name on the session Detail page's roster table. Requested 2026-07-28 so a Session
Manager can click into one candidate's full record (contact info, FRN/callsign, application status
timeline, every payment with its link, email history) instead of only seeing a summarized row.

## Keyed by Candidate.Id, not FRN

Explicit requirement: a real person can test with the same team more than once (retest, or a
future session) — each registration is its own `Candidate` row sharing the same FRN, not one merged
record. The page's route is `Candidate.Id`; it never looks anything up by FRN as a primary key.

Instead, the page shows a **"Other sessions with this FRN"** read-only cross-reference section —
every other `Candidate` row in the same team sharing this one's FRN, each linking to its own
detail page. This gives full visibility into someone's history without conflating separate
attempts into a single record.

## Action parity with the session Detail page

Every write action available on the session Detail page's candidate row (resend confirmation,
mark failed, delete/withdraw, add/edit FRN, mark paid, flag refund, create retest payment, send
Youth Program instructions) is also available here, reusing the exact same Core services
(`CandidateActionService`/`CandidateNotificationService`) — this page owns no business logic of
its own, same convention as `Detail.cshtml.cs`. Authorization mirrors `Detail.cshtml.cs`'s pattern
but keyed off the candidate's own session (loaded via `Candidate.Session`) rather than a route-level
session id, since this page's own route parameter *is* the candidate id.

One deliberate improvement over the session Detail page: **every payment is shown, not just the
primary one** — each with its own "Mark paid manually"/"Flag refund requested" actions and its own
`PaymentLinkUrl`. This also closes the previously-open TODO item "surface `Payment.PaymentLinkUrl`
somewhere in the UI" (it was only ever visible via direct DB query before).

## Shared helper: `CandidateEmailHistoryFormatter`

The "what has this candidate actually received" email history list was originally private logic
inside `Detail.cshtml.cs`. Since this page needed the identical list, it was extracted to
`Web/CandidateEmailHistoryFormatter.cs` (a static `Build(Candidate)` method + `EmailHistoryLine`
record) — both pages now call the one shared implementation instead of drifting apart, per this
repo's established shared-helper convention.

## Bugs found and fixed while building this

- **Nullable UTC-suffix formatting bug.** `value?.ToString(...) + " UTC"` only short-circuits the
  `ToString()` call — the `+ " UTC"` still runs unconditionally, so a null `DateTime?` produced a
  bare `" UTC"` string instead of staying null. Caught live rendering this page against a candidate
  with no result marked yet (showed a "Result marked: UTC" line with no date). Fixed with a
  `FormatUtcOrNull` helper that wraps the whole expression in one null check.
- **Kebab menu opened off-screen.** The shared `.menu` CSS (`position: absolute; right: 0`)
  assumes its positioned ancestor is a wide, right-aligned container — true for `.row-actions`
  (a table `<td>`) and `.nav-group`, both already-established usages. This page's page-level action
  kebab sits in `.panel-actions`, a narrow flex container near the page's left margin, so the same
  `right: 0` pushed the 260px-wide menu off the left edge of the viewport. Fixed by (1) wrapping the
  kebab+menu pair in a `.row-actions` div (which already has the needed `position: relative`) and
  (2) adding a `.panel-actions .menu { left: 0; right: auto; }` override in `app.css`, mirroring the
  existing `.nav-group .menu` override for the identical problem.

Both were caught by actually loading the page in a real browser against real HRCC data — a good
example of why `dotnet build`/`dotnet test` passing isn't the same as a UI feature actually working.
