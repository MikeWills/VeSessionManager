# Changelog

Full history of feature/phase pointer entries, newest first. This is the overflow for
`CLAUDE.md`'s Change Log: CLAUDE.md is read in full on every conversation turn, so it only keeps a
small rolling window of the most recent entries (currently capped around 10) plus anything not
already covered by CLAUDE.md's "Current State" phase list; an entry moves here once it ages out of
that window, or immediately if it's phase-numbered work already summarized in "Current State." Full
design rationale for any entry still lives in its linked `/docs/*.md` file, not here or in
CLAUDE.md — this file, like CLAUDE.md's Change Log, is pointers only.

- **FCC ULS stale/dismissed-application matching fix (2026-07-22).** `docs/fcc-uls-watcher.md`, found
  via a live real-FRN lookup.
- **TeamLead read-only view (2026-07-22).** Closed the Phase 9d self-audit gap. `docs/admin-auth.md`'s
  "TeamLead read-only view" section — new `SessionAccessScope.CanView` distinct from `CanEdit`.
- **Square unmatched-payment matching + order completion (2026-07-22).** `docs/square-payments.md`'s
  "Unmatched payments"/"Order completion" sections — includes why payment amount is deliberately not
  validated against what's owed.
- **Candidate ingestion scheduling, redesigned (2026-07-21, redesigned 2026-07-23).**
  `docs/candidate-refresh.md` — flat per-team polling interval + an on-demand "Refresh candidates"
  button, replacing an earlier "surge polling near session start" design.
- **Deployment-wide email test mode (2026-07-21).** `docs/test-mode.md`.
- **"Email history" candidate modal (2026-07-23).** First place any email-sent timestamp was ever
  surfaced outside the DB. `docs/email-reference.md`'s "Checking what a candidate actually received"
  section.
- **FCC daily watcher same-day retry (2026-07-23).** `docs/fcc-uls-watcher.md`'s "Same-day retry" and
  "Weekly complete snapshot lags real filings" sections — found via a live FRN re-lookup that a
  missed daily tick wasn't recovered for a full week, and that the weekly catch-up's "complete"
  snapshot lags real filings by 24+ hours so it isn't the backstop it looks like.
- **Public privacy page + Phase 9 polish (2026-07-21).** Built `/Privacy` (dynamic PII retention
  window) and restyled the scaffold-default auth pages to match the design system — no dedicated
  doc; see git history for `Pages/Privacy.cshtml`/`Pages/Account/*` if detail is ever needed.
- **PII purge job (Phase 10, final phase, 2026-07-21).** `docs/pii-purge.md` — global, not per-team;
  two independent triggers (passed/failed) share one purge action.
- **Admin config screens (Phase 9c, 2026-07-21).** `docs/admin-config-screens.md` — one shared
  `Pages/Admin/` set for SystemAdmin+TeamAdmin, new `SystemSettings` singleton row and
  `AdminAccessScope`.
- **Session Manager candidate actions (Phase 9b, 2026-07-21).** `docs/session-manager-ui.md` —
  business logic in three Core services, pages are thin wiring.
- **Admin backend auth (Phase 9a, 2026-07-21).** `docs/admin-auth.md` — four-role model
  (SystemAdmin/TeamAdmin/SessionManager/TeamLead), `SessionAccessScope`, Identity migration.
- **VEC submission tracker (Phase 8, 2026-07-21, renamed from "ARRL submission tracker").**
  `docs/vec-submission-tracker.md` — no background job, pure logic + `AuditLog`.
- **VE tracking (Phase 7, 2026-07-20).** `docs/ve-tracking.md` — fully automatic via ExamTools'
  `full.json` export; the one scan-based service that actively reconciles removals, not just
  additions.
- **Multi-team foundation + fast-follow (2026-07-20).** `docs/multi-team.md` — the per-team client
  pattern every future external API client should follow (see CLAUDE.md's Established Patterns).
  Every new `Team` credential column is left `NULL` by its own migration; see `TODO.md`'s per-team
  setup checklist.
- **Payment reminders/expiration (Phase 6).** `docs/payment-reminders.md`.
- **FCC ULS watcher (Phase 5).** `docs/fcc-uls-watcher.md`, including the live-verified
  field-position gotcha (see CLAUDE.md's Known Constraints).
- **Candidate notification emails (Phase 4).** `docs/email-reference.md` is the current full
  reference (recipient/trigger/placeholders for all six templates that exist today);
  `docs/email-notifications.md` has the original Phase 4 setup notes.
- **Square payment links + webhook (Phase 3).** `docs/square-payments.md`.
- **Zoom + Discord event scheduling (Phase 2).** `docs/zoom-discord-scheduling.md` — Discord's bot
  token is uniquely shared across all teams (only the target Guild varies per team), an explicit
  exception to the per-team pattern, since one bot identity can legitimately serve multiple guilds.
- **ExamTools session/candidate ingestion (Phase 1).** `docs/examtools-api.md`; runnable requests in
  `api-examples/` (Bruno).
