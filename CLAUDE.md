# CLAUDE.md

This is a Visual Studio project that is designed to automate many of the mundane tasks that a Amateur Radio Volunteer Examiner (VE) Session Manager (SM) needs to do to run a session include creating a Zoom session, sending payment links and reminder emails. See docs/spec.md for details.

## Current State

- **All phases of `docs/spec.md` are implemented, Phase 0 through Phase 10** (Phase 0 foundation, Phase 1 ExamTools session/candidate ingestion, Phase 2 Zoom + Discord event scheduling, Phase 3 Square payment links + webhook, Phase 4 candidate notification emails + templates, Phase 5 ULS application/license watcher (rewritten 2026-07-31 onto ExamTools' ULS API — see `docs/uls-watcher.md`), Phase 6 payment reminder & expiration job, Phase 7 VE tracking, Phase 8 VEC submission tracker, Phase 9a-9d admin backend auth/scaffolding/candidate actions/config screens/privacy page, Phase 10 PII purge job) — see spec.md's own Backlog section for unscoped future work (VEC discount programs, no-FRN batch export) and TODO.md for known gaps.
- **Outstanding review findings live in [`docs/audit-2026-08-03-tasks.md`](docs/audit-2026-08-03-tasks.md)
  — check it before starting unrelated work.** A six-agent review (security, optimization/dead code,
  and traceability across four layers) produced 39 findings as self-contained tasks: problem, files,
  fix, acceptance criteria. **14 are done, 25 remain.** The completed ones were the P0/P1 tier —
  authorization, public-internet hardening, Worker resilience, the payment race, the PII purge gap,
  and the Square/ExamTools config drift (T04, done 2026-08-06 before live payment testing). What's
  left is led by **T12** (the Data Protection key ring sitting beside the SQLite database, so one
  leaked backup carries both ciphertext and key), then a long tail of quality work. That file also records what the review verified as **clean**, so
  those areas don't need re-auditing. This pointer is deliberately here rather than in the Change Log
  below, which rotates entries out to `CHANGELOG.md` and would eventually take the only reference
  with it.
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
- **The per-team refresh pipeline is defined once, in `TeamPipeline`** (`Core/Ingestion`) — ingest,
  VE roster, exam results, Zoom/Discord, payment links, confirmation emails, in that order. **Add a
  new step there, not at a call site.** The order used to be written out three times (the Worker's
  `SessionIngestionJob`, and twice in `ManualCandidateRefreshService` for the team-wide and
  session-scoped buttons) and the copies drifted — exam-result sync was missing from the manual path
  for weeks while its own doc comment claimed to mirror the job. Callers vary only by a job-name
  prefix (`""` scheduled, `"Manual"` user-triggered) and an optional `onlySessionId`; two steps
  switch *method* rather than take a filter when scoped to one session, and that branch is inside
  the pipeline with the reasons attached.
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

- **Job Schedule page: when every background job runs next (2026-08-06).** See
  `docs/job-schedule.md`. "When does the next run happen?" was answerable only by reading the Worker's
  source — Job History records what happened, never what will. New `JobSchedules` registry in Core is
  the **one definition of every job's cadence, read by both hosts**: the Worker to schedule, Web to
  report, so the page cannot drift the way `TeamPipeline`'s order once did. `Jobs:*` config moved to
  `appsettings.Shared.json` for the same reason (Web resolves those keys now). Two cadence shapes are
  reported differently on purpose — **anchored** jobs (ULS 08:00/20:00 ET, LicenseWatch 06:00 ET) state
  a real time and show `Due now` when a slot is unrun, **interval** jobs are last-run-plus-interval and
  labelled *estimated*, because their timer restarts with the Worker. Tests caught two bugs first:
  `Max` over an empty filtered sequence **throws** rather than returning null (one perpetually-failing
  job would have taken down the whole page — the nullable cast must be *inside* `Max`), and advancing
  an anchored slot by adding hours to a UTC value is an hour off across DST.
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

Everything through Phase 0-10's initial build (ExamTools ingestion, Zoom/Discord, Square, email
notifications, FCC ULS watcher, payment reminders, VE tracking, VEC submission tracker, admin
auth/config/candidate-actions, PII purge), the public privacy page, and everything dated 2026-08-01
or earlier has aged out to **`CHANGELOG.md`** — same one-line-pointer format, just the overflow.

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
- **Duplicative-with-ExamTools features removed (reported 2026-07-21, removed 2026-07-21).** Phase 9b originally built "add walk-in candidate" and "move candidate to a different session" as in-app Session Manager actions, but both are already handled by ExamTools itself — a walk-in registered there, or a candidate moved between sessions there, already flows into this app through `SessionIngestionService`'s normal polling, same as any other candidate/session change. Building (and maintaining) a duplicate in-app path for either was unnecessary, so both were removed entirely: `CandidateActionService.AddWalkInAsync`/`MoveAsync`/`CandidateMoveResult`, their page handlers/modals/menu items in `Pages/SessionManager/Detail.cshtml(.cs)` (including the `CanMove`/`MoveTargetSessions` UI plumbing), their test coverage, and the corresponding spec.md bullet-list lines. **Third instance, removed 2026-08-07: session detail's VE roster editing** (`VolunteerExaminerRosterService` + the "+ Add VE" modal and per-chip remove) — `VolunteerExaminerSyncService` fully reconciles each session's roster from ExamTools on every poll, so an edit made here was reverted on the next tick precisely when ExamTools disagreed, i.e. whenever the button was worth pressing. The roster is now display-only. See Established Patterns above for the general lesson.
- **Worker Service reads `DOTNET_ENVIRONMENT`, not `ASPNETCORE_ENVIRONMENT`.** `VeSessionManager.Worker` is a plain generic Host (`Host.CreateApplicationBuilder`), which only honors `DOTNET_ENVIRONMENT`. Only the Web project (`WebApplication.CreateBuilder`) reads `ASPNETCORE_ENVIRONMENT` (and falls back to `DOTNET_ENVIRONMENT`). The generic Host's own default when neither is set is `Production` — so running the Worker's built DLL directly (bypassing `launchSettings.json`, which sets `DOTNET_ENVIRONMENT=Development` for `dotnet run`) silently picks up `appsettings.Production.json`'s Linux-only paths and fails on a dev machine. Always use `dotnet run --project ...` locally for the Worker, not the raw `.dll`.
- **Every ExamTools action this app takes is attributed to the stored credential's account, and ExamTools is starting to show its audit log to VEs** (alpha site already; reported 2026-08-07). The end user who clicked is invisible there — every entry reads as whichever VE's login is in `Team.ExamToolsUsername`. Harmless while the app is read-only against ExamTools, which it is today; it becomes a real cost the moment any write-back feature is considered, because this app's audit log would know who acted and ExamTools' would not. Weigh it alongside the "check whether ExamTools already does it" pattern above. See README's Configuration & Secrets note.
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
- **Any page/service calling into `SessionAccessScope`/`AdminAccessScope` must load the user through `CurrentUserLoader.GetUserWithManagerAsync`, not the bare `userManager.GetUserAsync`.** Originally added because `SessionAccessScope`'s TeamLead branch read the manager's team, uneagerly-loaded by the bare `UserManager.GetUserAsync(ClaimsPrincipal)` — a TeamLead would sign in successfully and silently see zero sessions. **That transitive scoping is gone (2026-08-07) — a TeamLead now reads their own `UserTeams` like every other scoped role** (a manager may span several teams while a lead belongs to one, so inheriting leaked the manager's other teams; see `docs/admin-auth.md`). **Now load-bearing for every role, not just TeamLead** (issues #17/#19): `User.TeamId` was replaced by the `UserTeams` join collection, and `GetUserWithManagerAsync` was extended to also `.Include(u => u.UserTeams).Include(u => u.ManagedByUser).ThenInclude(m => m!.UserTeams)` — a plain `GetUserAsync` now silently gives a TeamAdmin/SessionManager an *empty* team set (not just TeamLead a missing one), since `GetEffectiveTeamIds` reads `user.UserTeams` directly. A live audit during this change found several admin pages (`FeeConfigurations`, `EmailTemplates`, `TeamSettings`, `JobRunHistory`, `AuditLog`) still calling the bare `GetUserAsync` despite invoking these scope classes — all fixed the same way. See `docs/admin-auth.md`.
- **Razor `.cshtml` files are compiled into the assembly at build time in this app (no `AddRazorRuntimeCompilation()` configured)** — editing a `.cshtml` file while `dotnet run` is already running does **not** take effect; the process must be restarted, not just re-requested. Cost real debugging time once (a `_PublicLayout.cshtml` edit silently didn't apply until the dev server was relaunched).
- **A job tick timed for "the evening" in US Eastern can land at/after UTC midnight** — EDT is UTC-4, EST is UTC-5, so anything from ~8pm ET onward is already tomorrow in raw UTC. `TimeProvider.GetUtcNow().UtcDateTime.DayOfWeek` (or any UTC-based "what day is it" check) is wrong for that window; convert through `TimeZoneInfo.ConvertTimeFromUtc(..., FccUlsSchedule.EasternTimeZone)` first (IANA id `"America/New_York"`, resolves cross-platform since .NET 6 — verified directly on this repo's target framework on both Windows and the Linux deploy target). Found live 2026-07-23 building `FccDailyWatcherJob`'s same-day retry; see `docs/fcc-uls-watcher.md`. Reuse `FccUlsSchedule.EasternTimeZone` for any future US-Eastern-anchored scheduling rather than re-resolving the id.
- **Not every job here can safely reuse the "24h `PeriodicTimer` from Worker start, extra ticks are free" idiom** — that reasoning (used by `DayBeforeReminderJob`/`PaymentReminderJob`/`PiiPurgeJob`/`FccWeeklyCatchupJob`) assumes a missed tick is harmless because idempotent tracking catches it up next time. It breaks when the *data itself* — not just the job's own state — is only available in a narrow, non-retryable window, as with FCC's day-name files (see the same-day-retry entry above). Before adding a new job on this idiom, check whether the thing it polls has that same "one-shot window" property.
- **Square webhook subscriptions are separate per Sandbox/Production, each with its own signature key** — an existing subscription registered under one mode receives zero delivery attempts for events in the other (not a 401, no attempt at all), and reusing one mode's `WebhookSignatureKey` against the other mode's subscription makes every delivery fail signature verification (401) even though the URL/event config is otherwise correct. Found live 2026-07-25 testing Team 2 (MARC)'s payment flow — the "Ve Session Manager" subscription had been created under Production while all local testing used Sandbox credentials/payment links. Fix: add (or move) the subscription under the correct mode's tab in the Square dashboard, then set `Team.SquareWebhookSignatureKey` to *that* subscription's own signature key, not the other mode's. See `docs/square-payments.md`. **Which mode a team is in is now `Team.SquareEnvironment`, not a config value (2026-08-06)** — so this is per-team, and two teams on one deployment can legitimately be in different modes, each needing its own subscription. A team whose access token and environment disagree gets an auth failure from Square rather than a wrong-account charge.
- **`Web` and `Worker` must register Data Protection with the exact same application name and key-ring path, or one process's writes silently become unreadable by the other.** `Team`'s credential columns (ExamTools/Zoom/Square/SMTP secrets) are encrypted at rest via `EncryptedStringConverter` (2026-07-30) — both `Program.cs` files call `AddDataProtection().SetApplicationName("VeSessionManager").PersistKeysToFileSystem(...)` with the same hardcoded app name and the same `DataProtection:KeyRingPath` config value. A drift here doesn't throw — `EncryptedStringConverter`'s legacy-plaintext fallback (needed for the migration path) means a value encrypted under a different key just looks like it was never migrated. See `docs/credential-encryption.md`. Also: **if the key-ring directory is ever lost, every encrypted credential becomes permanently unrecoverable** — it must be backed up with the same discipline as the DB file itself (see `docs/deployment.md`).
- **A POST form on a filtered list page needs BOTH an explicit `action=` and `asp-antiforgery="true"` — each half fixes a bug the other half causes.** `asp-page-handler` builds the form action from the route only and **drops the query string**, so posting an action from a filtered/paged list silently redirects back to the unfiltered first page (found on the Sessions row-action menu, 2026-07-30). The fix is an explicit `action="@Model.BuildActionUrl("Handler")"`. But `FormTagHelper` only auto-emits the antiforgery token when *it* generated the action — with an explicit `action=` the token disappears, and every POST then 400s in the antiforgery middleware **before reaching the app, logging nothing server-side** (the symptom is a browser error page with a completely silent log, which reads like the request never happened). `asp-antiforgery="true"` restores it. Any future list page with row-level POST actions needs both, plus a `BuildActionUrl`-style helper so the redirect target keeps the same filter state.
- **`wireless2.fcc.gov` (ULS's own web UI) returns Akamai "Access Denied" (HTTP 403) to automated requests, and has done so for at least one manual browser attempt too.** This is why `FccUlsLinks` ships the *license* deep link (`UlsSearch/license.jsp?licKey=…`, whose shape is verified — ExamTools links to exactly it) but deliberately **not** an application deep link: the `applView.jsp?applID=…` shape has never been confirmed against a working response, and an unverified link would send a Session Manager to a dead page. `exam.tools`' own ULS mirror is unaffected and is what the app actually calls.
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
  as the backstop for rows predating `ExamToolsClosedUtc`), never `Status`. **Second instance found
  2026-08-06:** the VE Roster's "sessions worked" count had the same filter, so a VE rostered onto a
  *future* session already had it in their total — the bug is easy to reintroduce precisely because
  `Status == SessionStatus.Active` reads like "currently running." When the answer must translate to
  SQL, use `TestingCompletedUtc != null || ExamToolsClosedUtc != null` (what the Sessions list's
  "Completed" chip derives); `HasEnded` is plain C# arithmetic and cannot be used query-side.
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
- **In `app.css`, a single-class selector loses to `.vesm button` / `.vesm a` — both are (0,1,1),
  class *plus element*.** Two separate live bugs came from this: anchor-buttons inheriting the body
  colour (`.vesm a { color: inherit }` beating `.btn-primary`, white-on-black CTA, 2026-08-04), and
  the mobile hamburger staying visible on desktop (`.vesm button { display: inline-block }` beating
  `.nav-toggle { display: none }`, 2026-08-05). Any new rule that fights those two base rules on
  `color` or `display` needs **two classes** — write `.vesm .nav-toggle`, not `.nav-toggle`. The
  symptom is silent: the rule is in the file, spelled correctly, and simply doesn't apply.
- **The Web app cannot be loaded in an iframe, which rules out the obvious way to test responsive
  layout.** The 2026-08-03 hardening pass sends `X-Frame-Options: DENY` and CSP
  `frame-ancestors 'none'`, so framing `localhost:5158` at a phone width fails with a broken-image
  placeholder, not an error — and Chrome separately enforces a ~500px minimum window width, so
  resizing the window can't produce a phone viewport either. **Don't weaken the headers to test
  layout.** Use a self-contained harness instead (real `app.css`/`app.js` inlined + representative
  markup, served over a throwaway local HTTP server, loaded in a sized iframe); it needs no login and
  can assert on `scrollWidth` vs `clientWidth`. Recipe and the `</script>` escaping trap in
  `docs/responsive-ui.md`.
- **Never use a bare Unicode symbol for a UI affordance — use Bootstrap Icons** (`<i class="bi bi-*"
  aria-hidden="true">`, or the font's codepoint in a CSS `content:`). A symbol character renders only
  if the device happens to have a font containing it, and **that differs per device**: IBM Plex Mono
  ships `B2`/`BC` but not `B8`, so the withdrawn-roster marker looked correct on the dev
  machine and rendered as a **tofu box on an iPhone** (2026-08-06). The sort arrows two rules away
  used plain triangles and were fine, which is what made it look like a proven technique. Icons are
  self-hosted at `wwwroot/lib/bootstrap-icons` because the CSP allows `font-src 'self'` only — a CDN
  reference is blocked. See `docs/icons.md`. **Two traps when editing:** an icon inside a C# string
  literal (`@(x ? "<i …>" : "·")`) breaks the Razor expression, and a bulk replace will also rewrite
  arrows sitting in Razor comment prose.
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
