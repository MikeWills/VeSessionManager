# CLAUDE.md

This is a Visual Studio project that is designed to automate many of the mundane tasks that a Amateur Radio Volunteer Examiner (VE) Session Manager (SM) needs to do to run a session include creating a Zoom session, sending payment links and reminder emails. See docs/spec.md for details.

## Current State

- Phase 0 (foundation), Phase 1 (ExamTools session/candidate ingestion), Phase 2 (Zoom + Discord event scheduling), Phase 3 (Square payment links + webhook), Phase 4 (candidate notification emails + templates), Phase 5 (FCC ULS application/license watcher), and Phase 6 (payment reminder & expiration job) of `docs/spec.md` are implemented. Next up: Phase 7 (VE tracking).
- Build/test/run: `dotnet build`, `dotnet test`, `dotnet run --project src/VeSessionManager.Worker`, `dotnet run --project src/VeSessionManager.Web` (see README for the `DOTNET_ENVIRONMENT` gotcha). Tests are xUnit in `tests/VeSessionManager.Core.Tests`, using the EF InMemory provider and fake client implementations — follow `SessionIngestionServiceTests`/`SessionEventSchedulingServiceTests`/`PaymentGenerationServiceTests`/`CandidateNotificationServiceTests`/`FccUlsWatcherServiceTests`/`PaymentReminderServiceTests` as the pattern.
- **Optional-integration pattern, established across Phases 2-4 — follow it for every future external API client:** ExamTools is the one hard requirement (fails loudly, since ingestion is what everything else depends on); Zoom, Discord, Square, and Email/SMTP are all optional. Each client exposes `bool IsConfigured` on its interface; the consuming service checks it *before* attempting the call, skips quietly with one aggregate `INFO` log line (never a repeating `ERROR`) when unconfigured, and leaves whatever `...SentUtc`/`...Id`/`PaymentLinkUrl`-style tracking field null so the very next poll retries automatically — no separate "backfill" step needed once credentials are added. Never validate credentials in a client's constructor (see the BackgroundService gotcha below) — always in the method that needs them, or in a lazily-evaluated `IsConfigured` getter.
- ExamTools API access: cookie login + endpoint shapes documented in `docs/examtools-api.md`; runnable requests in `api-examples/` (Bruno). Credentials come from user-secrets (`ExamTools:Username`/`ExamTools:Password`), never appsettings.
- Zoom (Server-to-Server OAuth) + Discord (bot, `Discord.Net.Rest`) scheduling: API shapes documented in `docs/zoom-discord-scheduling.md`. `SessionEventSchedulingService` is scan-based, not event-driven — it diffs `Session.ScheduledStartUtc` against `Session.ZoomDiscordSyncedStartUtc` each run rather than reacting to a one-shot "new session" signal. Both Zoom and Discord are optional and independent (`IZoomClient.IsConfigured`/`IDiscordEventClient.IsConfigured`) — Discord's event needs the Zoom join link for its description/location, so Discord can't actually run until Zoom has produced one, even if Discord itself is fully configured; `ZoomDiscordSyncedStartUtc` only advances once *both* `ZoomMeetingId` and `DiscordEventId` are set (deliberately not "or unconfigured" — that was a real bug caught by a test before it shipped, see the gotcha below). Credentials: `Zoom:AccountId`/`Zoom:ClientId`/`Zoom:ClientSecret`, `Discord:BotToken` (user-secrets); `Discord:GuildId` (non-secret, defaults to `0` = "not configured").
- Square payment links + webhook: API shapes documented in `docs/square-payments.md`. `PaymentGenerationService` is scan-based like Phase 2 (candidate has no Payment row -> create one; Unpaid payment has no link -> generate one). Webhook lives in `VeSessionManager.Web` (`POST /webhooks/square`), matches by Square's `order_id` (not `reference_id` — see the gotcha below), signature-verified via the official `Square` SDK's `WebhooksHelper.VerifySignature`. Credentials: `Square:AccessToken`, `Square:WebhookSignatureKey` (user-secrets, shared between Worker and Web via the same `UserSecretsId`); `Square:LocationId`/`Square:WebhookNotificationUrl` are non-secret but have no safe default (empty string) — set both before the payment flow will actually work.
- Candidate notification emails: API/setup documented in `docs/email-notifications.md`. `CandidateNotificationService` sends via `EmailTemplateRenderer` (simple `{{Placeholder}}` substitution over the `EmailTemplates` table) + `SmtpEmailSender` (MailKit, Mailgun by default). Template/settings content (`EmailTemplates`, singleton `EmailSettings` row) is seeded once with placeholder content on first run and is meant to be **hand-edited by a human directly in the DB** before real use — not generated, and never re-seeded over an edit. `Candidate.FirstName`/`RegistrationConfirmationSentUtc`/`DayBeforeReminderSentUtc` are new fields, not in the original shared data model (same "add + document the deviation" pattern as Phase 2's `Session.ZoomDiscordSyncedStartUtc`). Credentials: `Email:SmtpUsername`/`Email:SmtpPassword` (user-secrets, shared Worker/Web secrets store); `Email:SmtpHost`/`Port`/`UseStartTls` default to Mailgun's `smtp.mailgun.org:587`+STARTTLS.
- FCC ULS watcher: field layout, verified real-data positions, and jobs documented in `docs/fcc-uls-watcher.md`. `FccUlsClient` downloads/parses `data.fcc.gov`'s daily (`a_am_<day>.zip`/`l_am_<day>.zip`) and weekly-complete (`a_amat.zip`/`l_amat.zip`) files; `FccUlsRecordParser` is a pure HD/EN pipe-delimited join, directly unit-testable with fixture strings (no live download needed). `FccUlsWatcherService` is scan-based like every other phase: `Unmatched` -> `Received` on an application-file FRN match, `Unmatched`/`Received` -> `Granted` (short-circuits, license always wins) on a license-file FRN match **with HD License Status `A`** — not just any appearance, since a Canceled license can still show up in a same-day transaction file for an unrelated reason. Two jobs: `FccDailyWatcherJob` (24h tick, daily files) and `FccWeeklyCatchupJob` (24h tick, but only actually scans on `Jobs:FccWeeklyCatchupDayOfWeek`, default Monday, against the weekly-complete files — covers any day the daily job missed). No credentials, so unlike Zoom/Discord/Square/Email this is **not** an `IsConfigured`-gated optional integration — it always runs; the only "unconfigured"-shaped state is a 404 (file not published yet), which is a normal no-op, not an error. No schema changes needed — `Candidate.ApplicationStatus`/`Frn`/`CallSign`/`LicenseGrantDateUtc`/`ApplicationDateEnteredUtc` already existed in the shared data model from Phase 0.
- **`Team` and `Vec` are not the same thing — don't conflate them in any future multi-tenant design.** A `Vec` is an FCC-recognized coordinating organization (ARRL, W5YI, Laurel, etc.) that dictates a session's fee schedule — already correctly modeled today (`FeeConfiguration.VecId`, `Session.VecId` per-session, so **one deployment already supports one team working with multiple VECs** across different sessions). A "Team" is the group of VEs actually operating a deployment of this app — there is **no `Team` entity today**, because the whole app currently *is* implicitly exactly one team. **Multi-team direction flagged, not yet built** (raised 2026-07-20): the user asked whether the app could someday serve multiple *independent teams* in one deployment, each with their own Discord server/Square account/etc., while still sharing one FCC ULS download across all of them. The FCC watcher already supports this for free — its candidate queries span every session in one pass, no per-team concept needed there. Zoom/Discord/Square/Email do not: each is a **singleton bound to one global appsettings/user-secrets credential set**. Multi-team support for those would mean introducing an actual `Team` entity and moving those credentials onto it — reworking each client from "one cached token for the app's lifetime" to "resolve credentials per-Team per-call." Open design question, not yet resolved: whether `Vec` stays a shared/global table (the same real-world "ARRL" row usable by any team) with a `Team`↔`Vec` join, or becomes scoped per-`Team` — a real but bounded change either way, intentionally deferred as its own future design pass.
- Payment reminders/expiration: setup and design notes in `docs/payment-reminders.md`. `PaymentReminderService` is three independent scan-based passes in one daily run (`PaymentReminderJob`, 24h tick): a 5-day-old `Received`+`Unpaid` payment gets `PaymentReminder5Day` sent to the candidate; a 10-day-old `Unpaid` payment gets `Payment.ExpiredUnpaid = true` and `PaymentExpirationNotice` sent to **`EmailSettings.AdminNotificationEmail`** (the Session Manager, not the candidate); a `Candidate` still `Unmatched` more than `PaymentReminder:UnmatchedReviewWindowDays` (default 5, the one part of this phase the spec calls out as configurable — the 5-/10-day thresholds themselves are fixed, not config) past `DateRegisteredUtc` gets `Candidate.UnmatchedReviewFlaggedUtc` set and a `WARNING` log line (no admin UI yet to show it elsewhere). No new external client — reuses Phase 4's `EmailTemplateRenderer`/`IEmailSender`. New fields: `EmailSettings.AdminNotificationEmail`, `Candidate.UnmatchedReviewFlaggedUtc` (migration `Phase6PaymentReminders`); `Payment.ExpiredUnpaid`/`PaymentReminderSentUtc` already existed in the shared data model from Phase 0.

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

- The deploy server is behind a Tailscale VPN — a GitHub-hosted Actions runner can't reach it directly. The deploy workflow needs either a self-hosted runner joined to the tailnet, or a `tailscale/github-action` step to join the hosted runner to the tailnet before the deploy step.
- **Worker Service reads `DOTNET_ENVIRONMENT`, not `ASPNETCORE_ENVIRONMENT`.** `VeSessionManager.Worker` is a plain generic Host (`Host.CreateApplicationBuilder`), which only honors `DOTNET_ENVIRONMENT`. Only the Web project (`WebApplication.CreateBuilder`) reads `ASPNETCORE_ENVIRONMENT` (and falls back to `DOTNET_ENVIRONMENT`). The generic Host's own default when neither is set is `Production` — so running the Worker's built DLL directly (bypassing `launchSettings.json`, which sets `DOTNET_ENVIRONMENT=Development` for `dotnet run`) silently picks up `appsettings.Production.json`'s Linux-only paths and fails on a dev machine. Always use `dotnet run --project ...` locally for the Worker, not the raw `.dll`.
- **ExamTools login returns HTTP 200 on bad credentials** — failure is an `{"error": ...}` body, not a status code. Any code touching `POST /api/ve/login` must check the body (see `ExamToolsClient` and `docs/examtools-api.md`).
- **ExamTools has no "cancelled" session state** — cancellations are detected by a known session id disappearing from the team feed, reschedules by a changed `date` on the same id. Don't go looking for a status flag that isn't there.
- **Zoom Server-to-Server OAuth tokens have no refresh token** — they just expire after an hour; the only way to get a new one is to call `/oauth/token` again with the same `account_credentials` grant. `ZoomClient` caches and re-requests a minute before expiry rather than reacting to a 401.
- **`DateTimeOffset` construction from a Sqlite-round-tripped `DateTime` will throw if you're not careful** — EF Core/Sqlite returns `DateTimeKind.Unspecified`, and `new DateTimeOffset(dateTime, TimeSpan.Zero)` validates Kind against the offset. `DiscordEventClient.ToOffset()` forces `Kind = Utc` first; reuse that pattern anywhere else a stored `DateTime` needs to become a `DateTimeOffset`.
- **Never validate external-API credentials in a singleton's constructor if that singleton is resolved from inside a Worker `BackgroundService`.** A constructor throw there stops the *entire host* (.NET's default `BackgroundServiceExceptionBehavior` is `StopHost`) — discovered live when an unconfigured `Square:AccessToken` threw from `SquareClient`'s constructor and killed ExamTools/Zoom/Discord polling too, not just payment generation. `ExamToolsClient`/`ZoomClient`/`DiscordEventClient`/`SquareClient` all defer credential checks to first *use* (inside the method that needs them) for exactly this reason — keep new API clients consistent with that pattern.
- **Square's `payment.updated` webhook does not include `reference_id`** — only `order_id`. `Payment.SquarePaymentReferenceId` stores the Square `order_id` (returned when the link is created), not our own `Order.ReferenceId` (which is set to `Payment.Id`, but only for human cross-referencing in Square's dashboard — it's never echoed back). See `docs/square-payments.md`.
- **When an "optional integration" gate combines multiple independent pieces (e.g. Zoom + Discord both feeding one `ZoomDiscordSyncedStartUtc`), do not write the per-piece "settled" check as `!IsConfigured || succeeded`.** That reads as "fine either way," which is wrong: it marks the *whole thing* settled (and stops retrying forever) the instant any one piece is unconfigured, even though the other piece may still be waiting. A dedicated test (`NeitherZoomNorDiscordConfigured_SessionStaysPending_NoCallsMade`) caught this before it shipped. Correct form: `succeeded` alone — a piece that's unconfigured simply never contributes toward "succeeded," so the aggregate stays unsettled (and gets retried, and logged once in aggregate) for as long as that piece stays unconfigured. See `SessionEventSchedulingService.SyncZoomAndDiscordAsync`.
- **A client's `SmtpUsername`/similar "did the admin actually finish setup" signal is not the same as "is a hostname/URL present."** `SmtpEmailSender.IsConfigured` originally checked only `SmtpHost`, which has a real default baked into `appsettings.json` (`smtp.mailgun.org`) — so it read "configured" the instant the repo was cloned, before any credentials existed, and threw a real (if expected) `MailKit.ServiceNotAuthenticatedException` every poll instead of the intended quiet skip. Fixed by requiring `SmtpUsername` too. When adding `IsConfigured` to a new client, make sure it actually reflects "an admin did something," not just "a shipped default is non-empty."
- **`www.fcc.gov` (the FCC's documentation/PDF pages) blocks or heavily throttles non-browser HTTP clients — `data.fcc.gov` (the actual ULS download host) does not.** Discovered while researching Phase 5: plain `curl`/`WebFetch` requests to `www.fcc.gov/file/.../download` reliably hung or reset, while identical requests to `data.fcc.gov/download/pub/uls/...` (what `FccUlsClient` actually calls) returned instantly. If FCC connectivity ever looks broken, check which host is actually failing before assuming the download API itself is down.
- **Don't trust a PDF-to-text extraction's field-position numbers without cross-checking real data, even for a well-established public format.** The FCC's own ULS field-layout PDF lists FRN at EN position 24; a real downloaded `EN.dat` row's FRN-shaped (10-digit) value was actually at position 23 — the document had an extra phantom field between two real ones, an apparent PDF-extraction artifact. `FccUlsRecordParser` uses the position verified against live data, not the document's stated one. See `docs/fcc-uls-watcher.md`.
- **`decimal.ToString("C", CultureInfo.InvariantCulture)` does not produce a `$` sign.** The invariant culture's currency symbol is the generic `¤`. Caught while building Phase 6's `{{PaymentAmount}}` placeholder before it shipped — this app is US-only (FCC/ARRL), so `PaymentReminderService` formats money as a literal `$` prefix + `"F2"` instead. Any future money-in-a-string code should do the same, not reach for `"C"`/`InvariantCulture`.
- (Environment-specific quirks and gotchas go here as they're discovered — e.g. API quirks, IIS behavior, network/DMZ restrictions, auth issues)

## Definition of Done

- Code builds without warnings
- Unit tests pass (where applicable per Testing/Quality section)
- No secrets, connection strings, or sensitive data committed
- Documentation updated in the appropriate file per Documentation Structure (README, CONTRIBUTING.md, ARCHITECTURE.md, SECURITY.md, or /docs) if setup/config/behavior changed
- CLAUDE.md updated if a new architecture decision, gotcha, or config quirk was introduced
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
| `/docs` folder | The "blueprint room" | Deep technical detail: architecture decisions, API specs, DB schemas, troubleshooting playbooks — as individual `.md` files (e.g. `docs/deployment.md`) |

- Use a GitHub Wiki or GitHub Pages only if documentation needs to be browsable outside the repo (e.g. for external stakeholders) — not needed for internal City projects by default
- Ownership, contacts, and escalation info belong in the README, not in this file

## Instructions for Claude

- Do not guess at facts, APIs, or library behavior — verify, and cite sources/docs when possible
- Keep responses concise by default; expand only when asked
- When producing code, include setup/run instructions for **Visual Studio** and/or **VS Code** as appropriate for the project type
- Flag any assumptions explicitly rather than silently filling gaps
- For deployment/CI tasks, default to GitHub Actions targeting Linux (systemd deploy, matching the NcsScheduler pattern), GitHub Flow branching; deploy trigger is on tag push only, not every commit (see Phase 0 in docs/spec.md)
- Maintain repo documentation per the Documentation Structure section above — route content to the right file rather than piling everything into README
- Update the claude.md file for architecture decisions, gotchas, and non-obvious configs you need to keep note of for future reference

## Notes

- This file is a starting template — update per-repo as conventions solidify.