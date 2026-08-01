# Changelog

Full history of feature/phase pointer entries, newest first. This is the overflow for
`CLAUDE.md`'s Change Log: CLAUDE.md is read in full on every conversation turn, so it only keeps a
small rolling window of the most recent entries (currently capped around 10) plus anything not
already covered by CLAUDE.md's "Current State" phase list; an entry moves here once it ages out of
that window, or immediately if it's phase-numbered work already summarized in "Current State." Full
design rationale for any entry still lives in its linked `/docs/*.md` file, not here or in
CLAUDE.md — this file, like CLAUDE.md's Change Log, is pointers only.

- **Team-picker `<select>` first-click bug + SystemAdmin single-team default + FCC license link
  (2026-07-30).** No linked doc. Bug: `ApplicantStatus`/`VeRoster`'s team `<select onchange>` never
  set an explicit `selected` option when no team was chosen yet (SystemAdmin's default state), so
  the browser silently pre-selected the first team in the list while the model still read
  `TeamId = null` — clicking that same (already-displayed) team didn't fire `onchange` at all until
  a *different* team was picked first. `ApplicantStatus` now uses the same filter-pill `<a>`
  pattern as `VecSubmission`/`UnmatchedPayments` (always a real navigation); `VeRoster` (whose
  `<select>` shares a form with the date-range filter, so pills weren't a drop-in fix) instead gets
  an explicit "Select a team…" placeholder option so the visible and actual state always match.
  Also: `SessionAccessScope.TryResolveViewableTeamId` now takes the already-fetched
  `AvailableTeams` list and defaults SystemAdmin to the sole team when a deployment only has one
  (previously only non-SystemAdmin roles auto-defaulted — a single-team SystemAdmin had no picker
  to make a choice with and no default either, a dead end). `AdminAccessScope.TryResolveManageableTeamId`
  deliberately keeps its own different null-means-"show every team merged" behavior, unchanged.
  Separately: new `Candidate.FccUlsLicenseKey` (the FCC ULS "Unique System Identifier", set by
  `FccUlsWatcherService` alongside `CallSign`/`LicenseGrantDateUtc`) powers a "(FCC license ↗)" link
  next to Call sign on the Candidate Detail page — confirmed live that ExamTools itself links to
  this exact `wireless2.fcc.gov/UlsApp/UlsSearch/license.jsp?licKey=...` URL shape. The equivalent
  *pending application* deep link was deliberately **not** built — `wireless2.fcc.gov`'s Application
  Search pages returned Akamai "Access Denied" for both automated and the user's own manual browser
  requests while investigating, so the URL shape couldn't be verified; see TODO.md's Feature
  requests section for the parked follow-up (the `UniqueSystemIdentifier` this needs is already
  captured in `FccUlsApplicationRecord`, just not persisted to `Candidate` yet).
- **Duplicate-code cleanup + credential encryption + two security fixes (2026-07-30).** No single
  linked doc — three small, independent items from a full-project code review: (1) `SessionAccessScope.GetAvailableTeamsAsync`
  replaced 5 copy-pasted team-picker blocks across the SessionManager pages; (2) `LicenseClassFormatter`
  replaced two independently-drifted license-class-transition formatters (`CandidateDetail`/`ApplicantStatus`);
  (3) `PerTeamDailyJob` (Worker) replaced the identical 24h-PeriodicTimer-per-team scaffold duplicated
  across `PaymentReminderJob`/`SquareLinkPurgeJob`/`DayBeforeReminderJob`. Plus: `ExternalLoginCallback`'s
  email-verification check now fails closed for any unrecognized external provider instead of
  trusting one by silent omission (Microsoft is explicitly allowlisted, with rationale, not
  accidentally trusted); the auth cookie now pins `CookieSecurePolicy.Always` outside Development.
  See `docs/credential-encryption.md` for the fourth, larger item from the same review — `Team`'s
  credential columns (ExamTools/Zoom/Square/SMTP secrets) are now encrypted at rest via
  `EncryptedStringConverter`, with `TeamSecretsMigrationService`/`--migrate-team-secrets` as the
  (idempotent, safe-to-rerun) upgrade path for existing plaintext data.
- **FCC ULS *application* deep link — investigated to a conclusion, closed as not buildable
  (2026-07-31).** See the comment in `Web/FccUlsLinks.cs` and `docs/uls-watcher.md`. Three independent
  blockers, any one of them sufficient: FCC's Application Search results page is **session-scoped**
  (`results.jsp?applSearchKey=applSearchKey2026…`), so there is no stable URL to build *even with full
  access*; `wireless2.fcc.gov/UlsApp/ApplicationSearch/*` returns Akamai 403 to this operator including
  from other VPN exits; and an application record's own Unique System Identifier — the plausible key —
  **isn't exposed by the ULS lookup API at all**, only `uls_filenumber`, which is stored on `Candidate`
  for paste-in reference. The *licence* link (`UlsSearch/license.jsp?licKey=`) is unaffected and
  verified working. Don't reopen this without a new fact; the previous framing ("just observe a working
  URL once") was wrong, because blocker 1 means no such durable URL exists.
- **Zoom/Discord cleanup stopped repeating an `[ERR]` every tick (2026-07-29, commit `b6cbfc0`).** No
  linked doc. A cancelled session with a `ZoomMeetingId` on a team whose Zoom credentials were never
  set threw `InvalidOperationException` on every poll forever (12 times in one day locally) — against
  this app's own "one quiet log line, never a repeating ERROR" pattern.
  `CleanupZoomAndDiscordAsync` now returns `fullyCleanedUp: false` for the unconfigured case, the run
  counts it as `SessionsAwaitingIntegrationConfig`, and `LogUnconfiguredCleanups` emits one aggregate
  line per run. **`ZoomMeetingId` is deliberately left set** so teardown retries automatically the
  moment credentials are added — the same optional-integration gate used everywhere else.
- **Standalone VEC Submission page removed — folded into the Sessions filter (2026-07-30).** No linked
  doc. The page listed *every* active session for a team rather than actionable ones (for HRCC, 40 rows
  of which 8 were actionable), so its header count never matched its own table. Replaced by a "Pending
  VEC submission" option in the Sessions Status filter — deliberately a different axis from the other
  four checkboxes, living in the same group because the group already ORs its members. **Its predicate
  must stay identical to `NavBadgeCountService.CountSessionsPendingVecSubmissionAsync`**, which backs
  the nav badge; there's a comment on both sides saying so. Removed `VecSubmission.cshtml(.cs)` and
  `VecSubmissionReportService`; **kept `VecSubmissionService`** (session Detail still calls
  `MarkSubmittedAsync`), and its 6 predicate tests were retargeted onto `NavBadgeCountService` rather
  than deleted.
- **Per-row action menu on the Sessions list (2026-07-30).** No linked doc. Direct follow-on to the VEC
  Submission removal above — a filtered list of 8 pending sessions otherwise meant 8 round-trips into
  Detail. Adds a kebab column (Mark submitted / Mark completed / Clear reschedule flag / Delete behind
  a confirmation modal). The `SessionRow.Can*` flags only decide what's worth rendering; every POST
  handler re-resolves the user and re-checks authorization server-side. The `asp-page-handler`
  query-string/antiforgery trap found building this is in CLAUDE.md's Known Constraints.
- **Nav regrouped by domain object + pending-count badges (2026-07-30).** No linked doc. A SystemAdmin
  saw 9 top-level items; regrouped to 6 (`Sessions | Applicants ▾ | VEs ▾ | VEC Submission | Unmatched
  Payments | Settings ▾`). **`Applicants ▾`/`VEs ▾` deliberately hold one page each today** — they're
  structural homes for planned pages, established now so the nav doesn't reshuffle under users later;
  don't "fix" them back to flat links. Badges are backed by new `NavBadgeCountService`
  (`Core/Navigation/`), whose `teamIds` parameter follows the `GetEffectiveTeamIds` convention (null =
  every team, empty = none) — inverting it would silently show a SystemAdmin an all-zero nav, so
  there's a dedicated test. Fixed alongside: a TeamLead saw an Unmatched Payments link that 403'd, the
  one nav link whose visibility didn't match its page's own roles.
- **Applicant Status: FCC Red Light / BQQ holds and fee status (2026-07-30, PR #53).** The actionable
  case is an application still Red Light *after* payment — every application sits Red Light while the
  $35 fee is unpaid, so that alone is not a signal. The data lives in `HS.dat` (History), not the
  `EN.dat`/`CO.dat`/`AD.dat` records first tried: `RDLOFF`/`RDLCOM` and `BQOFF`/`BQCOM` as OFF/COM
  toggles keyed by Unique System Identifier, walked in file order (Log Date is day-granularity only).
  Same file also yielded payment status via `FVPOFF`/`FVPCNF`/`FVPCOM`. **Historical as of 2026-07-31**
  — the bulk-file subsystem this parsed was replaced by the ULS lookup API; `FccHoldReason`/
  `FccPaymentStatus` remain but are no longer refreshed from `HS.dat`.
- **Session delete for TeamAdmin/SystemAdmin (2026-07-29).** No linked doc. `SessionActionService.DeleteAsync`
  removes Payments → Candidates → SessionVolunteerExaminers → Session in one transaction and FK-safe
  order, writes the `AuditLog` entry first, and **blocks** if any payment is still referenced by an
  unresolved `UnmatchedSquarePayment`. Gated on `AdminAccessScope.CanManageTeam`, not the regular
  `CanEdit`, because it's destructive and hard to reverse. Not established either way: whether
  re-ingestion recreates the session if it's still in ExamTools' feed.
- **Home page styling + in-app logout (2026-07-22).** No linked doc. `Pages/Index.cshtml` was still the
  scaffold-default Bootstrap page; now uses `_PublicLayout` and redirects an already-signed-in visitor
  to their role landing page instead of a dead end. Found while verifying that: `_AppLayout` — the
  layout every authenticated page uses — never included `_LoginPartial`, so the only working logout
  control in the entire app was on the vestigial `/Error` page. Added a `.user-menu` dropdown with a
  POST logout form.
- **`claude-review` workflow tool permissions (2026-07-22).** No linked doc. The review action errored
  out on any real-sized PR (46 permission denials on PR #6) because the sandboxed run had no explicit
  tool permissions — an infra gap, not findings. Fixed with `claude_args: --allowedTools` for
  `dotnet build`/`test`/`restore` and `git diff`/`log`/`show`. `build-and-test`, the real gate, was
  never affected.
- **Zoom meeting templates — closed as a dead end (2026-07-28/29).** No linked doc.
  `POST /meetings`'s `template_id` only accepts **Admin**-type templates; the diagnostic script
  (`scripts/check-zoom-meeting-templates.py`), re-run against real per-team credentials once the
  `meeting:read:list_templates` scopes were added, found exactly one template and it's personal-type.
  That met the item's own stated exit condition, so no template-picker UI was built. Revisit only if a
  future Zoom plan enables Admin templates.
- **Apple Sign-In — decided against (2026-07-22).** `docs/admin-auth.md`. Not worth the $99/year
  Developer account; username/password + Google + Microsoft is the final sign-in set.
- **Password policy reviewed (2026-07-28).** No linked doc. `RequiredLength` 10 → 12 per NIST 800-63B
  (length beats composition rules); `RequireDigit`/`RequireLowercase`/`RequireUppercase` left at
  Identity's `true` defaults as extra friction since these are admin/VE accounts, not public
  self-service. `RequireNonAlphanumeric` stays `false`.
- **Applicant Status page (2026-07-29).** `docs/exam-result-license-class.md`'s "Applicant Status
  page" section — new team-wide `Pages/SessionManager/ApplicantStatus.cshtml(.cs)`: a "Pending FCC
  grant" worklist (passed but not yet `Granted`, drops a candidate the instant they are) plus a
  "Recently issued" section (`Granted` in the last 7 days) so a Session Manager can confirm a
  specific person's license/upgrade actually came through. No new backing fields — built entirely
  on `InitialLicenseClass`/`NewLicenseClass` from the license-class tracking work below.
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
