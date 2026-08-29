# CLAUDE.md

This is a Visual Studio project that is designed to automate many of the mundane tasks that a Amateur Radio Volunteer Examiner (VE) Session Manager (SM) needs to do to run a session include creating a Zoom session, sending payment links and reminder emails. See docs/spec.md for details.

## Current State

- **All phases of `docs/spec.md` are implemented, Phase 0 through Phase 10** (Phase 0 foundation, Phase 1 ExamTools session/candidate ingestion, Phase 2 Zoom + Discord event scheduling, Phase 3 Square payment links + webhook, Phase 4 candidate notification emails + templates, Phase 5 ULS application/license watcher (rewritten 2026-07-31 onto ExamTools' ULS API — see `docs/uls-watcher.md`), Phase 6 payment reminder & expiration job, Phase 7 VE tracking, Phase 8 VEC submission tracker, Phase 9a-9d admin backend auth/scaffolding/candidate actions/config screens/privacy page, Phase 10 PII purge job) — remaining work of every kind (unscoped future features, operational setup, review findings) is in GitHub issues, per the next bullet.
- **GitHub issues are the single list of outstanding work (consolidated 2026-08-10) — check them
  before starting anything, and file new work there, not in a markdown file.** `TODO.md`,
  `docs/audit-2026-08-03-tasks.md` and `docs/spec.md`'s Backlog were each a separate list of "what's
  left"; all three are now stubs pointing at issues. Useful labels: `ops` (configuration/server work,
  not code), `audit-2026-08-03`, `audit-2026-08-11`, `needs-design`, `security`, `tech-debt`.
  Leading item, needing a human rather than code:
  **[#185](https://github.com/MikeWills/VeSessionManager/issues/185)**, which is now **only the
  Google/Microsoft SSO configuration** — client secrets are app-level, so they need
  `Authentication__*` environment variables in the systemd units. **This entry used to say "no
  production account can sign in until the first SystemAdmin exists". That has been false since
  2026-08-10**, when `--create-admin` landed and the issue itself was corrected; the description here
  was never updated, and it was still being read back as a live blocker on 2026-08-13 while the
  production SystemAdmin had been signing in for over a week. A stale summary of an issue outlives
  the issue, which is the argument for pointing at issues rather than restating them.
  **[#256](https://github.com/MikeWills/VeSessionManager/issues/256) (off-box backup) closed
  2026-08-14** — database and key ring back up to separate Wasabi buckets under separate keys, the
  key ring GPG-encrypted on top; both halves restore-tested, and the pair verified live with
  `--verify-keyring`.
  **[#254](https://github.com/MikeWills/VeSessionManager/issues/254) (deploy account's `rsync *`
  sudoers grant) closed 2026-08-27** — the code side had already shipped in #337 (deploy tree owned
  by the `deploy` user via group membership, no `sudo rsync` at all; sudoers narrowed to an exact,
  no-wildcard allowlist), and `sudo cat /etc/sudoers.d/vesessionmanager-deploy` on the box confirmed
  the live server already matched it — `ops/harden-deploy-sudoers.sh`'s one-off remediation had
  evidently already been run. One caveat surfaced verifying it, out of this repo's scope: `sudo -l -U
  deploy` shows the same shared `deploy` account still holds a root-equivalent grant through
  **NCSScheduler**'s own sudoers file (`rsync *`, `cp .../ncsscheduler.db *`) — a compromised deploy
  key still owns the box in practice, just via a sibling app's rule now, not this one's.
- **[`docs/audit-2026-08-03-tasks.md`](docs/audit-2026-08-03-tasks.md) is still worth reading for its
  "Verified clean" section** — what the six-agent review checked and found sound (zero raw SQL, IDOR
  re-checks on every id-taking POST handler, CSRF correct, and a list of deliberate patterns not to
  "fix"). That record exists nowhere else, and its value is negative work: knowing what *not* to
  re-audit. Its open findings are issues now (15 remain; six were closed on 2026-08-10); **their line
  numbers are from commit `2898817` and several files have moved since — a starting point, not an
  address.** Treat the findings themselves the same way: **five were wrong when re-checked that day**,
  including one that would have deleted a live authorization check (`CanCreateTeam`). Verify before
  acting on any of them. These two
  pointers stay here rather than in the Change Log below, which rotates entries out to `CHANGELOG.md`
  and would eventually take the only reference with it.
- Build/test/run: `dotnet build`, `dotnet test`, `dotnet run --project src/VeSessionManager.Worker`, `dotnet run --project src/VeSessionManager.Web` (see README, and Known Constraints below, for the `DOTNET_ENVIRONMENT` gotcha). Tests are xUnit in **three** projects, and which one a new test belongs in follows from what it can observe: `tests/VeSessionManager.Core.Tests` (services, mostly EF InMemory + fake clients — follow `SessionIngestionServiceTests`/`SessionEventSchedulingServiceTests`/`PaymentGenerationServiceTests`/`CandidateNotificationServiceTests`/`UlsWatcherServiceTests`/`PaymentReminderServiceTests`), `tests/VeSessionManager.Web.Tests` (page rendering via `WebApplicationFactory`, plus source scans over Razor — `PageSmokeTests`, `FormBindingTests`), and `tests/VeSessionManager.Worker.Tests` (each job's `RunTickAsync` driven directly against real SQLite via `WorkerTickHarness` — see `docs/worker-job-tests.md`). **InMemory is the default, not the rule**: transactions, `ExecuteUpdateAsync`, SQL null semantics and unique-index behaviour are all unobservable on it, and every test that turns on one of those uses `DataSource=:memory:` SQLite instead (`VecExamToolsCodeSqliteTests`, `AtomicCreateSqliteTests`, the whole Worker project).

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
  **Messaging is the one deliberate exception (2026-08-25):** a person's inbox, not a resource that
  has to exist, so a `MessageRule` does *not* backfill the moment email gets configured or a rule is
  re-enabled — see `MessageRuleEligibility.FloorUtc` and `docs/trigger-points.md`'s own section on it.
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
  - `Usd.Format`/`.Raw`/`.TryParse` (`Core/Usd.cs`, 2026-08-11) — the one place money becomes a
    string and back. **Never `"C"`** (invariant culture renders `¤`, not `$`) and never a bare
    `:F2`/`decimal.TryParse`, both of which use the *ambient* culture: `$12,50` out, and `"12.50"`
    parsed as **1250** back in. Named `Usd`, not `Money`, because the Square SDK owns `Money` and a
    `Core`-root type of that name shadows it across every `Core.*` namespace.
  - `UlsSchedule.ToEasternDate(utc)` (2026-08-11) — the calendar date a UTC instant falls on *in
    Eastern time*, and the only correct left-hand side when comparing one of this app's timestamps
    against an FCC date. See the Known Constraint below; `.Date` is wrong for ~80% of sessions.
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

- **A Discord channel is picked from a dropdown now, not typed by hand (#503, 2026-08-29).** See
  `docs/trigger-points.md`'s new section. New `IDiscordChannelMessageClient.ListTextChannelsAsync`
  backs a `<select>` on both `MessageRuleNew`/`MessageRuleEdit`; falls back to the old manual-id input
  whenever the list comes back empty — no `DiscordGuildId`, bot unconfigured, or the guild lookup
  failing all collapse to the same fallback rather than erroring the page. Fetched on `OnGetAsync`
  only, not from the shared `LoadAsync` both verbs call — a failed POST already redirects to a fresh
  GET, so fetching it there too would be a wasted Discord round trip on every validation failure.

- **A calendar invite, and a per-session VE summary email (#491, 2026-08-28).** See
  `docs/trigger-points.md`'s two new sections. Per-rule opt-in (`MessageRule.IncludeCalendarInvite`),
  not per-team — Mike: "The toggle I want is per email related to a session, not per team."
  `IcsInviteBuilder` shipped in #502 and sat unwired for three reasons its own doc comment named; all
  three landed here: `MessageSessionContext` gained `DurationMinutes`/`ZoomJoinUrl`,
  `BeforeSessionStartScanner` now populates `Session` the same way `CandidateRegisteredScanner`
  already did, and `EmailMessage` gained a real MimeKit `Attachment` (`IcsAttachment`), distinct from
  `InlineLogo`'s `LinkedResource`. A new `MessageTriggerDefinition.CarriesSessionContext` flag gates
  which triggers may turn the checkbox on — true only for `CandidateRegistered`/`BeforeSessionStart`
  today. **Same day, asked directly: `MessageFanOut.PerSession` (Discord-only until now) now works on
  email too** — a VE addressed as `SessionLead` used to get one email per candidate registered, with
  no candidate-count token available outside Discord's digest; `DispatchEmailPerSessionAsync` groups by
  session and renders the same `{{Count}}`/`{{SessionTitle}}`/`{{RegisteredCount}}` tokens Discord's
  `PerSession` posts already use. Refused only when addressed to `Candidate` — the one recipient a
  batched message has no single address for (`SingleDigest`, a batch spanning *every* session, stays
  Discord-only for the same underlying reason). A true VEC-the-organization notification is still a
  different, unbuilt thing — see the doc's "Still to come."

- **TeamLead can run the two day-of session actions (2026-08-27).** See `docs/admin-auth.md`'s
  "second exception" section. Mike, during role-access testing: *"These are a key part of running a
  test session. The SM might not be available to do that for them."*
  `SessionAccessScope.CanRunDayOfActions` grants exactly "Refresh candidates" and "Create retest
  payment" on Session Detail to every role that can view the session; every other write stays behind
  `CanEdit`, which remains false for TeamLead. Worth knowing: Refresh is not a pure read — it mints
  payment links and sends registration confirmations for anyone new, which is the point (a walk-in
  needs exactly those).

- **A bulk-email screen off Applicant Status (2026-08-26).** See `docs/candidate-email.md`'s new
  section. Same mechanism as #144's session-scoped compose screen — pick candidates, start from a
  template, edit, send — reached instead from Applicant Status, over every candidate on one team still
  waiting on an FCC grant. Built as the other half of the FCC-issue switches above: reminders
  suppressed, and a human who still wants to tell some or all of those people what's going on.
  **Requires one specific team, not "All teams"** — a message needs some team's own SMTP credentials,
  so the button only appears once a specific team is picked. The three predicates answering "who's
  pending" (Applicant Status's own list, its per-team nav badge, and this screen's recipient pool) are
  now one shared `CandidateApplicationStatusExtensions.AwaitingFccGrant`, replacing three copies that
  had already started drifting apart in comment-only form.

- **A manual switch for a real FCC-wide processing stall (2026-08-26).** See `docs/trigger-points.md`'s
  "FCC-wide-issue suppression" section. Mike, watching a live incident: the FCC's payment-verification
  subsystem stalling for new-license candidates while upgrade grants kept flowing — a distinction
  `FccFeeOutstandingScanner` had no way to represent. `Admin/FccStatus` (`RoleGroups.Admins`, so
  TeamAdmin as well as SystemAdmin) sets a master switch plus one sub-switch per candidate population;
  checking the master auto-checks all three in the UI. **Only two of the three do anything** —
  Renewal is stored and shown but never read, since this app has no renewal-candidate concept at all;
  it exists only so the control is already there the day that might change. **Suppressed is terminal,
  never a silent exclude** — `MessageDispatchService.SuppressByFccIssueAsync` marks it the same way a
  muted team's Zoom/Discord/Email already is, so flipping the switch back off never sends a backlog of
  everything that was held back, the same failure `MessageRuleEligibility.FloorUtc` already prevents
  for a different kind of "off." The four checkboxes render as real on/off switches now (`.switch` in
  `app.css`, generic enough for any future boolean setting to reuse) — a plain checkbox reads as "pick
  this option," and a state like "the FCC has a known issue" isn't a choice among several. A separate,
  general-purpose **System Banner** (`SystemSettings.SystemBannerEnabled`/`SystemBannerMessage`,
  `Admin/SystemSettings`, SystemAdmin-only) shipped alongside it — site-wide, free-text, not tied to
  the FCC switches at all; an operator types the message and turns it on/off by hand for anything
  worth telling every signed-in user, an FCC outage being only the first reason to.

- **A picked team now carries across every team-filtered page (2026-08-26).** Mike: a TeamAdmin/
  SessionManager/TeamLead can sit on several teams too, not just SystemAdmin, so this isn't a niche
  case. New `SharedTeamFilterCookie` (design rationale in its own doc comment and
  `RememberFiltersPageFilter`'s) is a single cross-page value layered on top of the existing #459
  per-page filter memory — every `[RemembersFilters]` page with a `TeamId` property reads/writes it,
  and the sessions list (which predates that mechanism and keeps its own bespoke cookie) was wired in
  separately for just the Team field. Stored as `"0"` rather than an empty string for "All teams" —
  an empty cookie value didn't reliably round-trip in testing. Privacy page's cookie count moved from
  four to five.

- **The product's display name is "VE Ops" (2026-08-25).** Nav brand, page `<title>`, footer,
  login page, the 2FA issuer string, and the default email `FromDisplayName`/subject lines
  (password reset, VE self-service/email-change) all changed from "VE Session Manager"/
  "VESESSIONMGR" to "VE Ops". **The repo, namespaces, classes, and every doc path stay
  `VeSessionManager`** — this was a display-string-only rename, not a rebrand of the codebase.

- **Refunds and payments show, tie to a candidate, and report (2026-08-26).** Issue #431 (live
  verification) and a same-day follow-on ask. See `docs/transactions-report.md`. Live-verified end to
  end on WX0MIK: a real Square payment taken and refunded from inside the app. **Settlement to
  `Completed` is now confirmed for both a full and a split-partial refund (2026-08-29)** — the
  partial-refund pair ($2 + $3 against one $5 charge, submitted 2026-08-27) was checked off on #384 as
  *submitted* but its settlement was inferred rather than observed at the time; the real bank
  statement showing both `PURCHASE RETURN` lines posted is that missing observation.
  **#384/#431 stay open** — #384 still has "Refund and dismiss" on Unmatched Payments and the
  pre-#383-payment fallback note unchecked; #431 is the broader end-to-end pass (ExamTools
  registration → close-session, youth-rate registration, the ARRL preview against real money) and most
  of its steps haven't run yet. Two things worth carrying forward from the report itself:
  **`Payment.CandidateNameSnapshot`** exists because `Candidate.Name` does not survive PII purge and a
  withdrawal (the report's reason to exist) is exactly what triggers that purge — captured once at
  Payment creation, same snapshot-on-the-record pattern as `MessageRuleRun`, so the Transactions
  Report has a name to show without reopening PII purge policy; and **only a `Completed` refund
  subtracts from the report's totals** — `Pending`/`Rejected`/`Failed` still show as rows (a record of
  what was attempted) but contribute zero, since counting them would understate money the team
  actually has. ⚠️ Flagged the same day, not yet built: a Square payment still `APPROVED` (not yet
  `COMPLETED`) can't be refunded at all — it needs `CancelPayment` (void) instead, which
  `RefundService` has no branch for today.

**Kept here vs. `CHANGELOG.md`:** this section is a bounded, recent-only window (rule of thumb: cap
around 10 entries), since CLAUDE.md is read in full on every conversation turn and this is the one
section that would otherwise grow forever. Phase-numbered work (Phase 0-10) is never listed here at
all — it's already one-line-summarized in "Current State" above, so a separate Change Log pointer
would be pure duplication — and goes straight to `CHANGELOG.md` instead. Non-phase entries (fixes,
redesigns, hardening passes) start here and move to `CHANGELOG.md` once the section is at/over the
cap and a newer entry needs to be added; oldest goes first.

- **No backlog on enable, for messaging specifically (2026-08-25).** See `docs/trigger-points.md`'s
  "MessageRuleEligibility" section. Every scanner used to bound eligibility by `MessageRule.CreatedUtc`
  alone — right for "a new rule shouldn't fire for people already past the moment," but it missed two
  other off-to-on transitions: a rule switched back on, and a team's email configured for the first
  time. Mike, after a beta candidate got a registration confirmation the moment SMTP was turned on:
  *"it's not supposed to send any backlog of email"* / *"if a message is off then I turn on, it's not
  supposed to send backlog either."* `MessageRuleEligibility.FloorUtc` now folds in
  `MessageRule.EnabledSinceUtc` (stamped only on an actual disabled→enabled transition) and, for an
  email rule, `Team.EmailConfiguredUtc` (stamped only on an actual unconfigured→configured transition)
  — both null-by-default and never backfilled, so an already-running team or already-enabled rule sees
  no behavior change. **Deliberately narrower than "no backlog" everywhere**: Zoom/Discord/Square still
  backfill the moment they're configured, and that stays right — those create a resource that has to
  exist regardless of when config caught up, where messaging is telling a person about something old
  the moment notifications get switched on.

- **A team can be deactivated, or deleted outright (2026-08-21).** See `docs/team-lifecycle.md`.
  Deactivate stops the app polling and sending and is one click back; delete removes the team and
  everything it owns and cannot be undone. Five things worth carrying forward. **Order is the whole
  difficulty** — thirteen of a team's child tables are `Restrict`, so each goes explicitly, leaves
  first, and `TeamDeletionCoverageTests` reads the EF model to fail when a new table with a Team
  foreign key is added and nobody teaches the service about it (a hand-written list cannot notice the
  seventeenth table). ⚠️ **The deletion's own audit entry carries no `TeamId`** — attributed to the
  team it describes, it would be caught by the sweep clearing that team's audit rows and delete
  itself; only *attributed* rows are identifiable at all, since `AuditLog.TeamId` is populated on
  job writes and left null on user-attributed ones. **Files go before rows**, because a file left
  after the row naming it is unreachable forever while the reverse is just a retry — and file by
  file, never by removing the team's directory, which is keyed on a free-text team code two teams
  could share. **A VE is a person, not team property**: deleted only if this was their sole
  membership *and* no user account links them, nothing was merged into them, and they are not on
  another team's session roster — that last one fails as a foreign key violation rather than as
  anything legible. And **the typed team name is the guard rather than a second "are you sure"**,
  checked server-side because a modal is not a permission: the mistake this invites is deleting the
  right-looking row of the wrong team, which a confirm dialog does not catch.

Everything through Phase 0-10's initial build (ExamTools ingestion, Zoom/Discord, Square, email
notifications, FCC ULS watcher, payment reminders, VE tracking, VEC submission tracker, admin
auth/config/candidate-actions, PII purge), the public privacy page, and everything older than the
entries above has aged out to **`CHANGELOG.md`** — same one-line-pointer format, just the overflow.

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
- **All changes land on `main` via a PR — no direct pushes — and this is server-enforced now (2026-08-13), not convention.** Branch protection on `main`: PR required, **0 required approvals** (a solo maintainer still merges their own work), `build-and-test` required green, branches must be up to date, force pushes and deletions blocked. **Admins are deliberately not included**, so a direct push remains possible in a genuine emergency — it just is not the normal path. A push to `main` is now rejected rather than tut-tutted; branch first. *(Prior history, since it explains an obsolete note elsewhere: this was impossible while the repo was private on a free plan, where the API 403s "Upgrade to GitHub Pro or make this repository public". Going public made it available.)* See `CONTRIBUTING.md`.
- **Public repo.** Assume anything committed is world-readable and that strangers may clone and self-host. MIT licensed (`LICENSE`, 2026-08-13); vulnerability reporting is `SECURITY.md`.

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

Steps are in [`docs/runbooks/roll-back-a-release.md`](docs/runbooks/roll-back-a-release.md); what's here is
only what shapes decisions elsewhere.

- **Versioning**: semantic version tags (`v1.2.0`). A pushed tag is the *only* deploy trigger — an
  ordinary commit to `main` builds and tests, and ships nothing.
- **There is no previous build to swap back to, and no symlink/`releases/` scheme.** `deploy.yml`
  `rsync --delete`s straight over `/opt/vesessionmanager/{worker,web}/`, so the prior build is gone
  the moment the sync runs. **Rolling back means deploying an earlier tag** — tag the last good
  commit and push. *(This bullet used to describe keeping the previous deployment folder for a
  symlink/service-restart swap. That was template text, never true of this deployment, and it
  contradicted the workflow for months.)*
- **Rollback safety comes from the pre-deploy snapshots, not from retained builds.** Each deploy
  takes a database snapshot (`sqlite3 .backup` + `PRAGMA integrity_check`) and a key-ring snapshot,
  newest **5** kept, retentions deliberately equal because the pair is taken and restored together.
  ⚠️ Both live on the same disk as the thing they protect — **rollback points, not backups**. The
  backup that survives losing the box is the separate off-box job (#256, BackupScripts).
- **Database changes**: code rollback alone will not undo a schema migration. Any migration needs a
  rollback path, and on this deployment that path is the snapshot pair — **key ring first, then the
  database**, since a database restored against a missing or wrong key ring refuses to start.
  Everything written since that snapshot is lost, so weigh a forward fix against the data loss.
- **Rollback authority**: the maintainer's call (solo maintainer; branch protection requires 0
  approvals). Record which tag was rolled back and why in the PR or issue that carried the bad
  change — there is no other place a rollback is logged.

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
- **Worker Service reads `DOTNET_ENVIRONMENT`, not `ASPNETCORE_ENVIRONMENT`.** `VeSessionManager.Worker` is a plain generic Host (`Host.CreateApplicationBuilder`), which only honors `DOTNET_ENVIRONMENT`. Only the Web project (`WebApplication.CreateBuilder`) reads `ASPNETCORE_ENVIRONMENT` (and falls back to `DOTNET_ENVIRONMENT`). The generic Host's own default when neither is set is `Production` — so running the Worker's built DLL directly (bypassing `launchSettings.json`, which sets `DOTNET_ENVIRONMENT=Development` for `dotnet run`) silently picks up `appsettings.Production.json`'s Linux-only paths and fails on a dev machine. Always use `dotnet run --project ...` locally for the Worker, not the raw `.dll`. **Second half of the same trap, found on the server 2026-08-13: the content root is the *current directory*, not the DLL's directory** — so running the published DLL from anywhere but the app folder finds **no `appsettings` file at all**, leaving the connection string null; SQLite then opens an anonymous temporary database and the first symptom is `no such table: Teams`, which reads as a damaged database when the real one was never opened. The systemd units set `WorkingDirectory` for this reason. Any by-hand invocation on the box needs `sh -c 'cd /opt/vesessionmanager/worker && exec dotnet ./VeSessionManager.Worker.dll <switch>'`.
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
- **A literal NUL byte (`U+0000`) in a source file makes that file invisible to both ripgrep and
  git.** Neither errors: `rg` classifies the file as binary and reports *no matches*, and `git diff`
  renders it as `Bin 5207 -> 6768 bytes`. Found 2026-08-11 (issue #300) in two files, where it was
  the "untagged" filter sentinel. **Both kinds of invisibility cost something real**: a code review
  searched for callers of `VeSessionInvitationService`, found none because its only caller lives in
  one of those files, and recommended deleting its DI registration — which would have crashed the
  VE-invite page. And an HTML parser rewrites `U+0000` to `U+FFFD` (raw *or* as `&#x0;`), so the
  sentinel never round-tripped and the "Untagged" filter hid every VE instead of showing them.
  `NoNulBytesInSourceTests` now fails the build if one reappears anywhere under `src/`. Use a
  printable sentinel — a **leading space** is the established one here (tag names are `Trim()`ed and
  rejected when blank, so no stored tag can collide), already used by
  `VolunteerExaminerDirectoryService.GuestTagFilter`.
- **`Session.ScheduledStartUtc.Date` is a UTC calendar date, and comparing it against an FCC date is
  wrong for the *majority* of this deployment's sessions.** Every FCC date arrives date-only and is
  stamped at UTC midnight by `ExamToolsUlsLookupClient.AsUtcDate`, so it already *is* a wall-clock
  date; the session side is a real instant, and `.Date` on it answers "what day is it in London".
  **697 of 867 stored sessions start between 23:00 and 04:00 UTC** — evening ET is simply when
  volunteer-run sessions happen — so for most of them `.Date` is *tomorrow*. `UlsWatcherService` did
  this in three places (issue #248, fixed 2026-08-11): an evening session's candidates could never
  match an application FCC received that same evening, stayed `Unmatched` permanently, and therefore
  never reached `FccPaymentStatus = PendingVerification`, which is what the FCC-fee reminder keys
  off. Use `UlsSchedule.ToEasternDate(...)`. Note the warning was already in `UlsSchedule`'s own doc
  comment, in the file the buggy code imported — a comment is not a guardrail.
- **`ChangeTracker.Clear()` is almost never the right way to recover from one bad row.** It detaches
  *everything*, including the rest of the batch the loop is still working through — so later
  iterations mutate detached objects, `SaveChangesAsync` writes nothing, and the counters still
  increment. The signature is a run that reports success and changed nothing. Found at four sites on
  2026-08-11 (issues #231, #232, #233, #234), one of which had been silently disabling a team's
  ingestion throttle in production. **Detach the failing entity and its pending children instead**
  (`dbContext.Entry(x).State = EntityState.Detached`), or bypass the tracker entirely with
  `ExecuteUpdateAsync` where the write is a simple stamp. `JobRunHistoryLogger`'s own clear is the
  deliberate exception — it is abandoning the whole unit of work — which is exactly why anything
  sharing its scoped `DbContext` must not assume its entities survive a failed step.
- **A job tick timed for "the evening" in US Eastern can land at/after UTC midnight** — EDT is UTC-4, EST is UTC-5, so anything from ~8pm ET onward is already tomorrow in raw UTC. `TimeProvider.GetUtcNow().UtcDateTime.DayOfWeek` (or any UTC-based "what day is it" check) is wrong for that window; convert through `TimeZoneInfo.ConvertTimeFromUtc(..., FccUlsSchedule.EasternTimeZone)` first (IANA id `"America/New_York"`, resolves cross-platform since .NET 6 — verified directly on this repo's target framework on both Windows and the Linux deploy target). Found live 2026-07-23 building `FccDailyWatcherJob`'s same-day retry; see `docs/fcc-uls-watcher.md`. Reuse `FccUlsSchedule.EasternTimeZone` for any future US-Eastern-anchored scheduling rather than re-resolving the id.
- **Not every job here can safely reuse the "24h `PeriodicTimer` from Worker start, extra ticks are free" idiom** — that reasoning (used by `DayBeforeReminderJob`/`PaymentReminderJob`/`PiiPurgeJob`/`FccWeeklyCatchupJob`) assumes a missed tick is harmless because idempotent tracking catches it up next time. It breaks when the *data itself* — not just the job's own state — is only available in a narrow, non-retryable window, as with FCC's day-name files (see the same-day-retry entry above). Before adding a new job on this idiom, check whether the thing it polls has that same "one-shot window" property.
- **A Square refund that returns successfully has not happened yet.** `RefundPaymentAsync` answers
  immediately with a status, and for a card or bank transfer that status is `PENDING` for anything up
  to **14 days** before reaching `COMPLETED` — or `REJECTED`/`FAILED`. Every other outbound Square
  call in this app is done when it returns, so "the call succeeded, therefore the money went back" is
  the natural and wrong reading. Anything new that reports on a refund must read `Refund.Status`, not
  the fact that a call was made. Two hard limits worth knowing before writing a guard: Square refuses
  a payment **more than a year old**, and allows at most **20 refunds** against one payment. See
  `docs/square-refunds.md`.
- **Square webhook subscriptions are separate per Sandbox/Production, each with its own signature key** — an existing subscription registered under one mode receives zero delivery attempts for events in the other (not a 401, no attempt at all), and reusing one mode's `WebhookSignatureKey` against the other mode's subscription makes every delivery fail signature verification (401) even though the URL/event config is otherwise correct. Found live 2026-07-25 testing Team 2 (MARC)'s payment flow — the "Ve Session Manager" subscription had been created under Production while all local testing used Sandbox credentials/payment links. Fix: add (or move) the subscription under the correct mode's tab in the Square dashboard, then set `Team.SquareWebhookSignatureKey` to *that* subscription's own signature key, not the other mode's. See `docs/square-payments.md`. **Which mode a team is in is now `Team.SquareEnvironment`, not a config value (2026-08-06)** — so this is per-team, and two teams on one deployment can legitimately be in different modes, each needing its own subscription. A team whose access token and environment disagree gets an auth failure from Square rather than a wrong-account charge.
- **`Web` and `Worker` must register Data Protection with the exact same application name and key-ring path, or one process's writes silently become unreadable by the other.** `Team`'s credential columns (ExamTools/Zoom/Square/SMTP secrets) are encrypted at rest via `EncryptedStringConverter` (2026-07-30) — both `Program.cs` files call `AddDataProtection().SetApplicationName("VeSessionManager").PersistKeysToFileSystem(...)` with the same hardcoded app name and the same `DataProtection:KeyRingPath` config value. A drift here doesn't throw — `EncryptedStringConverter`'s legacy-plaintext fallback (needed for the migration path) means a value encrypted under a different key just looks like it was never migrated. See `docs/credential-encryption.md`. Also: **if the key-ring directory is ever lost, every encrypted credential becomes permanently unrecoverable** — it must be backed up with the same discipline as the DB file itself (see `docs/deployment.md`).
- **`PaymentEligibilityWindow` (30 days from session start) silently caps two values a team can now set past it.** It bounds `FccFeeOutstanding` and `PaymentUnpaid` — the historical-import guard, and rightly so — but since #401 PR2 those triggers' hours are per-team with a one-year ceiling on the form. A rule set beyond the window never fires, and nothing fails: no send, no error, no marker. The Message Rules form shows a caution rather than refusing, because the real headroom is 30 days *minus* however long the FCC took to enter the application, which nobody knows in advance. **Changing the window now changes what a team's configuration is allowed to mean**, not just what the code does.
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
- **An ASP.NET Core `FallbackPolicy` applies to minimal-API endpoints, not just Razor Pages.** Added
  2026-08-10 (#158); the Square webhook has no authorization metadata of its own, so it inherited the
  policy and **every delivery would have been refused** until `.AllowAnonymous()` was added
  explicitly. The issue that requested the policy asserted the webhook was "unaffected" — it was not.
  The failure mode is the dangerous part: Square retries, gives up, and payments stop being recorded
  with **nothing logged on this side**, so the first symptom is a candidate insisting they paid.
  Anything mapped outside Razor Pages needs the same consideration. Related trap when testing it:
  the handler answers a missing signature with **401**, the same status authorization produces, so a
  status-code probe cannot tell "exempt" from "not exempt" — assert on endpoint metadata instead
  (`PageSmokeTests`).
- **A wrong or missing Data Protection key ring is indistinguishable from un-migrated data, and
  always will be** — `EncryptedStringConverter`'s read path returns the raw stored value when
  `Unprotect` throws, which is exactly what makes the legacy-plaintext migration safe. Nothing
  throws, nothing logs, and every integration quietly authenticates with a base64 blob.
  `DataProtectionKeyRingGuard` (2026-08-10) is the backstop: it refuses to start when a credential
  still looks like ciphertext *after* being read through the converter. **It runs before the Worker's
  one-off `--` switches on purpose** — a `--migrate-team-secrets` run against the wrong key ring
  would rewrite every credential with the undecryptable value it just read, destroying the originals.
  Never "fix" a key-ring problem by re-entering credentials in Team Settings; that overwrites the
  originals under the new key permanently.
- **Never format a candidate-facing time with `EasternTimeFormatter` — it lives in the Web project
  and is unreachable from Core, which is how candidate email spent months rendering UTC while every
  screen rendered ET (#205).** Use `SessionTimeFormatter.ForCandidate` (Core), which gives
  `10:00 AM ET / 7:00 AM PT`. The two-zone form is deliberate: sessions are remote, the gap between
  those zones is always exactly three hours, and it lets Central/Mountain readers interpolate.
  **The wider lesson is the test shape, not the timezone**: the notification tests used
  `{{SessionDate}}` in template subjects and never asserted what it rendered to, so a full green
  suite coexisted with every candidate email being wrong. A placeholder that is never asserted on is
  not covered.
- **GitHub only honours the first issue in a comma-separated `Closes` list.** `Closes #1, #2, #3`
  closes #1 and silently leaves the rest open (confirmed 2026-08-10 across two PRs). Repeat the
  keyword per issue.
- **A "manual/on-demand" variant that shares its filter with the scheduled scan inherits every
  bound you add to that scan, and the doc comment promising otherwise will not fail.**
  `ExamResultSyncService.SyncSessionAsync` is documented as the unbounded escape hatch for a session
  graded later than the routine sweep reaches. Its promise has been **false twice**: once because the
  manual refresh actually called `RunAsync` and inherited `ResultSyncWindow` (fixed 2026-08-03), and
  again because the settled gate added with #437 was inherited too, so a license class frozen too low
  could not be repaired by pressing Refresh — the bound excluded exactly the candidates needing
  repair, since a frozen class is normally noticed *after* the session is finalized (fixed
  2026-08-20). Both times prose and code disagreed and nothing failed. Which candidates get read now
  lives in **`ExamResultCandidateFilter`**, split into two named buckets — `CanBeReadFromTheFeed`
  (what the feed is *allowed* to speak for; binds both paths) and `IsSettledForNow` (cost only;
  scheduled scan only) — so a new rule has to be put on one side or the other, and
  `ExamResultEscapeHatchTests` fails if it lands on the wrong one. **Do not add a clause to the
  `Where` in `SyncSessionCandidatesAsync`.** The same shape is worth watching anywhere else a
  scheduled job and a user-triggered button share one query.
- **Any new code path that calls `Database.Migrate()` must go through `MigrationLock.Run`
  (#443, 2026-08-20).** Web, Worker and `--create-admin` all migrate at startup, and until this
  landed nothing in the code kept them apart — the only protection was `deploy.yml` starting Worker,
  asserting it active, then Web. **That is workflow sequencing, and it never covered a reboot**
  (systemd starts both units together, no `After=` between them), a server attached to no pipeline
  (HRCC), a self-hoster using the documented publish-and-copy install, or a crash-restart (both units
  are `Restart=always`). The failure is not clean: a transient `database is locked` from the migrate
  call escapes **outside** `JobTick.GuardedAsync`, so `StopHost` takes the whole Worker down. The
  lock is an exclusive file beside the **database** — never in the app directory, which the service
  account cannot write and which `rsync --delete` replaces mid-deploy — and it no-ops for
  `:memory:`/`Mode=Memory`, which is what the entire test suite runs on.
- **"No fee, no test" — this app's own exam fee can never legitimately sit unpaid after the fact, so
  don't build (or re-build) a "the exam fee has been unpaid for N days" concept.** Mike, 2026-08-25,
  after two rounds of me getting this wrong: *"the only '10 day rule' is the lifetime of the
  application at the FCC. If the application fee isn't paid to the FCC, then the application expires.
  Any fees related to a test must be collected prior to the test. No fee, no test."* There is exactly
  **one** 10-day rule in this domain, and it belongs to the FCC's own CORES fee
  (`Candidate.FccPaymentStatus`, the `FccFeeOutstanding` trigger) — never to `Payment` (this team's
  own Square exam/retest fee). An "exam fee unpaid" condition anchored on a *later* FCC-side date
  (application entered, result marked) reads as plausible and is not: payment gates testing, so that
  combination can't arise for a real candidate. **This is a human process rule, not a system
  invariant** — the VE running the session enforces it at the door, and nothing in the code checks or
  could check it (Mike: "this cannot be enforced by code"). The point isn't that the app guarantees
  the combination is impossible; it's that the app must not build a feature whose premise assumes it
  happens anyway. This was tried **twice** — the original Phase 6 `PaymentExpirationNotice`/
  `PaymentUnpaid` trigger, and its retest branch, which I defended as "the one real case" until asked
  to check: **zero retest payments exist in this deployment's history, ever.** Both were removed
  2026-08-25 (`Payment.ExpiredUnpaid` and the write that set it) rather than kept as inert bookkeeping.
  If a future request sounds like "notify/flag when our own fee has been unpaid for a while," it is
  almost certainly describing the FCC's fee and should be built against `FccFeeOutstanding`, not a new
  `Payment`-side trigger.
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
| `/docs/runbooks` folder | The "toolbox by the door" | Operational procedures read *while doing or fixing* something — deploy, roll back, restore, and symptom-first diagnostics. Steps and warnings only; the reasoning stays in the design doc beside them, which each runbook links. Index at [`docs/runbooks/README.md`](docs/runbooks/README.md) |

- **README is written for a stranger who found the repo, not for Mike** (2026-08-13, when the repo was found to be public). It covers what the app is, what it needs, and how to stand one up on your own server; per-credential detail moved to `docs/configuration.md`, and the tag-triggered Actions workflow is called out as specific to one box rather than presented as *the* way to deploy. `ARCHITECTURE.md` and `SECURITY.md` now exist — the table above described them for months while neither did.
- Use a GitHub Wiki or GitHub Pages only if documentation needs to be browsable outside the repo (e.g. for external stakeholders) — not needed for internal City projects by default
- Ownership, contacts, and escalation info belong in the README, not in this file
- **CLAUDE.md is read in full on every conversation turn, so its size is a permanent, compounding cost — write new content directly into `/docs` and leave only a pointer in CLAUDE.md's Change Log, and only for as long as that entry stays within the Change Log's own recent-only cap before moving to `CHANGELOG.md`.** Three kinds of content earn a permanent home in CLAUDE.md itself, dense prose and all: (1) standing rules/conventions that shape every future decision (the sections below this one), (2) Established Patterns — cross-cutting conventions, not tied to one phase, (3) Known Constraints — short, sharp "this will silently break if you don't know X" gotchas. Everything else — the narrative of what was built, why, and what was learned building it — belongs in `/docs`, written there at the time, not accumulated here and split out later once the file gets unwieldy. A completed phase's Change Log pointer never even starts in CLAUDE.md at all if it's already summarized in "Current State" — straight to `CHANGELOG.md`.

## Instructions for Claude

- Do not guess at facts, APIs, or library behavior — verify, and cite sources/docs when possible
- **Leave every issue you touch in a truer state than you found it — update it or close it.** Issues
  here are mostly audit findings, and they rot: line numbers drift, files move, and the finding
  itself is sometimes wrong (five were on 2026-08-11; one would have deleted a live authorization
  check). So verify before acting, and then **write down what you found**. If a bullet is stale,
  strike it. If the whole issue is already done, close it with the evidence — #161 sat open with a
  throttle that had existed since PR #77, a deliberate design decision recorded as an oversight, and
  a WAL bullet that was factually wrong. If counts have drifted, say so in the PR rather than
  silently fixing a different number than the issue claims.
  **Do not open a successor issue for a risk nobody has observed whose fix you have just argued
  against** — that is how the backlog fills with items no one will ever action and everyone will
  re-verify. Reasoning that constrains code belongs *in* that code, next to the thing it constrains,
  where it cannot go stale independently of it (see `TeamRefreshThrottle`'s remarks for the worked
  example). A new issue earns its place when someone could actually pick it up and finish it.
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
