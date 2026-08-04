# CLAUDE.md

This is a Visual Studio project that is designed to automate many of the mundane tasks that a Amateur Radio Volunteer Examiner (VE) Session Manager (SM) needs to do to run a session include creating a Zoom session, sending payment links and reminder emails. See docs/spec.md for details.

## Current State

- **All phases of `docs/spec.md` are implemented, Phase 0 through Phase 10** (Phase 0 foundation, Phase 1 ExamTools session/candidate ingestion, Phase 2 Zoom + Discord event scheduling, Phase 3 Square payment links + webhook, Phase 4 candidate notification emails + templates, Phase 5 ULS application/license watcher (rewritten 2026-07-31 onto ExamTools' ULS API — see `docs/uls-watcher.md`), Phase 6 payment reminder & expiration job, Phase 7 VE tracking, Phase 8 VEC submission tracker, Phase 9a-9d admin backend auth/scaffolding/candidate actions/config screens/privacy page, Phase 10 PII purge job) — see spec.md's own Backlog section for unscoped future work (VEC discount programs, no-FRN batch export) and TODO.md for known gaps.
- Build/test/run: `dotnet build`, `dotnet test`, `dotnet run --project src/VeSessionManager.Worker`, `dotnet run --project src/VeSessionManager.Web` (see README, and Known Constraints below, for the `DOTNET_ENVIRONMENT` gotcha). Tests are xUnit in `tests/VeSessionManager.Core.Tests`, using the EF InMemory provider and fake client implementations — follow `SessionIngestionServiceTests`/`SessionEventSchedulingServiceTests`/`PaymentGenerationServiceTests`/`CandidateNotificationServiceTests`/`UlsWatcherServiceTests`/`PaymentReminderServiceTests` as the pattern.

## Established Patterns

Cross-cutting conventions that apply to **all future work** in this codebase, not tied to one
phase — follow these by default instead of re-deriving them. (Contrast with Known Constraints
below, which is "this will silently break if you don't know X," and the Change Log/`CHANGELOG.md`,
which is "here's what was built and why, mostly historical.")

- **Optional-integration pattern** (established across Phases 2-4, follow for every future external
  API client): ExamTools is the one hard requirement (fails loudly — ingestion is what everything
  else depends on); Zoom, Discord, Square, and Email/SMTP are all optional. Each client exposes
  `bool IsConfigured` on its interface; the consuming service checks it *before* attempting the
  call, skips quietly with one aggregate `INFO` log line (never a repeating `ERROR`) when
  unconfigured, and leaves whatever `...SentUtc`/`...Id`/`PaymentLinkUrl`-style tracking field null
  so the very next poll retries automatically — no separate "backfill" step needed once credentials
  are added. Never validate credentials in a client's constructor (see the BackgroundService gotcha
  in Known Constraints) — always in the method that needs them, or a lazily-evaluated `IsConfigured`
  getter. A client's `IsConfigured` must reflect "an admin actually did something," not just "a
  shipped appsettings default happens to be non-empty" (see the SmtpUsername gotcha below).
- **Domain hierarchy: VEC ⇒ Team ⇒ VE, not the reverse.** `Team` (the group of VEs operating a
  deployment, holding all integration credentials) and `Vec` (the FCC-recognized coordinating org, a
  shared/global reference table — one real-world "ARRL" row, not one per team) are siblings, not
  parent/child — `Vec` is never owned by `Team`. `Session` has independent `TeamId`/`VecId` FKs.
  Full rationale in `docs/multi-team.md`.
- **Scan-based, idempotent jobs, not event-driven.** Every background job in this app (ingestion,
  scheduling, payment generation, notifications, reminders, the PII purge) works the same way: diff
  stored state against a remote feed or a date threshold on each tick, and use a
  `...SentUtc`/`...SyncedUtc`/status-flag field as both the "needs action" query filter and the
  idempotency guard, saved immediately after each individual item so a crash mid-run never
  double-processes or loses progress already made. New jobs should follow this shape rather than
  reacting to a one-shot signal.
- **External-resource-creation calls must be retry-safe against a crash between the API call
  succeeding and local persistence:** either query-before-create (list existing resources, match by
  name/time before creating — see Discord/Zoom in `docs/zoom-discord-scheduling.md`) or persist an
  idempotency key *before* calling, then reuse it on every retry (see Square in
  `docs/square-payments.md`). A pre-existing `IdempotencyKey` parameter on an API call is not
  evidence the call is actually retry-safe — check whether the key is generated fresh per attempt
  (useless) or persisted and reused across attempts (correct) before trusting it.
- **Shared helpers — use these instead of re-deriving the same logic** (introduced during the
  2026-07-21 security/quality hardening pass, see `docs/security-hardening-2026-07-21.md`):
  - `CandidateApplicationStatusExtensions.TerminalStatuses`/`.IsTerminal()` (`Entities/Enums.cs`) —
    the one definition of which `CandidateApplicationStatus` values are terminal. Use
    `TerminalStatuses.Contains(...)` in an EF Core LINQ query (translates to SQL `IN`) or
    `.IsTerminal()` on an already-materialized `Candidate`.
  - `AuditLogExtensions.AddAuditLog` (`Data/AuditLogExtensions.cs`) — replaces a service's own
    private `AddAudit`/inline `AuditLog` object-initializer.
  - `CandidatePiiFields.Clear` (`Entities/CandidatePiiFields.cs`) — the one definition of "PII
    cleared," shared by the immediate no-show purge and the scheduled retention purge.
  - `Team.ToEmailCredentials()` (`Email/EmailCredentials.cs`) — replaces the port-587/StartTLS-true
    fallback that used to be re-typed at every call site.
  - `AdminAccessScope.TryResolveManageableTeamId` — replaces the
    SystemAdmin-team-picker-vs-TeamAdmin-locked-to-own-team resolution.
- **Before building an in-app admin action, check whether ExamTools already does it.** This app's
  own ingestion polling means an ExamTools-side change always wins eventually anyway, so a duplicate
  in-app action is pure redundant maintenance surface, not a safety net — "add walk-in candidate"
  and "move candidate between sessions" were both built and then removed for exactly this reason
  (see Known Constraints).

## Change Log

One-line-or-two pointer per feature, newest first — full design rationale lives in the linked
`/docs/*.md` file, not here. See "Documentation Structure" below for the policy this follows.

**Kept here vs. `CHANGELOG.md`:** this section is a bounded, recent-only window (rule of thumb: cap
around 10 entries), since CLAUDE.md is read in full on every conversation turn and this is the one
section that would otherwise grow forever. Phase-numbered work (Phase 0-10) is never listed here at
all — it's already one-line-summarized in "Current State" above, so a separate Change Log pointer
would be pure duplication — and goes straight to `CHANGELOG.md` instead. Non-phase entries (fixes,
redesigns, hardening passes) start here and move to `CHANGELOG.md` once the section is at/over the
cap and a newer entry needs to be added; oldest goes first.

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
Everything through Phase 0-10's initial build (ExamTools ingestion, Zoom/Discord, Square, email
notifications, FCC ULS watcher, payment reminders, VE tracking, VEC submission tracker, admin
auth/config/candidate-actions, PII purge) plus the public privacy page has aged out to
**`CHANGELOG.md`** — same one-line-pointer format, just the overflow.

## Environment

- **IDEs**: Visual Studio (ASP.NET / C# projects), Visual Studio Code (PowerShell, small/misc apps)
- **OS/Hosting**: Ubuntu with Apache (primary)

## Tech Stack (in order of preference)

1. **ASP.NET Core 10 / C#** — primary language for applications
2. **PowerShell 7** — scripting, automation, deployment tasks
3. **Python** — only when it's clearly the better tool (data processing, one-off scripts, GIS integrations)
4. **JavaScript** — UI/UX only, kept minimal. jQuery is okay, but ask before using and JS frameworks (React, Vue, etc.).
5. **Nuget** - Use Nuget packages when it makes sense, but ask before installing.

## Coding Conventions

- The use of jQuery is acceptable. Use a JS framework/library when it makes the code cleaner and simpler to maintain. Ask before using any JS framework/library.
- Favor simple, readable solutions over clever/elaborate ones
- Use EF Core for data access on .NET projects unless told otherwise
- SQLite is the default DB for this project (no SQL Server instance available)
- When a 3rd party Nuget package could be used, ask for permission to use it and explain why it's needed.
- **C#**: Follow Microsoft's C# Coding Conventions (learn.microsoft.com/dotnet/csharp/fundamentals/coding-style/coding-conventions)
- **PowerShell**: Follow Microsoft's PowerShell scripting style guidelines and approved verbs (learn.microsoft.com/powershell/scripting/dev-cross-plat/vscode/vscode-powershell)

## Git Conventions

- Commits: Conventional Commits format (feat/fix/docs/chore/refactor: description)
- Branches: feature/, fix/, chore/, hotfix/ prefixes with short kebab-case description
- PRs: one logical change per PR; title matches commit convention
- **All changes land on `main` via a PR — no direct pushes — by convention, not server enforcement.** This repo is private on a free GitHub plan, which 403s branch protection ("Upgrade to GitHub Pro or make this repository public") — confirmed via `gh api .../branches/main/protection` (2026-07-21). The user chose to keep the repo private/free over upgrading or going public, so this is discipline-only; see `CONTRIBUTING.md`.

## Environments

- Two environments: **Test** and **Prod** (no separate "dev" — local machine serves that role)
- Config via `appsettings.Test.json` / `appsettings.Production.json`, selected by `ASPNETCORE_ENVIRONMENT`
- Secrets never go in appsettings files — see Security & Data Handling (Key Vault / user-secrets)
- Server/site topology and Test-vs-Prod endpoint differences vary by project — document per-repo

## Testing / Quality

- For more complex projects, build unit testing to maintain a level of quality on the project.
- (Add project-specific test framework and conventions here)

## Error Handling / Logging

- Use **Serilog** (`Serilog.AspNetCore`) for application logging, via the standard `ILogger<T>` interface
- Default sinks: rolling File sink + Console; add a Seq sink per-project if needed
- Do not log full PCI/PII data (see Security & Data Handling) — mask/redact sensitive fields before logging
- Use structured logging syntax (`{PropertyName}`) rather than string interpolation in log messages
- Reference: https://serilog.net/ | https://github.com/serilog/serilog-aspnetcore

## Security & Data Handling

### Secrets
- Never commit connection strings, API keys, tokens, or passwords to source control
- Use Azure Key Vault for production/shared secrets; use .NET user-secrets or environment variables for local dev
- If a secret is found in a commit, treat it as compromised — rotate it, don't just remove it from a future commit
- For PowerShell, utilize Export-Clixml/Import-Clixml for credentials

### Sensitive Data (PCI / PII)
- Cashiering and payment-related code must not log, cache, or persist full card numbers — PCI DSS scope applies
- Data from application databases may contain PII (SSNs, DOB, addresses) — avoid logging raw record data; mask/redact in logs and error messages
- Flag any new data flow that touches PCI/PII data so it can be reviewed against City compliance requirements

## Rollback / Versioning

- **Versioning**: Use semantic versioning (`v1.2.0`) for tagging releases in Git
- **Deployment retention**: Keep the previous systemd deployment folder/build untouched for a set period after a new release before cleanup, so rollback is a symlink/service-restart swap rather than a rebuild
- **Database changes**: Any schema migration must have a documented rollback path (down-migration script or pre-migration backup) — code rollback alone will not undo a schema change
- **Rollback authority**: Document who can decide to roll back and where that decision/action gets logged (e.g. commit, ticket, or team channel)
- (Add project-specific rollback steps and retention window once decided)

## Required Plugins

This project uses the `claude-tools` marketplace for shared team standards. If a plugin below shows as missing/not installed, install it before continuing:

```
/plugin marketplace add City-of-Mankato/claude-tools
/plugin install code-review@claude-tools
/plugin install powershell-deploy@claude-tools
/plugin install security-checklist@claude-tools
```

To pick up updates: `/plugin marketplace update claude-tools`

| Plugin | Purpose |
|---|---|
| `code-review` | Security/correctness/convention review checklist for code changes |
| `powershell-deploy` | PowerShell 7 deployment and automation script conventions |
| `security-checklist` | Secrets handling + PCI/PII data handling checklist |


## Known Constraints

- The deploy server is behind a Tailscale VPN — a GitHub-hosted Actions runner can't reach it directly. **Resolved (2026-07-21):** `.github/workflows/deploy.yml` uses a GitHub-hosted `ubuntu-latest` runner + a `tailscale/github-action@v3` step to join the tailnet ephemerally per-run (`tag:ci`, same OAuth client already used by the sibling `NcsScheduler` project on the same box) — no persistent self-hosted runner needed. Full setup in `docs/deployment.md`.
- **Deploy topology (2026-07-21):** two systemd services, `vesessionmanager-worker`/`vesessionmanager-web`, run as a dedicated `vesessionmanager` system account (not `www-data` — NcsScheduler's account on the same box) at `/opt/vesessionmanager/{worker,web}/`. They share one SQLite DB at `/var/lib/vesessionmanager/vesessionmanager.db`, deliberately **outside** the app path so `deploy.yml`'s `rsync --delete` can never touch it regardless of exclude flags (unlike NcsScheduler, whose DB sits inside its own synced app directory and is protected only by an `--exclude` flag every run). Deploy triggers only on a pushed version tag (`v*.*.*`), never on an ordinary commit. Because both Worker and Web call `dbContext.Database.Migrate()` at startup, the deploy workflow starts Worker first and confirms it's active before starting Web, to avoid both processes racing to apply the same SQLite migration concurrently. `appsettings.Production.json` needs no manual server-side editing — it carries no secrets (every real integration credential is per-`Team` in the DB, never in appsettings) and syncs automatically like any other file.
- **Duplicative-with-ExamTools features removed (reported 2026-07-21, removed 2026-07-21).** Phase 9b originally built "add walk-in candidate" and "move candidate to a different session" as in-app Session Manager actions, but both are already handled by ExamTools itself — a walk-in registered there, or a candidate moved between sessions there, already flows into this app through `SessionIngestionService`'s normal polling, same as any other candidate/session change. Building (and maintaining) a duplicate in-app path for either was unnecessary, so both were removed entirely: `CandidateActionService.AddWalkInAsync`/`MoveAsync`/`CandidateMoveResult`, their page handlers/modals/menu items in `Pages/SessionManager/Detail.cshtml(.cs)` (including the `CanMove`/`MoveTargetSessions` UI plumbing), their test coverage, and the corresponding spec.md bullet-list lines. See Established Patterns above for the general lesson.
- **Worker Service reads `DOTNET_ENVIRONMENT`, not `ASPNETCORE_ENVIRONMENT`.** `VeSessionManager.Worker` is a plain generic Host (`Host.CreateApplicationBuilder`), which only honors `DOTNET_ENVIRONMENT`. Only the Web project (`WebApplication.CreateBuilder`) reads `ASPNETCORE_ENVIRONMENT` (and falls back to `DOTNET_ENVIRONMENT`). The generic Host's own default when neither is set is `Production` — so running the Worker's built DLL directly (bypassing `launchSettings.json`, which sets `DOTNET_ENVIRONMENT=Development` for `dotnet run`) silently picks up `appsettings.Production.json`'s Linux-only paths and fails on a dev machine. Always use `dotnet run --project ...` locally for the Worker, not the raw `.dll`.
- **ExamTools login returns HTTP 200 on bad credentials** — failure is an `{"error": ...}` body, not a status code. Any code touching `POST /api/ve/login` must check the body (see `ExamToolsClient` and `docs/examtools-api.md`).
- **ExamTools has no "cancelled" session state** — cancellations are detected by a known session id disappearing from the team feed, reschedules by a changed `date` on the same id. Don't go looking for a status flag that isn't there.
- **Zoom Server-to-Server OAuth tokens have no refresh token** — they just expire after an hour; the only way to get a new one is to call `/oauth/token` again with the same `account_credentials` grant. `ZoomClient` caches and re-requests a minute before expiry rather than reacting to a 401.
- **`DateTimeOffset` construction from a Sqlite-round-tripped `DateTime` will throw if you're not careful** — EF Core/Sqlite returns `DateTimeKind.Unspecified`, and `new DateTimeOffset(dateTime, TimeSpan.Zero)` validates Kind against the offset. `DiscordEventClient.ToOffset()` forces `Kind = Utc` first; reuse that pattern anywhere else a stored `DateTime` needs to become a `DateTimeOffset`.
- **Never validate external-API credentials in a singleton's constructor if that singleton is resolved from inside a Worker `BackgroundService`.** A constructor throw there stops the *entire host* (.NET's default `BackgroundServiceExceptionBehavior` is `StopHost`) — discovered live when an unconfigured `Square:AccessToken` threw from `SquareClient`'s constructor and killed ExamTools/Zoom/Discord polling too, not just payment generation. `ExamToolsClient`/`ZoomClient`/`DiscordEventClient`/`SquareClient` all defer credential checks to first *use* (inside the method that needs them) for exactly this reason — keep new API clients consistent with that pattern. **The constructor is only half of it (2026-08-03):** the same `StopHost` default kills the Worker for anything thrown by a job's *per-tick* work outside `JobRunHistoryLogger` — settings/team loads, queue peeks, `LastIngestionRunUtc` stamps — and Web and Worker share one SQLite file, so a transient "database is locked" is enough. Every tick body is now wrapped in `JobTick.GuardedAsync`; **a new job's timer loop must use it too**. See `docs/worker-resilience.md`.
- **Square's `payment.updated` webhook does not include `reference_id`** — only `order_id`. `Payment.SquarePaymentReferenceId` stores the Square `order_id` (returned when the link is created), not our own `Order.ReferenceId` (which is set to `Payment.Id`, but only for human cross-referencing in Square's dashboard — it's never echoed back). See `docs/square-payments.md`.
- **When an "optional integration" gate combines multiple independent pieces (e.g. Zoom + Discord both feeding one `ZoomDiscordSyncedStartUtc`), do not write the per-piece "settled" check as `!IsConfigured || succeeded`.** That reads as "fine either way," which is wrong: it marks the *whole thing* settled (and stops retrying forever) the instant any one piece is unconfigured, even though the other piece may still be waiting. A dedicated test (`NeitherZoomNorDiscordConfigured_SessionStaysPending_NoCallsMade`) caught this before it shipped. Correct form: `succeeded` alone — a piece that's unconfigured simply never contributes toward "succeeded," so the aggregate stays unsettled (and gets retried, and logged once in aggregate) for as long as that piece stays unconfigured. See `SessionEventSchedulingService.SyncZoomAndDiscordAsync`.
- **A "create if id is null" check is not enough for any external API call whose success and its local persistence aren't atomic.** Real duplicate Discord events (~6, live incident 2026-07-21) were created because the process crashed/restarted after Discord's API call succeeded but before `SaveChangesAsync` persisted the returned id. See Established Patterns above for the fix pattern (query-before-create / persisted idempotency key) — this same bug class was found and fixed in Discord, Zoom, and Square the same day via proactive self-audit, not three separate reported incidents.
- **A client's `SmtpUsername`/similar "did the admin actually finish setup" signal is not the same as "is a hostname/URL present."** `SmtpEmailSender.IsConfigured` originally checked only `SmtpHost`, which has a real default baked into `appsettings.json` (`smtp.mailgun.org`) — so it read "configured" the instant the repo was cloned, before any credentials existed, and threw a real (if expected) `MailKit.ServiceNotAuthenticatedException` every poll instead of the intended quiet skip. Fixed by requiring `SmtpUsername` too.
- **Don't trust a PDF-to-text extraction's field-position numbers without cross-checking real data, even for a well-established public format.** The FCC's own ULS field-layout PDF lists FRN at EN position 24; a real downloaded `EN.dat` row's FRN-shaped (10-digit) value was actually at position 23 — the document had an extra phantom field between two real ones, an apparent PDF-extraction artifact. `FccUlsRecordParser` uses the position verified against live data, not the document's stated one. See `docs/fcc-uls-watcher.md`.
- **`decimal.ToString("C", CultureInfo.InvariantCulture)` does not produce a `$` sign.** The invariant culture's currency symbol is the generic `¤`. Caught while building Phase 6's `{{PaymentAmount}}` placeholder before it shipped — this app is US-only (FCC/ARRL), so `PaymentReminderService` formats money as a literal `$` prefix + `"F2"` instead. Any future money-in-a-string code should do the same, not reach for `"C"`/`InvariantCulture`.
- **`JobRunHistoryLogger.RunAsync` now takes a required `int? teamId` parameter** (positioned before `CancellationToken`, added for the multi-team foundation) — pass the real `Team.Id` for a per-team job step (like `SessionIngestionJob`'s ingestion loop) or `null` for anything still global (every other existing job). Every call site needed updating when this landed; don't forget it when adding a new job.
- **A scan-based service that loads "all local rows to diff against a remote feed" must scope that local query once the remote feed itself becomes per-team.** `SessionIngestionService`'s local `Sessions` query used to load every session in the DB (fine with one team); once `GetTeamSessionsAsync` started returning only one team's sessions, an unscoped local query would see *other* teams' still-active sessions as "missing from this team's feed" and wrongly cancel them. Fixed by adding `.Where(s => s.TeamId == team.Id)` — caught by a dedicated test (`TeamBIngestion_NeverCancelsTeamAsStillActiveSessions`) before it could ship. Keep this in mind for any future per-team service scan.
- **A dev/test seeder's "already seeded, skip" guard must check for the *specific* rows it seeds, not "does any row of this type exist at all."** `DevAuthSeeder`'s first version checked `userManager.Users.AnyAsync()` — but the Worker's own `DevDataSeeder` already creates a "System" `User` row (for `CreatedByUserId` audit trails) sharing the same table, so that guard was always true and the four Phase 9a test users never got seeded. Caught live during this phase's own Web smoke test. Fixed by checking for one of the specific seeded emails instead (`FindByEmailAsync("sessionmanager@example.com")`). Any future seeder sharing a table with another seeder needs the same specific-row check, not a table-wide existence check.
- **`app.UseAuthorization()` does nothing without `app.UseAuthentication()` before it** — the latter is what actually populates `HttpContext.User` from the request's auth cookie/token; `UseAuthorization()` just reads whatever `HttpContext.User` already is. `VeSessionManager.Web`'s pipeline had `UseAuthorization()` since Phase 0's scaffold but no `UseAuthentication()` at all, making it a silent no-op the entire time — nobody noticed because nothing used `[Authorize]` until Phase 9a. Both calls, in that order, are required any time authentication is added to an ASP.NET Core pipeline.
- **EF Core InMemory can't translate `OrderBy` chained directly onto a `GroupBy(...).Select(...)` join projection.** Hit building `VolunteerExaminerReportService.GetSessionCountsAsync` — fixed by materializing the grouped counts with `ToListAsync()` first, then ordering in memory. Worth remembering for any future report query shaped the same way.
- **Any page/service calling into `SessionAccessScope`/`AdminAccessScope` must load the user through `CurrentUserLoader.GetUserWithManagerAsync`, not the bare `userManager.GetUserAsync`.** Originally added because `SessionAccessScope.GetEffectiveTeamId`'s TeamLead branch read `user.ManagedByUser?.TeamId`, uneagerly-loaded by the bare `UserManager.GetUserAsync(ClaimsPrincipal)` — a TeamLead would sign in successfully and silently see zero sessions. **Now load-bearing for every role, not just TeamLead** (issues #17/#19): `User.TeamId` was replaced by the `UserTeams` join collection, and `GetUserWithManagerAsync` was extended to also `.Include(u => u.UserTeams).Include(u => u.ManagedByUser).ThenInclude(m => m!.UserTeams)` — a plain `GetUserAsync` now silently gives a TeamAdmin/SessionManager an *empty* team set (not just TeamLead a missing one), since `GetEffectiveTeamIds` reads `user.UserTeams` directly. A live audit during this change found several admin pages (`FeeConfigurations`, `EmailTemplates`, `TeamSettings`, `JobRunHistory`, `AuditLog`) still calling the bare `GetUserAsync` despite invoking these scope classes — all fixed the same way. See `docs/admin-auth.md`.
- **Razor `.cshtml` files are compiled into the assembly at build time in this app (no `AddRazorRuntimeCompilation()` configured)** — editing a `.cshtml` file while `dotnet run` is already running does **not** take effect; the process must be restarted, not just re-requested. Cost real debugging time once (a `_PublicLayout.cshtml` edit silently didn't apply until the dev server was relaunched).
- **A job tick timed for "the evening" in US Eastern can land at/after UTC midnight** — EDT is UTC-4, EST is UTC-5, so anything from ~8pm ET onward is already tomorrow in raw UTC. `TimeProvider.GetUtcNow().UtcDateTime.DayOfWeek` (or any UTC-based "what day is it" check) is wrong for that window; convert through `TimeZoneInfo.ConvertTimeFromUtc(..., FccUlsSchedule.EasternTimeZone)` first (IANA id `"America/New_York"`, resolves cross-platform since .NET 6 — verified directly on this repo's target framework on both Windows and the Linux deploy target). Found live 2026-07-23 building `FccDailyWatcherJob`'s same-day retry; see `docs/fcc-uls-watcher.md`. Reuse `FccUlsSchedule.EasternTimeZone` for any future US-Eastern-anchored scheduling rather than re-resolving the id.
- **Not every job here can safely reuse the "24h `PeriodicTimer` from Worker start, extra ticks are free" idiom** — that reasoning (used by `DayBeforeReminderJob`/`PaymentReminderJob`/`PiiPurgeJob`/`FccWeeklyCatchupJob`) assumes a missed tick is harmless because idempotent tracking catches it up next time. It breaks when the *data itself* — not just the job's own state — is only available in a narrow, non-retryable window, as with FCC's day-name files (see the same-day-retry entry above). Before adding a new job on this idiom, check whether the thing it polls has that same "one-shot window" property.
- **Square webhook subscriptions are separate per Sandbox/Production, each with its own signature key** — an existing subscription registered under one mode receives zero delivery attempts for events in the other (not a 401, no attempt at all), and reusing one mode's `WebhookSignatureKey` against the other mode's subscription makes every delivery fail signature verification (401) even though the URL/event config is otherwise correct. Found live 2026-07-25 testing Team 2 (MARC)'s payment flow — the "Ve Session Manager" subscription had been created under Production while all local testing used Sandbox credentials/payment links. Fix: add (or move) the subscription under the correct mode's tab in the Square dashboard, then set `Team.SquareWebhookSignatureKey` to *that* subscription's own signature key, not the other mode's. See `docs/square-payments.md`.
- **`Web` and `Worker` must register Data Protection with the exact same application name and key-ring path, or one process's writes silently become unreadable by the other.** `Team`'s credential columns (ExamTools/Zoom/Square/SMTP secrets) are encrypted at rest via `EncryptedStringConverter` (2026-07-30) — both `Program.cs` files call `AddDataProtection().SetApplicationName("VeSessionManager").PersistKeysToFileSystem(...)` with the same hardcoded app name and the same `DataProtection:KeyRingPath` config value. A drift here doesn't throw — `EncryptedStringConverter`'s legacy-plaintext fallback (needed for the migration path) means a value encrypted under a different key just looks like it was never migrated. See `docs/credential-encryption.md`. Also: **if the key-ring directory is ever lost, every encrypted credential becomes permanently unrecoverable** — it must be backed up with the same discipline as the DB file itself (see `docs/deployment.md`).
- **A POST form on a filtered list page needs BOTH an explicit `action=` and `asp-antiforgery="true"` — each half fixes a bug the other half causes.** `asp-page-handler` builds the form action from the route only and **drops the query string**, so posting an action from a filtered/paged list silently redirects back to the unfiltered first page (found on the Sessions row-action menu, 2026-07-30). The fix is an explicit `action="@Model.BuildActionUrl("Handler")"`. But `FormTagHelper` only auto-emits the antiforgery token when *it* generated the action — with an explicit `action=` the token disappears, and every POST then 400s in the antiforgery middleware **before reaching the app, logging nothing server-side** (the symptom is a browser error page with a completely silent log, which reads like the request never happened). `asp-antiforgery="true"` restores it. Any future list page with row-level POST actions needs both, plus a `BuildActionUrl`-style helper so the redirect target keeps the same filter state.
- **`wireless2.fcc.gov` (ULS's own web UI) returns Akamai "Access Denied" (HTTP 403) to automated requests, and has done so for at least one manual browser attempt too.** This is why `FccUlsLinks` ships the *licence* deep link (`UlsSearch/license.jsp?licKey=…`, whose shape is verified — ExamTools links to exactly it) but deliberately **not** an application deep link: the `applView.jsp?applID=…` shape has never been confirmed against a working response, and an unverified link would send a Session Manager to a dead page. `exam.tools`' own ULS mirror is unaffected and is what the app actually calls.
- **The FCC bulk-file constraints are historical as of 2026-07-31** — the weekly-snapshot staleness, the day-name publication schedule, the Sunday-file-is-empty trap, and the `AM.dat`/Grant-Date upgrade behaviour all described a subsystem this app no longer runs. They are preserved in `docs/fcc-uls-watcher.md` (marked as removed) because the *matching rules* they justify are still enforced in `UlsWatcherService`. The one that still bites day-to-day: **FCC's Grant Date does NOT advance on a class upgrade — the effective/last-action date does**, so any "did this exam produce a result?" check written against grant date is correct for a first-time licensee and permanently false for an upgrade. Confirming an upgrade needs the operator class matching `NewLicenseClass` **and** the effective date on/after the session; neither alone is sufficient. See `docs/uls-watcher.md`.
- **An "exclude this row" predicate written as `x.Id != someNullableInt` matches NOTHING when the
  value is null, and the InMemory provider won't reproduce it.** SQL `Id <> NULL` is NULL, not true,
  so a uniqueness check shaped `AnyAsync(v => v.Id != excludingId && ...)` returns zero rows on the
  create path (where there's no row to exclude) and waves every duplicate through — while EF
  InMemory evaluates the same expression as plain LINQ, where `Id != null` is true, so the tests pass.
  Take `int` and pass `0` (never a real key) instead of `int?`. Found writing
  `VecManagementService.MatchCodeIsTakenAsync` (2026-08-01). The general lesson: **provider-dependent
  behaviour — SQL null semantics, whether a query translates at all, whether a unique index tolerates
  repeated NULLs — cannot be verified on EF InMemory.** `VecExamToolsCodeSqliteTests` is the pattern
  for pinning those against a real `DataSource=:memory:` SQLite context.
- **`Session.Status == Active` does NOT mean "this session hasn't happened yet" — it means "not
  cancelled."** `Status` only ever leaves `Active` on cancellation; it is never set to Completed.
  "Completed" in the UI is *derived* at render time from `TestingCompletedUtc ?? ExamToolsClosedUtc`
  (issue #71), and neither field is written back to `Status`. So a query filtered on
  `Status == SessionStatus.Active` returns **every session the team has ever run**, forever — which
  is how `VolunteerExaminerSyncService` ended up re-polling a team's entire history hourly for
  months (found 2026-07-31, see `docs/historical-import.md`). It also makes the bug near-invisible:
  every screen shows those sessions as Completed, so the code reads as if it already filters them.
  For "is this session finished?", test `ExamToolsClosedUtc`/`TestingCompletedUtc` (plus `HasEnded`
  as the backstop for rows predating `ExamToolsClosedUtc`), never `Status`.
- **`SessionAccessScope` has two team-resolution methods and picking the wrong one silently empties a page.** `ResolveViewableTeamIds(user, selectedTeamId)` returns the team-id *set* to filter by, where **null means every team** (SystemAdmin, unfiltered) — use it for any list that can render several teams merged. `TryResolveViewableTeamId` collapses to a *single* team and returns null for "no team context, show nothing" — only correct for a page that genuinely cannot render without one team chosen. Applicant Status and Unmatched Payments used the latter and so had no "All teams" and bounced to an empty page after every action (fixed 2026-07-30). Related trap in the same area: a guard written as `GetEffectiveTeamIds(user)?.Contains(id) ?? false` is **always false for a SystemAdmin** (that method returns null for them, meaning "all teams"), which is exactly how a SystemAdmin ended up 403ing on every unmatched-payment match.
- **`[Required]` on a non-nullable `bool` is a client-side-only guard — it never fails server-side.**
  The checkbox tag helper posts a hidden `false`, and any bound value satisfies `Required` for a
  value type, so `ModelState.IsValid` is always true for that field. Found on the anonymous
  youth-rate page, where it meant a direct POST could claim the discount with no attestation
  (2026-08-03). Any "must tick this box" rule needs an explicit handler check (or
  `[Range(typeof(bool), "true", "true")]`); keep `[Required]` only for the browser experience.
- **A per-IP rate limiter behind a reverse proxy needs `UseForwardedHeaders`, or it becomes a
  self-inflicted outage** — without it every request carries the proxy's loopback address, so all
  clients share one partition and a handful of requests locks out everyone. Added together
  2026-08-03; the defaults trust loopback proxies, which matches this deployment's same-box Apache.
  Same middleware is what makes `Request.Scheme` correct behind TLS termination.
- **`AllowedHosts` is pinned to `ve.wx0mik.radio` in Web's `appsettings.Production.json`
  (2026-08-03).** A deployment served under any other hostname — beta box, staging name, bare IP —
  returns **400 Bad Request for every request** until that value and `App:PublicBaseUrl` beside it
  are updated. Both take a semicolon-separated list. Pinned because the framework default `"*"`
  combined with request-host-derived absolute URLs was an admin-account-takeover vector; see
  `docs/security-hardening-2026-08-03.md`.
- **Browser-verifying any authenticated page needs Mike to log in — Claude will not type the dev
  password into the login form.** Every Session Manager and Admin page is `[Authorize]`d, so a UI
  change can't be clicked through until someone signs in. Claude declines to enter a password to
  authenticate as a standing rule; that this one is a throwaway dev fixture published in the README
  and `DevAuthSeeder.DevPassword` doesn't change it, and knowing the password was never the blocker.
  Agreed working arrangement (2026-07-31): **Mike logs in once at `http://localhost:5158/Account/Login`,
  and the auth cookie carries the rest of the session** — Claude can then navigate, click, and read
  pages freely without touching the login form again. Plan for this step rather than discovering it
  mid-task; if it's not worth the interruption, the fallback is shipping verified by `dotnet build`
  + `dotnet test` only, with the UI clicked through by Mike. **Front-end logic that doesn't depend on
  real data can still be verified unattended** — an `<iframe srcdoc>` harness that loads
  `/js/app.js` against a synthetic table exercises the real shipped code with no login (used to
  verify the table sorter, 2026-07-31). Watch one trap there: re-`eval`ing `app.js` in an
  already-loaded page and dispatching a synthetic `DOMContentLoaded` **also re-fires the original
  instance's listener**, double-initialising every handler and making one click run two state
  cycles — which reads as a real bug and isn't. Use a fresh iframe, not `eval` + dispatch.
- (Environment-specific quirks and gotchas go here as they're discovered — e.g. API quirks, IIS behavior, network/DMZ restrictions, auth issues)

## Definition of Done

- Code builds without warnings
- Unit tests pass (where applicable per Testing/Quality section)
- No secrets, connection strings, or sensitive data committed
- Documentation updated in the appropriate file per Documentation Structure (README, CONTRIBUTING.md, ARCHITECTURE.md, SECURITY.md, or /docs) if setup/config/behavior changed
- CLAUDE.md updated if a new architecture decision, gotcha, or config quirk was introduced — per Documentation Structure below, as a **pointer**, not a full narrative
- Reviewed by the other team member before merge when available; repo admins may bypass this requirement (e.g. during PTO) — do not hard-block merges on a single reviewer
- Claude should review code changes for security issues (secrets, injection risks, auth/permission gaps), correctness, and adherence to this file's conventions before a PR is finalized — this supplements but does not replace human review

## Documentation Structure

Keep `README.md` high-level; route deeper technical content to the right file so the README doesn't bloat:

| File | Purpose | Content |
|---|---|---|
| `README.md` | The "storefront" | What the project is, install steps, quick start, basic usage |
| `CONTRIBUTING.md` | The "workshop manual" | Local dev setup, running tests, code style, branching strategy |
| `ARCHITECTURE.md` | System overview | How components interact, high-level technical design |
| `SECURITY.md` | Security policy | How to report a vulnerability, security handling policy |
| `CHANGELOG.md` | The "attic" | Full history of one-line Change Log pointer entries, newest first — overflow for CLAUDE.md's own Change Log once it ages past the recent-only cap (see that section) |
| `/docs` folder | The "blueprint room" | Deep technical detail: architecture decisions, API specs, DB schemas, troubleshooting playbooks — as individual `.md` files (e.g. `docs/deployment.md`) |

- Use a GitHub Wiki or GitHub Pages only if documentation needs to be browsable outside the repo (e.g. for external stakeholders) — not needed for internal City projects by default
- Ownership, contacts, and escalation info belong in the README, not in this file
- **CLAUDE.md is read in full on every conversation turn, so its size is a permanent, compounding cost — write new content directly into `/docs` and leave only a pointer in CLAUDE.md's Change Log, and only for as long as that entry stays within the Change Log's own recent-only cap before moving to `CHANGELOG.md`.** Three kinds of content earn a permanent home in CLAUDE.md itself, dense prose and all: (1) standing rules/conventions that shape every future decision (the sections below this one), (2) Established Patterns — cross-cutting conventions, not tied to one phase, (3) Known Constraints — short, sharp "this will silently break if you don't know X" gotchas. Everything else — the narrative of what was built, why, and what was learned building it — belongs in `/docs`, written there at the time, not accumulated here and split out later once the file gets unwieldy. A completed phase's Change Log pointer never even starts in CLAUDE.md at all if it's already summarized in "Current State" — straight to `CHANGELOG.md`.

## Instructions for Claude

- Do not guess at facts, APIs, or library behavior — verify, and cite sources/docs when possible
- Keep responses concise by default; expand only when asked
- **If you abandon a slow command and try another approach, kill the first one as you pivot.** The
  failure mode is not the slowness, it's the orphan: a `find`/search/build takes too long, you switch
  tactics, get your answer elsewhere — and the original is still running, forgotten, because nothing
  ever reported back on it. Terminate it at the moment you decide to stop caring about it, not
  "later". Beyond the wasted work, an abandoned `dotnet run` holds a lock on the build output (see
  Known Constraints), so it turns a *later, unrelated* `dotnet build` into a spurious failure that
  looks like a code problem. **Two things this does not license:** killing anything mid-flight whose
  side effects would be left half-applied (a deploy, a migration, a historical import, a bulk email
  pass — those stop through their own mechanism or run to completion), and killing a process **you**
  didn't start. Mike runs Web/Worker locally on purpose; ask before stopping anything you didn't
  launch yourself, however tidy it would be.
- When producing code, include setup/run instructions for **Visual Studio** and/or **VS Code** as appropriate for the project type
- Flag any assumptions explicitly rather than silently filling gaps
- For deployment/CI tasks, default to GitHub Actions targeting Linux (systemd deploy, matching the NcsScheduler pattern), GitHub Flow branching; deploy trigger is on tag push only, not every commit (see Phase 0 in docs/spec.md)
- Maintain repo documentation per the Documentation Structure section above — route content to the right file rather than piling everything into README
- **When a feature/phase is done, write its full design rationale into a new or existing `/docs/<topic>.md` file, and add only a 1-2 sentence pointer to CLAUDE.md's Change Log** — see Documentation Structure above. Reserve CLAUDE.md's own prose for Established Patterns (truly cross-cutting) and Known Constraints (gotchas) — don't let a Change Log entry grow into a full narrative the way earlier entries did before this policy existed. If it's a numbered spec phase already covered by "Current State," skip CLAUDE.md's Change Log entirely and add the pointer straight to `CHANGELOG.md`. If the Change Log is at/over its recent-only cap (~10 entries) when adding a new one, move the oldest entry there first.

## Notes

- This file is a starting template — update per-repo as conventions solidify.
