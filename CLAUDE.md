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
  the VEC's job"). **All grant rules carried over unchanged** — Active-only, new-licence grant date
  on/after session, and the two-part upgrade test — now using `effective_date` (ExamTools' rendering
  of HD Last Action Date) in place of AM.dat + Last Action Date. Verified live in both directions the
  same day: two candidates were correctly withheld at 10:00 (class still Technician) and correctly
  granted at 11:30 once the class moved. Still twice a day (08:00/20:00 ET); the weekly catch-up job
  is gone entirely (a lookup returns current state, so there is no one-shot window to miss) and the
  three `--run-fcc-*` switches collapse to `--run-uls`. Migration `UlsWatcherReplacesFccFiles` is
  **hand-written** — EF's scaffolder paired the columns by position and would have set start-hour 24
  and a 1-hour interval. New `Candidate.UlsApplicationFileNumber`; Applicant Status gains the ULS
  licence link and the application file number (no application deep link — `wireless2.fcc.gov` 403s,
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
- **Never validate external-API credentials in a singleton's constructor if that singleton is resolved from inside a Worker `BackgroundService`.** A constructor throw there stops the *entire host* (.NET's default `BackgroundServiceExceptionBehavior` is `StopHost`) — discovered live when an unconfigured `Square:AccessToken` threw from `SquareClient`'s constructor and killed ExamTools/Zoom/Discord polling too, not just payment generation. `ExamToolsClient`/`ZoomClient`/`DiscordEventClient`/`SquareClient` all defer credential checks to first *use* (inside the method that needs them) for exactly this reason — keep new API clients consistent with that pattern.
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
- **`SessionAccessScope` has two team-resolution methods and picking the wrong one silently empties a page.** `ResolveViewableTeamIds(user, selectedTeamId)` returns the team-id *set* to filter by, where **null means every team** (SystemAdmin, unfiltered) — use it for any list that can render several teams merged. `TryResolveViewableTeamId` collapses to a *single* team and returns null for "no team context, show nothing" — only correct for a page that genuinely cannot render without one team chosen. Applicant Status and Unmatched Payments used the latter and so had no "All teams" and bounced to an empty page after every action (fixed 2026-07-30). Related trap in the same area: a guard written as `GetEffectiveTeamIds(user)?.Contains(id) ?? false` is **always false for a SystemAdmin** (that method returns null for them, meaning "all teams"), which is exactly how a SystemAdmin ended up 403ing on every unmatched-payment match.
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
- When producing code, include setup/run instructions for **Visual Studio** and/or **VS Code** as appropriate for the project type
- Flag any assumptions explicitly rather than silently filling gaps
- For deployment/CI tasks, default to GitHub Actions targeting Linux (systemd deploy, matching the NcsScheduler pattern), GitHub Flow branching; deploy trigger is on tag push only, not every commit (see Phase 0 in docs/spec.md)
- Maintain repo documentation per the Documentation Structure section above — route content to the right file rather than piling everything into README
- **When a feature/phase is done, write its full design rationale into a new or existing `/docs/<topic>.md` file, and add only a 1-2 sentence pointer to CLAUDE.md's Change Log** — see Documentation Structure above. Reserve CLAUDE.md's own prose for Established Patterns (truly cross-cutting) and Known Constraints (gotchas) — don't let a Change Log entry grow into a full narrative the way earlier entries did before this policy existed. If it's a numbered spec phase already covered by "Current State," skip CLAUDE.md's Change Log entirely and add the pointer straight to `CHANGELOG.md`. If the Change Log is at/over its recent-only cap (~10 entries) when adding a new one, move the oldest entry there first.

## Notes

- This file is a starting template — update per-repo as conventions solidify.
