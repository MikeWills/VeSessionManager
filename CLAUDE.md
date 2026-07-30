# CLAUDE.md

This is a Visual Studio project that is designed to automate many of the mundane tasks that a Amateur Radio Volunteer Examiner (VE) Session Manager (SM) needs to do to run a session include creating a Zoom session, sending payment links and reminder emails. See docs/spec.md for details.

## Current State

- **All phases of `docs/spec.md` are implemented, Phase 0 through Phase 10** (Phase 0 foundation, Phase 1 ExamTools session/candidate ingestion, Phase 2 Zoom + Discord event scheduling, Phase 3 Square payment links + webhook, Phase 4 candidate notification emails + templates, Phase 5 FCC ULS application/license watcher, Phase 6 payment reminder & expiration job, Phase 7 VE tracking, Phase 8 VEC submission tracker, Phase 9a-9d admin backend auth/scaffolding/candidate actions/config screens/privacy page, Phase 10 PII purge job) — see spec.md's own Backlog section for unscoped future work (VEC discount programs, no-FRN batch export) and TODO.md for known gaps.
- Build/test/run: `dotnet build`, `dotnet test`, `dotnet run --project src/VeSessionManager.Worker`, `dotnet run --project src/VeSessionManager.Web` (see README, and Known Constraints below, for the `DOTNET_ENVIRONMENT` gotcha). Tests are xUnit in `tests/VeSessionManager.Core.Tests`, using the EF InMemory provider and fake client implementations — follow `SessionIngestionServiceTests`/`SessionEventSchedulingServiceTests`/`PaymentGenerationServiceTests`/`CandidateNotificationServiceTests`/`FccUlsWatcherServiceTests`/`PaymentReminderServiceTests` as the pattern.

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
- **`www.fcc.gov` (the FCC's documentation/PDF pages) blocks or heavily throttles non-browser HTTP clients — `data.fcc.gov` (the actual ULS download host) does not.** Discovered while researching Phase 5: plain `curl`/`WebFetch` requests to `www.fcc.gov/file/.../download` reliably hung or reset, while identical requests to `data.fcc.gov/download/pub/uls/...` (what `FccUlsClient` actually calls) returned instantly. If FCC connectivity ever looks broken, check which host is actually failing before assuming the download API itself is down.
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
