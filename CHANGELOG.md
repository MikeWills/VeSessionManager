# Changelog

Full history of feature/phase pointer entries, newest first. This is the overflow for
`CLAUDE.md`'s Change Log: CLAUDE.md is read in full on every conversation turn, so it only keeps a
small rolling window of the most recent entries (currently capped around 10) plus anything not
already covered by CLAUDE.md's "Current State" phase list; an entry moves here once it ages out of
that window, or immediately if it's phase-numbered work already summarized in "Current State." Full
design rationale for any entry still lives in its linked `/docs/*.md` file, not here or in
CLAUDE.md — this file, like CLAUDE.md's Change Log, is pointers only.

- **Felony disclosure instructions are a button now, sent before the session (2026-08-11).** Issue
  #221. See `docs/email-reference.md`. `MarkCompletedAsync` sent this automatically to every candidate
  whose `Tested` flag that call flipped — no button, no confirmation. Two things wrong with that: the
  email tells someone their **felony disclosure requires extra FCC paperwork**, which is not a thing
  to send as a side effect of a bulk status flip, and keying it to "session completed" meant it could
  only ever arrive **after** the exam, when the candidate can no longer easily ask anyone about it.
  Now a per-candidate action, offered whenever a disclosure is declared — `Tested` is not consulted at
  all. **Two consequences of deleting an automatic send, both deliberate:** the disclosure check moved
  *into* the service (`NoFelonyDisclosure`), because the id arrives from a form now and one caller's
  filtering can no longer be trusted; and the candidate is **marked, not just counted** — the session
  row and candidate page both show "declared a disclosure, instructions not sent", since a count in a
  one-off status message is gone on the next click. `SessionCompletionResult` reports how many are
  still waiting rather than how many emails it sent, which is now always zero.


- **The 5-day reminder chased the wrong fee (2026-08-11).** Issues #219/#218. See
  `docs/payment-reminders.md`. Found by *sending one and reading it* — the first candidate-facing
  email this app produced end to end. It fired on an unpaid Square `Payment`, the team's **exam
  fee** — money collected before or at the session, so by the time the trigger could fire (FCC
  receives the application, plus five days) it had been in hand for over a week, and the email
  carried a Square link for a bill already settled. **What is actually outstanding then is FCC's own
  application fee, paid at CORES**, and the signal was already being collected and read by nothing
  but a display column: `UlsWatcherService` maps ULS `FVPOFF` to `FccPaymentStatus =
  PendingVerification` twice daily. Three consequences that each look like an omission otherwise:
  the tracking stamp **moved from `Payment` to `Candidate`** (a team that collects no fees has no
  Payment row, and its candidates still owe the FCC), the template carries **no payment link and no
  placeholder that could become one** — which disposes of #218 by construction rather than by patch
  — and the retest branch went with the payment it hung off. `PaymentReminder5Day` is **retired, not
  deleted**: seeding never removes rows, so `EmailTemplateTriggers.Retired` exists and the admin page
  labels it "No longer sent". **Still open: whether the 10-day pass means anything now** — it expires
  a Square payment, but if day 10 is FCC's dismissal deadline the meaningful event is a different one.
- **A login and a VE record are the same person (2026-08-11).** Issues #224/#226. See
  `docs/ve-self-service.md`. `User` and `VolunteerExaminer` had **no FK in either direction** — an
  absence, not a decision: the *authentication* separation is deliberate and documented, and the
  *identity* separation appears to have been assumed from it. `User.VolunteerExaminerId` links them
  (identity only — it grants nothing, and a call-sign match is offered as a suggestion for a human to
  confirm, never applied automatically, because the FCC reissues call signs). That unlocks
  `/Account/MyVeDetails`: self-service is entered by clicking a link **mailed to the address on
  file**, so it can only ever reach a VE who already has one, and **one VE of 176 does**. The loop
  only opens from inside the app. The email field is the one divergence and only by necessity —
  `VeEmailChangeService` confirms via the *old* address and so structurally cannot set a first one,
  while `SetOwnEmailWhenUnsetAsync` refuses the moment an address exists, so there stays exactly one
  way to change a credential field.

- **Page smoke tests: every Razor page, actually rendered (2026-08-10).** See `docs/page-smoke-tests.md`. Nothing in this repo rendered Razor — not the build, not the 928 Core tests, not the static-HTML layout harness — and two bugs reached a deployment the same day because of it: a `<form>` carrying both `action=` and `asp-page-handler` (which `FormTagHelper` throws on **at render time**, so the build was clean and the page 500'd for anyone who opened it), and an anchor where `asp-all-route-data` silently discarded `asp-route-id` so every link to a VE pointed at nobody. `WebApplicationFactory` now boots the app in-process against throwaway SQLite and requests **every page discovered from the app's own `EndpointDataSource`**, so a new page is covered the day it exists. **The fake auth scheme is half the value**: every interesting page is `[Authorize]`d, so before this the only way to see one was for a human to log in and click. Seeding happens *before* the host starts because `Program.cs` refuses to start when no account can sign in — the harness satisfies that guard rather than weakening it. And **an empty `href` is the signature of the whole bug class**: the first version of the link test only followed links that had one, and passed with the original bug reintroduced.

- **All fourteen VECs seeded with verified ExamTools codes (2026-08-10).** Issue #83. See
  `docs/vec-examtools-code.md`. The full code↔VEC mapping came from the `From VEC` filter on
  `hamstudy.org/sessions`, whose per-entry slug is the same code space ExamTools reports — fourteen
  entries, matching the FCC's accredited count, with `arrl`/`lagroup`/`sandarc` agreeing with codes
  already confirmed live. **Nine of the fourteen have a code that differs from the display name, so
  GLAARG was never the exception it looked like.** New `KnownVecs` + `VecDefaultsSeeder` run from
  Worker startup in every environment, filling gaps only: a VEC is "present" when
  `ExamToolsCode ?? Name` matches a known code, so hand-made rows keep their name, notes and
  youth-program flag. Two cases warn rather than act, both meaning a human must look — a name taken
  by a row resolving to a *different* code (inserting would trip `IX_Vecs_Name`), and an existing row
  whose match code isn't one of the fourteen (a closed code space, so that row ingests nothing). Note
  `DevDataSeeder`'s guard moved from `Vecs.AnyAsync()` to a `FeeConfiguration` check — the Vec table
  is never empty now, which is the same table-wide-guard trap `DevAuthSeeder` hit.

- **v0.3.0: key ring separated, authenticated by default, candidate email corrected (2026-08-10).**
  See `docs/credential-encryption.md`, `docs/admin-auth.md`, `docs/email-notifications.md`,
  `docs/deployment.md`. The Data Protection key ring moved off the database directory to
  `/var/lib/vesessionmanager-keys` — it had satisfied "outside the app path so `rsync --delete`
  can't touch it" while still meaning **one `tar` of `/var/lib/vesessionmanager/` carried the
  ciphertext and the key together**. New `DataProtectionKeyRingGuard` refuses to start rather than
  running with credentials it cannot read, because the converter's legacy-plaintext fallback makes
  that state *completely silent*. A `FallbackPolicy` makes pages authenticated by default; the
  fifteen public ones say so explicitly. **Candidate email gave the session time in UTC** while every
  screen showed ET — now `10:00 AM ET / 7:00 AM PT`, two zones so Central and Mountain can
  interpolate. Optional per-team BCC on candidate mail (never on token-bearing sends). All fourteen
  VECs seeded. 8.4 MB of vendored Bootstrap deleted. **Three of these were found by looking rather
  than by being reported**, and two were mis-described by the audit that raised them — see the
  audit-file pointer above.

- **ExamTools reconciliation: a nightly check that the feed and the database agree (2026-08-10).** See `docs/reconciliation.md`. Every other job trusts ingestion to have worked; nothing checked, which is how the historical import could drop the last day of every calendar month since it was written and only be caught because HRCC's own Discord bot reads the same API directly and disagreed about whether a VE was still active. Per team, daily: diff ExamTools' closed-session feed against ours over a trailing 120 days. Findings are a **standing table plus a nav badge**, not just a run summary — Job History rotates, renders green because the *job* succeeded, and a count inside a sentence cannot be acted on, which is the same shape as the `sent 0, failed 1` incident. Each row carries the import range that would fix it; the job itself is **read-only**. The tests cover the bookkeeping and cannot cover the premise: the bug that prompted it had a full green suite because the fakes shared our own wrong assumption.

- **Job Schedule page: when every background job runs next (2026-08-06).** See
  `docs/job-schedule.md`. "When does the next run happen?" was answerable only by reading the Worker's
  source — Job History records what happened, never what will. New `JobSchedules` registry in Core is
  the **one definition of every job's cadence, read by both hosts**: the Worker to schedule, Web to
  report, so the page cannot drift the way `TeamPipeline`'s order once did. `Jobs:*` config moved to
  `appsettings.Shared.json` for the same reason (Web resolves those keys now). Two cadence shapes are
  reported differently on purpose — **anchored** jobs (ULS and LicenseWatch both 08:00/20:00 ET —
  they share one schedule, and the page reads the descriptor, never a constant) state
  a real time and show `Due now` when a slot is unrun, **interval** jobs are last-run-plus-interval and
  labelled *estimated*, because their timer restarts with the Worker. Tests caught two bugs first:
  `Max` over an empty filtered sequence **throws** rather than returning null (one perpetually-failing
  job would have taken down the whole page — the nullable cast must be *inside* `Max`), and advancing
  an anchored slot by adding hours to a UTC value is an hour off across DST.

- **VE management, license tracking, self-service and invitations (2026-08-07).** Issues #142 and
  #107, built together because neither could answer its own question alone. See
  `docs/ve-management.md` (the person model), `docs/ve-license-tracking.md`,
  `docs/ve-import-export.md`, `docs/ve-self-service.md` and `docs/ve-session-invitations.md`.
  **`VolunteerExaminer` is now a person, not a per-team row** — `TeamId` gone, `VeTeamMembership`
  added, identity on `Id` then `Frn` and never the call sign, since a call sign changes and the
  person does not. #107's ULS sweep is what backfills that FRN, which is why the two shipped
  together; it also answers "can this VE legally serve on Saturday?" on Session Detail's chips, which
  needed #142's accreditations to be more than half an answer. **Three things real data caught that
  the tests could not:** ExamTools' literal `<UNKNOWN>` fused two different people (hence
  `Core/CallSign.IsUsable`, now the one definition of "is this a call sign"), an FRN collision aborted
  the whole sweep for want of a per-row guard, and an admin could not set a VE's email at all — so
  nobody could ever start self-service. Self-service is the app's **first unauthenticated endpoint
  reaching personal data**: separate cookie scheme, three independent barriers from the admin app, and
  `/VeSelfService` added to the global rate limiter.

- **Square's Sandbox/Production environment moved onto `Team` (2026-08-06).** See
  `docs/square-payments.md`. It was the last Square value still in `appsettings.json`, left there on
  the reasoning that sandbox-vs-production is an environment choice rather than a per-team one — which
  is wrong, because a Square access token is *issued for* one environment and only authenticates
  against that host, so it belongs with the credentials it travels with. One global switch made
  "real team on Production, test team (WX0MIK) on Sandbox" impossible on a single deployment, and on
  beta it forced *every* team to Production. New `Team.SquareEnvironment`; `SquareOptions` deleted
  outright, so nothing Square-related remains in config. `SquareClient` reads it off the credentials
  record — new `Team.ToSquareCredentials()` replacing five hand-built copies. **Post-deploy step:
  the `TeamSquareEnvironment` migration puts every existing team on Sandbox** (the old value was
  config, so there was nothing to migrate from) — **set live teams back to Production in Team
  Settings.** Until then their links fail to generate and show as failures in Job History; that
  direction is deliberate, since defaulting to Production would make a misconfiguration invisible
  *and* billable.

- **Job Run History records what each run actually did (2026-08-05).** See `docs/job-run-history.md`.
  `Success`/`ErrorMessage` alone made three outcomes identical on the ops dashboard: sent five, sent
  none because nothing qualified, and **sent none because every attempt failed** — the last of which
  rendered green, because a job is Success when the *job* completes and per-item failures are caught
  inside it on purpose. Cost an evening chasing "no emails are being sent" when the Worker log had
  been printing `sent 0, failed 1` all day (the real cause: `smtp.mailgun.com` instead of `.org`,
  failing the TLS handshake on a certificate name mismatch). `JobRunHistory.ResultSummary` now stores
  the result object's own `ToString()` — text that already existed — via a generic
  `JobRunHistoryLogger.RunAsync<TResult>` overload, so result-returning jobs get it with no call-site
  change. **The overload resolution is load-bearing and tested**: call sites pass method groups, which
  convert to *both* overloads, and binding to the void one would leave every summary silently null.


- **Renewal Monitor: expiration + renewal tracking for an arbitrary watch list (2026-08-05).** See
  `docs/renewal-monitor.md`. Team-scoped list of any call sign at all — club members, family, people
  who never tested here — showing expiration and the renewal lifecycle. Screen only, no email, open
  to every role (it is all public FCC record data). **`expired_date` was returned by ExamTools' ULS
  mirror all along and simply never mapped**; call-sign lookup works on the same endpoint as FRN,
  which is what makes call-sign-first entry possible. **Renewal issuance has no positive signal
  except the expiration date moving** — a renewal leaves call sign, class and grant date untouched —
  so the service stores the expiry as it stood when the renewal was first seen and only claims it
  landed once the current value passes that anchor. FCC's own `data.fcc.gov` License View API is
  Akamai-403 from this deployment, same as `wireless2`. Tracking **VEs'** licenses is a deliberately
  separate, not-yet-built feature.


- **Team logo in emails via `{{Logo}}` (2026-08-05).** See `docs/email-logo.md`. Per-team PNG/JPEG
  uploaded on Team Settings, stored as a **DB column** (an uploads folder under `wwwroot` would be
  wiped by `deploy.yml`'s `rsync --delete`) and embedded as a **CID linked resource**, not a hosted
  URL — Gmail and Outlook block remote images by default, so a hosted logo is invisible until the
  recipient clicks "show images". **`{{Logo}}` is the one body placeholder that is NOT HTML-encoded**,
  and that exemption is safe only because the value is built in the renderer from a constant out of
  app-owned data — nothing registrant-controlled may ever join that branch. Format is decided from
  the file's magic numbers, never the browser-declared content type; SVG is excluded outright.
  A template carrying the placeholder stays valid for a team with no logo (renders to nothing), and
  a template without it attaches nothing.

- **WYSIWYG editor for email templates (2026-08-05).** See `docs/email-template-editor.md`. Quill
  2.0.3 vendored under `wwwroot/lib/quill`, loaded only by Admin → Email Templates via a new `Head`
  section on `_AppLayout`. **The `<textarea name="body">` is still the field that posts** — the editor
  is a second view onto it and the HTML tab stays authoritative, so the server contract is unchanged
  and the page degrades to its old plain-textarea self without JS. Three traps, all measured rather
  than assumed: `root.innerHTML` renders bullets as `<ol data-list="bullet">` (**an email client shows
  them numbered**), `getSemanticHTML()` turns every space into `&nbsp;` (**which stops lines
  wrapping on a phone**), and Quill's default alignment emits a *class*, which does nothing in an
  inbox — so alignment is registered as an inline-style attributor. Placeholder chips are now
  click-to-insert. Toolbar deliberately stops at headings/alignment: colour and font-size are where
  users produce mail that renders differently in every client.

- **Mobile-first responsive pass over the whole site (2026-08-05).** See `docs/responsive-ui.md`.
  `app.css` had **zero media queries** — the site was desktop-only by construction, not merely
  unpolished. It is now mobile-first (base layer = phone; a `min-width: 768px` "Desktop layer"
  restores the original design unchanged), the chassis nav collapses behind a `☰` toggle, and tables
  carry **both** treatments: `class="cards"` restacks each row into a labelled card below 768px
  (labels generated by `app.js` from each `<th>`, so putting a table on cards is one word), and a
  `.table-scroll` wrapper catches the **768–1100px band** where cards are off but a wide table still
  overflows — a tablet or split-screen window. The wrapper's `min-width` floor must stay scoped
  `:not(.cards)` or it shoves a card list off the side of a phone. Focusable controls are 16px on mobile because **iOS Safari
  zooms on focus below that and never zooms back**, which is also why several inline
  `style="…font-size:12px…"` attributes became `.menu-input` — a media query cannot override an
  inline style.

- **Worker resilience + duplicate-payment index (2026-08-03).** See `docs/worker-resilience.md`.
  Every job's per-tick work outside `JobRunHistoryLogger` (settings/team loads, queue peeks,
  `LastIngestionRunUtc` stamps) was unguarded, so one transient "database is locked" stopped the
  **whole Worker** — the StopHost trap the constructor rule never covered. New
  `JobTick.GuardedAsync` wraps each tick; `JobRunHistoryLogger` now protects both its saves and
  clears the change tracker on the failure path (a poisoned entity was retried by its own `finally`).
  Separately, a filtered unique index on `Payments (CandidateId, Reason)` closes the Web-vs-Worker
  double-payment race; creation saves per candidate so one collision can't roll back the pass, and
  the migration deletes only provably-inert duplicates, failing loudly on any that were linked/paid.

- **Audit P0/P1 batch: PII purge gap, youth idempotency key, wedged imports, STARTTLS (2026-08-03).**
  See `docs/pii-purge.md`, `docs/youth-payment-confirmation.md`, `docs/historical-import.md`.
  `CandidatePiiFields.Clear` never nulled `FirstName` (added Phase 4, never added to the helper), so
  every purged candidate kept their given name — now cleared, guarded by a **reflection test** that
  fails when a new `Candidate` field isn't explicitly classified, plus a self-healing repair pass for
  rows already purged under the old definition. `YouthPaymentConfirmationService` minted a fresh
  Square idempotency key per attempt despite a comment claiming persist-once (duplicate live order on
  crash-retry) — key now cleared with the standard link it belongs to, then `??=`. Historical import
  left `Running` by a Worker restart wedged that team's queue forever — now reclaimed after
  `StaleRunningThreshold` and **resumed at the interrupted chunk** via `Skip(ChunksCompleted)`. Team
  Settings' STARTTLS checkbox gained its hidden `false` sibling (it could never be turned off).

- **Public-internet hardening pass (2026-08-03).** See `docs/security-hardening-2026-08-03.md`
  (Tier 1 of `docs/audit-2026-08-03-tasks.md`). Rate limiting on `/Account/*` (20/min per IP, global
  limiter + no-limiter partition elsewhere) — which **required adding `UseForwardedHeaders`**, or
  behind the Apache proxy the whole internet shares one bucket; security response headers incl. a
  CSP whose `style-src`/`font-src` allowances are load-bearing (Google Fonts + ~139 inline
  `style=""` attributes — tightening them without removing those breaks the site's typography);
  password-reset links now built from `App:PublicBaseUrl` instead of the request Host (which was an
  admin-account-takeover vector) plus `AllowedHosts` pinned — **a deployment under any other
  hostname now 400s until both are updated**; the youth-rate attestation enforced server-side
  (`[Required]` on a non-nullable bool is client-side only — it always passes on the server); Square
  webhook body capped at 64KB.

- **Session Detail's "Refresh candidates" narrowed from team-wide to session-scoped (2026-08-03).**
  See `docs/team-maintenance.md`'s "session-scoped" section. The button ran the full team pipeline —
  one click could mint payment links and send emails for every *other* session the team had. New
  `ManualCandidateRefreshService.RunForSessionAsync`: `SessionIngestionService.RefreshSessionCandidatesAsync`
  syncs one session's applicants (no session create/cancel — those need the full-feed diff),
  `ExamResultSyncService.SyncSessionAsync` ignores `ResultSyncWindow` (making the window's documented
  escape hatch real for the first time), and the other four scan services gained a trailing optional
  `int? onlySessionId` filter. Team Maintenance's "Refresh now" stays team-wide and throttled.
- **Payment work bounded by session age; post-import log noise fixed (2026-08-01).** See
  `docs/historical-import.md`'s companion fixes 3 and 4. `PaymentGenerationService` filtered only on
  `Session.Status == Active` (= "not cancelled", never "not finished"), so the historical import's
  year of backfilled candidates produced **~1710 Unpaid payments** — inert only because that team had
  no Square credentials, and one config change away from minting ~1710 live payment links and then
  emailing those people. New `PaymentEligibilityWindow` (30 days on `ScheduledStartUtc`) bounds
  creation, **link generation**, reminders and expiration. **A window, not `HasEnded`:** reminders key
  off `ApplicationDateEnteredUtc`, which FCC sets *after* the session, so they legitimately target
  ended sessions — a `HasEnded` guard would break the feature. Bounding *link generation* is what
  makes leaving the existing 1710 rows in place safe. Separately, scheduling/notification queries
  gained a `>= now - 1 day` bound: a past session can never satisfy
  `ScheduledStartUtc == ZoomDiscordSyncedStartUtc`, so 794 sessions + 1991 candidates were being
  loaded, filtered and log-counted every tick forever. Third case, same shape:
  `VolunteerExaminerSyncService` settled a finished session only once it *had* a roster, so a 2023
  session whose roster ExamTools 500s on retried hourly forever — new `RosterRetryWindow` (30 days)
  settles it regardless.
- **Self-service password reset + deployment-wide "system" email sender (2026-08-01).** See
  `docs/password-reset.md`. There was **no password reset of any kind** — a local-account user who
  forgot their password was locked out permanently, hand-editing `AspNetUsers` the only recovery
  (OAuth users unaffected). New `PasswordResetService` + `/Account/ForgotPassword`/`ResetPassword`.
  **Mail sends from new `SystemSettings.SystemSmtp*` fields, not a Team's** — a reset is addressed to
  an app *user*, and a SystemAdmin may belong to no team; per-team SMTP still owns all candidate
  mail. `IsSystemEmailConfigured` requires host **and** username (the `SmtpHost`-default gotcha
  below). **Non-disclosure is the design constraint:** every request reports `Accepted` — unknown
  address, deactivated (= locked out; there is no `IsActive` flag), OAuth-only (no password hash, or
  mailbox access could downgrade an SSO login to a password login), and even an SMTP throw — so the
  page is never an account-enumeration oracle; only `SystemEmailNotConfigured` is surfaced. Throttle
  stamped **before** the send so a failing SMTP server can't be driven as a mail-bombing loop.
  Migration `PasswordResetAndSystemEmail` is nullable adds only. **Never live-verified — no SMTP has
  ever been configured on any deployment.**
- **VE Roster restricted to admin roles (2026-08-01).** See `docs/admin-auth.md`. SessionManager and
  TeamLead dropped from `VeRoster.cshtml.cs`'s `[Authorize]` — the page is a full VE contact roster
  *and* a per-VE session-count leaderboard, and a visible count-per-person invites comparison between
  volunteers. Session Detail's per-session VE chips are deliberately untouched (operational context
  for the session being run, not a roster). **The `[Authorize]` attribute and the `_AppLayout.cshtml`
  nav gate must change together** — the attribute enforces, the nav gate only avoids a link that
  403s; same rule Unmatched Payments already follows.
- **VEC matching moves from `Vec.Name` to `Vec.ExamToolsCode` (2026-08-01).** See
  `docs/vec-examtools-code.md`. Ingestion matched ExamTools' per-session `vec` code against the VEC's
  *name*, which worked only because ARRL reports `"arrl"` — GLAARG reports **`lagroup`**, so a
  correctly-named "GLAARG" row would have skipped every one of its sessions forever, with nothing but
  one `[WRN]` line per poll to show for it (found by reading the live Worker log, not by any alert).
  New nullable `Vec.ExamToolsCode`; null means "same as the name," so existing rows are untouched and
  the common case stays blank. Ingestion matches `(v.ExamToolsCode ?? v.Name).ToLower()` — spelled
  out in the query, not via the new `Vec.MatchCode` helper, so EF can translate it. Duplicate
  detection is against that same coalesce (a code colliding with another VEC's *name* is rejected
  too, `VecActionResult.DuplicateExamToolsCode`). Migration `VecExamToolsCode` is one nullable column
  + `IX_Vecs_ExamToolsCode`, clean down-path. Codes confirmed live: `arrl`, `lagroup`, `sandarc` —
  read from `GET /api/teams/team`'s `teamDoc.vecs` (the only place they're exposed; it lists the
  calling VE's own teams, so it is not a global VEC directory).
- **One-time historical session import + VE-roster re-poll fix (2026-07-31).** See
  `docs/historical-import.md` (issue #67 part 2). Admin picks a date range on Team Maintenance; a
  `HistoricalImportRequest` row is queued and the Worker's new `HistoricalImportJob` walks it **one
  calendar month per ExamTools call** with a 2s pause, saving progress counters after every chunk.
  Queued-for-the-Worker rather than run inline because Web and Worker are separate processes — no
  spinner, no half-import lost to an app recycle, no two processes polling ExamTools at once. Scope
  is **sessions + candidates + VE roster only** — no payment links, no Zoom/Discord, no emails; the
  `HasEnded` guards stay a backstop rather than the sole defence for a year of backdated data.
  **`SessionIngestionService.ImportHistoricalRangeAsync` must never be collapsed into `RunAsync`:**
  RunAsync cancels sessions absent from the feed, and a date-ranged feed excludes a team's entire
  live schedule by construction. It also skips reschedule/`ExtId` handling and only syncs candidates
  for sessions it creates, keeping `WithdrawMissingCandidates` away from historical rosters where a
  short export would irreversibly clear PII. **Companion fix, needed before any of this was safe:**
  `VolunteerExaminerSyncService` re-polled *every* `Status == Active` session every tick forever, so
  importing a year would have added ~100 permanent hourly API calls. It now skips a session that is
  done **and** already has a roster — done meaning `ExamToolsClosedUtc` (authoritative, and can
  precede the scheduled end) or `TestingCompletedUtc` or `HasEnded` (the backstop for pre-2026-07-31
  sessions carrying neither stamp). The roster half is not redundant: a session appearing and closing
  inside one polling interval would otherwise lose its roster permanently; empty rosters keep
  retrying so a failed sync self-heals. **Same bug class then fixed in `ExamResultSyncService`
  (issue #81):** bounded to `ResultSyncWindow` (14 days), anchored on `ScheduledStartUtc` and **not**
  `ExamToolsClosedUtc` — the import stamps the close field at *import* time, so anchoring there would
  preserve the very burst the bound exists to stop. `ManualCandidateRefreshService` gained the
  exam-result step it had always been missing (its own doc comment claimed otherwise) as the
  on-demand escape hatch for a session graded later than the window; that also required registering
  `ExamResultSyncService` in the **Web** project's DI for the first time. Migration
  `HistoricalImportRequests` is one new table, clean `DROP TABLE` down-path. **Follow-up
  (2026-08-01):** imported sessions are now marked **Submitted to the VEC** — they defaulted to
  `NotSubmitted`, which dumped six months of backdated sessions into the submission tracker as
  outstanding work, one manual Detail-page click each. The marking sits **outside** the create branch
  on purpose (an import skips sessions it already has, so re-running a range is the supported way to
  clear a pre-existing backlog); an already-Submitted session keeps its original date/user; and
  `RunAsync` never does this — only the historical path may assume paperwork was filed. Note the
  assumption: importing a range that overlaps genuinely unsubmitted sessions marks them submitted too.
- **Admin → Team Maintenance: team-level "Refresh now" + ingestion schedule + Worker-health banner
  (2026-07-31).** See `docs/team-maintenance.md` (issues #77/#73). New TeamAdmin/SystemAdmin page,
  operations to Team Settings' configuration. Closes the gap where `ManualCandidateRefreshService`'s
  **only** trigger was a session Detail page — so a team with no ingested sessions had no way to
  trigger ingestion at all, and the live workaround was `Team.LastIngestionRunUtc = NULL` by hand.
  Refresh now reuses the same service unchanged, debounced 60s per team by `TeamRefreshThrottle`
  (schema-free — it reads the `ManualSessionIngestion` JobRunHistory rows; the per-session button
  stays unthrottled), and deliberately does **not** write `LastIngestionRunUtc`, so a manual run
  never delays the scheduled poll. `IngestionStatusService` derives last/next poll from
  `IngestionScheduleService.IsDue` rather than restating the arithmetic. **Two traps worth knowing:**
  it reads `SystemSettings` directly because `SystemSettingsService.GetAsync` get-or-creates (a
  *write*, and this runs on every render — same rule `_TestModeBanner` follows), and the site-wide
  health banner's `IngestionHealthCache` is a **singleton**, so it resolves the scoped
  `IngestionStatusService` through a fresh scope instead of injecting it. Health is four states, not
  a bool (a fresh install must not open on a red alarm), is deployment-wide regardless of which team
  is being viewed, and fires at **2×** the configured interval — 1× fires during normal operation.
- **Closed-session sweep narrowed to a discovery net (2026-07-31).** See `docs/historical-import.md`
  (issue #67, part 1). `CompletedSessionBackfillWindow` 30 days → 7, and a closed session that is
  already stored locally **and** already carries an `ExamToolsClosedUtc` stamp is dropped from the
  merged feed instead of being re-processed every tick, for every team, forever (its only remaining
  effects were a meaningless `ApplyRescheduleRules` and the long-complete `ExtId` backfill). **The
  stamp half of that test is load-bearing:** skipping on "already known locally" alone would starve a
  not-yet-closed session of both its `ExamToolsClosedUtc` stamp and its final candidate sync, which
  brings issue #68's false cancellations straight back — `KnownButNotYetClosedSession_IsStillReadFromTheClosedFeed`
  is the regression test. Pulling real history is now a deliberate one-off (see the historical import)
  rather than a side effect of the rolling window.
- **Click-to-sort columns on every table (2026-07-31).** See `docs/table-sorting.md`. Ascending →
  descending → back to the server's order; a second column replaces the first; the choice is
  remembered per page. Two mechanisms behind one appearance: a shared vanilla-JS sorter in `app.js`
  for every table that renders its full set (opt in with `data-sortable="key"`, remembered in
  `localStorage`), and real `sort`/`dir` query parameters on the **Sessions list only**, because it
  pages server-side and reordering just the rows on screen would look like — but not be — a sort of
  the whole result set (remembered in the existing `vsm_session_filters` cookie, now 6 fields).
  **Rule for any new table: pages server-side ⇒ sort server-side; otherwise `data-sortable`.** Cells
  sort on `data-sort-value` when present — mandatory for dates (`"MMM d, yyyy"` sorts Apr before Mar),
  which is why several row records grew a `...SortValue` member beside their formatted `...Line`.
- **ULS watcher replaces the FCC bulk-file parser (2026-07-31).** See `docs/uls-watcher.md`;
  `docs/fcc-uls-watcher.md` is retained as history because the matching rules survived the rewrite
  and their incident rationale lives there. `FccUlsClient`/`FccUlsRecordParser`/`FccUlsSchedule`/
  `FccDailyWatcherJob`/`FccWeeklyCatchupJob` and both test files are **deleted**, replaced by one
  unauthenticated call per non-terminal candidate: `GET exam.tools/api/uls/lookup2/{frn}`
  (`ExamToolsUlsLookupClient` + `UlsWatcherService` + `UlsWatcherJob`). Motivation: FCC's files are
  structurally ~26-30h stale (issuance 02:00 ET, file publishes next morning), so the app routinely
  disagreed with what ExamTools showed a Session Manager on the next screen — and the accuracy
  trade-off was accepted deliberately since this tracking is informational, not operational ("that's
  the VEC's job"). **All grant rules carried over unchanged** — Active-only, new-license grant date
  on/after session, and the two-part upgrade test — now using `effective_date` (ExamTools' rendering
  of HD Last Action Date) in place of AM.dat + Last Action Date. Verified live in both directions the
  same day: two candidates were correctly withheld at 10:00 (class still Technician) and correctly
  granted at 11:30 once the class moved. Still twice a day (08:00/20:00 ET); the weekly catch-up job
  is gone entirely (a lookup returns current state, so there is no one-shot window to miss) and the
  three `--run-fcc-*` switches collapse to `--run-uls`. Migration `UlsWatcherReplacesFccFiles` is
  **hand-written** — EF's scaffolder paired the columns by position and would have set start-hour 24
  and a 1-hour interval. New `Candidate.UlsApplicationFileNumber`; Applicant Status gains the ULS
  license link and the application file number (no application deep link — `wireless2.fcc.gov` 403s,
  so the shape is still unverified).
- **Team selectors unified + `User.CallSign` (2026-07-30).** No linked doc — see TODO.md. The last
  four pages on the old pills/`<select>` pickers (VE Roster, Admin Users/Team Settings/Email
  Templates) now use the session list's dropdown. "All teams" is present where a merged view means
  something and omitted where it doesn't — the two admin config pages edit one team's settings, so
  their trigger reads "Select a team…" instead. `VolunteerExaminerReportService.GetSessionCountsAsync`
  widened from a single teamId to the same `IReadOnlyList<int>?` set convention (null = every team);
  a VE is team-scoped, so a merged run yields one row per VE-per-team, never a silent cross-team
  merge. Separately: new nullable `User.CallSign` (migration `UserCallSign`), stored upper-invariant
  like `VolunteerExaminer.CallSign`, editable on Admin → Users. Deliberately not an FK to
  VolunteerExaminer — see the property's own comment.
- **Applicant Status / Unmatched Payments: days-pending anchor, 5/10-day colouring, Sessions-style
  team filter (2026-07-30).** No linked doc — see TODO.md. Days pending now counts only from
  `ApplicationDateEnteredUtc` (VEC processing time isn't FCC's clock; shows an em dash until FCC has
  the application). Day 5/day 10 colouring reads `PaymentReminderService`'s now-public
  `ReminderThresholdDays`/`ExpirationThresholdDays` rather than restating them, and only escalates
  while an Unpaid payment exists — the condition both of those passes actually require. Team filter
  switched to the session list's dropdown incl. "All teams", backed by new
  `SessionAccessScope.ResolveViewableTeamIds` (null = every team) with `Scope()` reimplemented on top.
  Fixed on the way: a SystemAdmin could never match an unmatched payment, and cross-team matching
  became possible once several teams shared a screen.
- **FCC weekly snapshot is days stale — catch-up must sweep the daily files (2026-07-30).** See
  `docs/fcc-uls-watcher.md`'s "The weekly snapshot is not a rolling backstop". The AM.dat fix below
  recovered 11 candidates and left 10 that I wrongly reported as legitimately pending (2 hand-checked,
  the rest assumed). FCC's weekly `complete` zip stamps its own creation date and arrived 4-5 days
  stale, while `RunDailyAsync` reads only yesterday+today — Monday's/Tuesday's files were read by
  neither. New `RunAllDailyFilesAsync` sweeps Mon-Sat, `FccWeeklyCatchupJob` now runs it alongside the
  snapshot, plus a `--run-fcc-all-dailies` switch. Recovered 4 more; remaining 6 verified individually.
- **FCC upgrade detection via AM.dat + Last Action Date, and on-demand watcher runs (2026-07-30).**
  See `docs/fcc-uls-watcher.md`'s "Confirming a class upgrade" section. The Grant-Date guard added
  earlier the same day made class upgrades *permanently* undetectable (Grant Date never advances on
  an upgrade), leaving 20 real candidates stuck — oldest 19 days. Fixed by reading `AM.dat` (operator
  class, present in every archive, never opened before) and pairing it with `HD`'s Last Action Date,
  which *does* advance: an upgrade grants only when both the class matches `NewLicenseClass` and the
  last action is on/after the session. 11 candidates recovered in one weekly pass. Also adds
  `--run-fcc-daily`/`--run-fcc-weekly` Worker switches (same shape as `--migrate-team-secrets`),
  replacing the previous "temporarily rewrite `FccDailyWatcherStartHourEt`, restart, put it back"
  dance for forcing a run.
- **Session list "Last 7 + Upcoming" filter + past-row shading + quieter EF logging (2026-07-30).**
  No linked doc. `IndexModel` gets a second forward-looking date-range preset alongside the existing
  `Upcoming` one — `ScheduledStartUtc` from 7 days ago through the unbounded future in one filter,
  same ascending "soonest first" sort as `Upcoming` — and it replaces `Upcoming` as the fallback
  default for a fresh visit with no filter cookie yet (a returning visitor's own remembered choice
  is unaffected). Independent of any date filter, every row also gets a `row-past` CSS class once
  `Session.HasEnded(now)` — a light background tint (reusing the existing `--paper` theme token, so
  it's already correct in both light/dark mode) makes it obvious at a glance which sessions in a
  mixed list already happened. Unrelated, bundled in the same pass: both `Worker` and `Web`
  `appsettings.json` now override `Microsoft.EntityFrameworkCore.Database.Command` to `Warning` —
  full per-query SQL text at `Information` was dominating both projects' logs (one file alone hit
  2.5MB/day) and burying the actual "Starting job"/"Finished job" business-logic lines underneath;
  `Web` already had the equivalent `Microsoft.AspNetCore` override, `Worker` never did.
- **Session.ExtId + breadcrumb rework (2026-07-30).** No linked doc. `Session.ExamToolsSessionId`
  (a raw Mongo id) turned out to be meaningless to a user for "which session is this" purposes —
  new `Session.ExtId` maps `sessionDef.extId` instead, ExamTools' own short lead-VE-callsign code
  (e.g. `"KM6Z - W5CBW"`, `"AD2GX"`), verified byte-for-byte against real HRCC sessions to be the
  exact parenthetical text ExamTools' own calendar UI shows next to the team name. Already present
  on the cheap team-list endpoint (`GetTeamSessionsAsync`) — no extra per-session API call needed.
  Replaces the session list's "Session ID" column and, combined with `Session.Title` via new
  `SessionBreadcrumbFormatter`, the Detail/CandidateDetail breadcrumbs, page title, and delete-modal
  heading. Existing sessions backfill lazily (same idiom as the license-class backfill) — no
  one-off migration script — `SessionIngestionService` fills in a null `ExtId` the next time that
  session is still in the feed, and never overwrites once set.
- **FCC ULS watcher reliability: weekly-catchup retry, upgrade-exam false-positive guard, FRN
  column (2026-07-30).** No linked doc — see `docs/fcc-uls-watcher.md` for the underlying job
  design this builds on. Found live investigating a real HRCC discrepancy (Applicant Status showed
  48 pending vs. an expected ~2): (1) `FccWeeklyCatchupJob` only ever attempted its once-a-week
  scan with **no retry on failure** — a single `403 Forbidden` from `data.fcc.gov` (confirmed
  transient; the identical request succeeds on retry) left the entire safety net dark for a full
  week. Fixed with the same "has this week's slot already succeeded?" catch-up idiom
  `FccDailyWatcherJob` already uses, retried every `intervalHours` tick until success. Manually
  triggering it live recovered HRCC's backlog from 40 Unmatched/8 Received down to 3
  Unmatched/50 Granted. (2) Separately, and more seriously: `FccUlsWatcherService.ProcessLicensesAsync`
  had no guard against a candidate's FRN already having an *old, unrelated* Active license record —
  exactly the "upgrade exam" case the class's own doc comment had flagged as deferred. Three real
  same-day upgrade candidates (testing General→Extra, Technician→General) were incorrectly marked
  `Granted` off license grants from weeks-to-years earlier, before FCC had done anything with
  today's actual exam. Fixed by gating the match on the license record's Grant Date being on/after
  `Session.ScheduledStartUtc`, same rule already used for application-file matches; new
  `FccUlsWatcherService.RunForDayAsync(DayOfWeek, ...)` lets a specific missed daily file be
  reprocessed on demand (used to recover this incident's data without waiting on a fresh weekly
  snapshot). The limitation this guard traded off — upgrades becoming permanently undetectable —
  **was fixed later the same day, see the AM.dat entry at the top of this Change Log.** (3) Also added an FRN column to Applicant
  Status's "Pending FCC grant" table for manual copy-paste into FCC's ULS search while (1)/(2) were
  being investigated.
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
  for paste-in reference. The *license* link (`UlsSearch/license.jsp?licKey=`) is unaffected and
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
