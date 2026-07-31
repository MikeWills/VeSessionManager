# Changelog

Full history of feature/phase pointer entries, newest first. This is the overflow for
`CLAUDE.md`'s Change Log: CLAUDE.md is read in full on every conversation turn, so it only keeps a
small rolling window of the most recent entries (currently capped around 10) plus anything not
already covered by CLAUDE.md's "Current State" phase list; an entry moves here once it ages out of
that window, or immediately if it's phase-numbered work already summarized in "Current State." Full
design rationale for any entry still lives in its linked `/docs/*.md` file, not here or in
CLAUDE.md — this file, like CLAUDE.md's Change Log, is pointers only.

- **License class tracking + exam-result backfill (2026-07-29).** `docs/exam-result-license-class.md`
  — new `Candidate.InitialLicenseClass`/`NewLicenseClass`, derived purely from which exam elements
  ExamTools reports graded+passed this sitting (no FCC `AM.dat` fetch needed — a VE session never
  re-administers an element already credited). Shown on the CandidateDetail page. `ExamResultSyncService`'s
  scan now also re-includes already-`Tested`, non-`Failed` candidates missing the new fields, so every
  current/past/future candidate gets backfilled once via the usual idempotent-field pattern, not a
  one-off script. Also confirmed (no change needed): a passed candidate already gets a distinct
  "done testing" signal via `Tested=true` shown separately from `ApplicationStatus`, which
  deliberately stays `Received` until the FCC watcher later confirms `Granted`.

- **Session ID column + VE roster fix (issues #35/#38, 2026-07-29).** `docs/examtools-api.md`'s
  `export/full.json` section — real prod data (`alpha.exam.tools`) confirmed the wrapper key really
  does differ from dev, more than expected: prod doesn't wrap the VE list under `DEVDOC` at all,
  it's top-level. `VolunteerExaminerSyncService` had found zero VEs for every real HRCC session the
  whole time because of this; `ExamToolsFullExport.ResolveVes()` now checks both shapes. Also added
  a Session ID column to the session list (#35) and converted the VE Roster page's team-pill/plain
  date inputs to the same dropdown pattern as the session list (#38), reusing `IndexModel.DateRangePresets`
  directly rather than duplicating it.

- **Per-team ExamTools host override (issue #18, 2026-07-28).** `docs/examtools-api.md`'s "Per-team
  host override" section — nullable `Team.ExamToolsBaseUrl` override column (not an
  `Team.ExamToolsEnvironment` enum) so a team can point at a different ExamTools host than the
  deployment's global `ExamTools:BaseUrl` default; `ExamToolsCredentials.For(team, ...)` is the one
  place the override-falls-back-to-global logic lives.

- **Payment reminders retest-gating fix (2026-07-22).** `docs/payment-reminders.md`'s "Retest
  payments" section — a same-session retest fee previously never got a reminder or expiration.

- **Youth rate payment confirmation (2026-07-27).** `docs/youth-payment-confirmation.md` — a
  self-service, honor-system public page (`/youth-confirm/{token}`) that switches a candidate's
  standard-rate Square payment link to the session's configured youth rate, replacing reliance on
  the separate Square-hosted page + manual `AmountMismatchFlaggedUtc` reconciliation for the
  in-app-generated case.
- **Stale unpaid Square payment link purge (2026-07-28).** `docs/payment-link-purge.md` — a daily,
  per-team scan (`SquareLinkPurgeJob`) deletes an Unpaid Payment's Square link after
  `Team.PurgeUnpaidLinkDays` (default 30), reusing `ISquareClient.DeletePaymentLinkAsync` from the
  youth-payment feature above.
- **Multi-team users + session list team filter (issues #17/#19, 2026-07-28).** `docs/admin-auth.md`'s
  "Team scoping" section — replaced the single, nullable `User.TeamId` with a real many-to-many
  `UserTeam` join table so a TeamAdmin/SessionManager can belong to more than one team; the session
  list gets a Team column and a `?teamId=` filter-pill (reusing the existing SystemAdmin team-picker
  convention), and `SessionAccessScope`/`AdminAccessScope` moved from scalar equality to
  set-membership (`Contains`) throughout — covered by explicit cross-team-leak regression tests per
  role, not just the new happy paths.
- **Completed-session backfill (issue #22, 2026-07-28).** `docs/examtools-api.md`'s "Stale `"pend"`
  sessions exist" section — `SessionIngestionService` now also first-ingests a `"done"` session up
  to ~30 days past its start (previously never ingested at all), gated by the new
  `Session.HasEnded` helper so `SessionEventSchedulingService`/`CandidateNotificationService` don't
  try to live-schedule or email a session that already happened.
- **FCC upgrade-exam PII purge anchor fix (2026-07-28).** `docs/fcc-uls-watcher.md`'s "Upgrade exam
  handling" section — found live running the FCC daily watcher against real HRCC data (William
  Denney/Jason Pelowitz, both re-detected against an already-old license). FCC's Grant Date doesn't
  change on a class upgrade, so `PiiPurgeService`'s retention Trigger A now anchors on the later of
  `LicenseGrantDateUtc`/`Session.ScheduledStartUtc` (new `Candidate.LicenseGrantPredatesSession()`
  helper) instead of the bare grant date, which would otherwise purge an upgrade/repeat candidate's
  PII almost immediately after their real, current session. No schema change — computed from data
  already stored. Also surfaced on the applicant detail page.
- **Zoom breakout rooms per team (2026-07-28).** `docs/zoom-discord-scheduling.md`'s "Breakout rooms"
  section — new `Team.ZoomBreakoutRoomCount` (admin-editable, default 2) pre-creates that many empty
  "Exam Room N" breakout rooms on every session's Zoom meeting; VEs move candidates in manually.
  Live-tested against a real meeting: despite multiple 2022-2024 Zoom devforum reports that the
  Create Meeting API silently ignores `settings.breakout_room`, it works on this account — confirmed
  by checking the real meeting's Breakout Room Assignment dialog in the Zoom client itself.
- **Post-launch security/quality hardening pass (2026-07-21).** A real cross-tenant IDOR plus five
  smaller fixes. `docs/security-hardening-2026-07-21.md` — the shared helpers it introduced are in
  CLAUDE.md's Established Patterns section.
- **Auto-detect graded exam results from ExamTools (2026-07-28).** `docs/examtools-api.md`'s
  "Applicant exam results" section — found live during a real HRCC test session: a candidate who
  failed his exam that night had no `Tested`/`ApplicationStatus` reflected in the app at all, even
  though ExamTools' own per-applicant detail endpoint (`exams[]`) had the graded result the whole
  time. New `ExamResultSyncService` (wired into `SessionIngestionJob` right after `VeRosterSync`)
  auto-flips a candidate to `Failed` on any graded-and-failed exam element, or `Tested = true` on an
  all-passed result — closing a second latent gap along the way, since `PaymentReminderService`'s
  existing Reason=Retest reminder logic is gated on `ResultMarkedUtc` and had never fired for a
  candidate nobody manually marked Failed.
- **FCC daily watcher catch-up, not exact-instant (2026-07-28).** `docs/fcc-uls-watcher.md`'s
  "Catch-up, not exact-instant" section — the 8am/8pm ET slots exist to make sure FCC has published
  that day's file, but a Worker down right at a slot used to silently wait the full 12h for the next
  one instead of catching up. `FccDailyWatcherJob.LatestDueSlotUtc` now finds the most recent slot
  not yet run (checked against `JobRunHistory`) and runs on the very next hourly tick.
- **Applicant detail page (2026-07-28).** `docs/applicant-detail.md` — a new per-Candidate.Id page
  (never keyed by FRN, since one person can test with the team more than once) with full action
  parity to the session Detail page's candidate row, every payment's link surfaced (closing a
  previously-open TODO item), and an "other sessions with this FRN" cross-reference list. Introduced
  the shared `CandidateEmailHistoryFormatter` helper and fixed two bugs found live-testing in a real
  browser: a nullable-UTC-suffix formatting bug and an off-screen kebab-menu CSS positioning bug.
- **Closed-session ingestion endpoint fix (2026-07-28).** `docs/examtools-api.md`'s "Closed sessions
  are a separate feed" section — issue #22's backfill could never actually fire against real
  data, because `GetTeamSessionsAsync` never returns a closed (`"done"`) session at all; found live
  running the Worker against real HRCC data. New `IExamToolsClient.GetTeamClosedSessionsAsync` calls
  the real date-range endpoint and is merged into `SessionIngestionService`'s feed.
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
