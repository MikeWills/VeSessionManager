# VE Session Manager

Automates the mundane tasks a Ham Radio VE (Volunteer Examiner) Session Manager needs to do
to run a test session — Zoom/Discord scheduling, candidate payment links, reminder emails,
FCC/ARRL tracking — with a role-based admin backend. See [`docs/spec.md`](docs/spec.md) for
the full phased build plan and data model.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- `dotnet-ef` global tool (`dotnet tool install --global dotnet-ef`) if you need to add/inspect migrations directly

## Solution Layout

```
src/
  VeSessionManager.Core/     class library — entities, EF Core DbContext/migrations, JobRunHistoryLogger
  VeSessionManager.Worker/   Worker Service — background jobs
  VeSessionManager.Web/      ASP.NET Core admin backend
tests/
  VeSessionManager.Core.Tests/
```

## Build / Test / Run

```bash
dotnet build
dotnet test

dotnet run --project src/VeSessionManager.Worker
dotnet run --project src/VeSessionManager.Web
```

The Worker applies EF Core migrations automatically on startup, creating `vesessionmanager.db`
if it doesn't exist yet. No manual `dotnet ef database update` step is required.

To add a new migration after changing entities in `VeSessionManager.Core`:

```bash
dotnet ef migrations add <Name> --project src/VeSessionManager.Core
```

## First sign-in on a new deployment

A brand-new database has **no account anyone can sign into** — `DevAuthSeeder`'s test users only
exist in Development, and every page that could create a user requires you to already be signed in.
Nothing is seeded automatically, on purpose: this app never ships a credential that works before you
have set it up.

Create the first administrator from the command line:

```bash
dotnet VeSessionManager.Web.dll --create-admin --email you@example.org --name "Your Name" [--callsign WX0MIK]
```

It applies migrations first (so it works before the services have ever started), prints a generated
password **once**, and exits without starting the web host. Save that password — it is stored only as
a hash. If you lose it, run the command again with a different email to create another administrator.

To choose the password yourself — for scripted or repeatable provisioning — set it in the
environment rather than passing it as an argument, so it stays out of shell history and `ps` output:

```bash
VSM_ADMIN_PASSWORD='choose-something-long'   dotnet VeSessionManager.Web.dll --create-admin --email you@example.org --name "Your Name"
```

If you start the app before doing this, the login page will reject every credential — there is
nothing to sign in as. The startup log says so explicitly and repeats the command.

### Also worth doing before real sessions run

- **Admin → System Settings → Test Mode** — turn it **on** with an override address until you are
  ready to email real candidates. Every email in the app routes through it.
- **Admin → System Settings → System Email** — required before password reset can send anything.
- **Admin → System Settings → PII retention window** — null by default, and the purge job will not
  run until it is set.
- **Admin → VECs** — each VEC needs an ExamTools code (if it differs from the name) *and* a fee
  configuration, or its sessions are silently skipped at ingestion.

## Configuration & Secrets

**ExamTools credentials (Phase 1) now live on the `Team` row in the DB**, hand-edited directly
(no admin UI yet — see [`docs/multi-team.md`](docs/multi-team.md)), not user-secrets: set
`Team.ExamToolsUsername`/`ExamToolsPassword`/`ExamToolsTeamCode` per team. One `Team` row is
seeded automatically by the `Phase6_5MultiTeamFoundation` migration, but its credential columns
are intentionally left blank (migrations must never contain real secrets) — ingestion for that
team is silently skipped until they're filled in. Only `ExamTools:BaseUrl` (which host to hit —
`examtools.dev` in dev, the live site in production) stays in `appsettings.json`, since it's the
same for every team on one deployment; per-environment overrides pick the right one automatically.
See [`docs/examtools-api.md`](docs/examtools-api.md) for API details.

In the Development environment the Worker also seeds a starter `Vec`/`FeeConfiguration` on
first run (see `DevDataSeeder`) — without those rows, ingestion intentionally skips sessions
until fee configuration exists. `Vec` is shared/global across every team, not per-team — see
`docs/multi-team.md` for why.

**As of the multi-team fast-follow, Zoom/Square/Email credentials also live on the `Team` row in
the DB (hand-edited directly, same pattern as ExamTools above), not user-secrets — only Discord's
bot token is still a shared, global user-secret.** See [`docs/multi-team.md`](docs/multi-team.md)
for the full per-team rationale.

The Zoom/Discord scheduler (Phase 2) needs its own credentials, and both Zoom and Discord are
**optional**: leave either unconfigured and the Worker just skips that half (logging one quiet
note per poll, not an error), creating whichever one(s) you *have* set up and back-filling the
rest automatically the moment you add credentials later. If you don't have a Zoom
Server-to-Server OAuth app or a Discord bot yet, see
[`docs/zoom-discord-scheduling.md`](docs/zoom-discord-scheduling.md#account-setup-one-time-before-the-four-secrets-in-the-readme-mean-anything)
for how to create them — that's account-dashboard setup, not something runnable from this repo.

Set `Team.ZoomAccountId`/`ZoomClientId`/`ZoomClientSecret` directly on each team's row (a
`ZoomUserId` of `"me"` is fine for most single-license Zoom accounts). Discord's bot token is
still one shared credential across every team:

```bash
dotnet user-secrets set "Discord:BotToken" "<Discord bot token>" --project src/VeSessionManager.Worker
```

On the server: `Discord__BotToken` environment variable. Each team then needs its own
`Team.DiscordGuildId` (the server events get created in) — not secret, `null`/`0` reads as "not
configured" the same as a missing BotToken; the shared bot must be invited into that team's
Discord server before events will actually create. See
[`docs/zoom-discord-scheduling.md`](docs/zoom-discord-scheduling.md) for API details.

The Square payment-link/webhook flow (Phase 3) reads `Team.SquareAccessToken`/`SquareLocationId`/
`SquareWebhookSignatureKey`/`SquareWebhookNotificationUrl` for each team; the Web project's
webhook route is now **`/webhooks/square/{teamId}`** (the route identifies the team before
signature verification, since verification needs that team's own key), so
`SquareWebhookNotificationUrl` must include the team's numeric id (e.g.
`https://<host>/webhooks/square/1` for the seeded team). If you don't have a Square Developer
account/app yet, see [`docs/square-payments.md`](docs/square-payments.md#account-setup-one-time)
for how to create one, get sandbox credentials, and register the webhook subscription against
the team-specific URL. Only `Square:Environment` (`Sandbox` locally, `Production` in
`appsettings.Production.json`) remains a whole-deployment `appsettings.json` setting, since
sandbox-vs-production is an environment choice, not a per-team one. **Square is optional** too,
same pattern as Zoom/Discord: without a team's `SquareAccessToken` set, payment-link generation
is skipped quietly for that team (Payment rows still get created, `Unpaid`, just without a link
until Square is configured) rather than erroring every poll. See
[`docs/square-payments.md`](docs/square-payments.md) for API details.

Candidate notification emails (Phase 4) read `Team.SmtpHost`/`SmtpPort`/`SmtpUsername`/
`SmtpPassword`/`SmtpUseStartTls` for each team — **also optional**, same pattern, and deliberately
with **no baked-in default** on any of the five columns (so a team reads as "not configured" until
someone actually sets them, not the instant the repo is cloned). `EmailSettings` and
`EmailTemplate` are both per-team now too — each team gets its own From/Reply-To/privacy-policy
settings row and its own template wording, seeded once with placeholder content on first run and
meant to be **hand-edited by a human** (not generated content) before real use; see
[`docs/email-notifications.md`](docs/email-notifications.md) for the full placeholder reference —
editable either directly in the DB or via the Phase 9c admin UI's Email Templates screen.

The FCC ULS watcher (Phase 5) needs **no credentials at all** — `data.fcc.gov` is a public
dataset — so there's nothing to configure beyond the `FccUls:BaseUrl` default already in
`appsettings.json`. See [`docs/fcc-uls-watcher.md`](docs/fcc-uls-watcher.md) for the file formats
and field layout it depends on.

Payment reminders/expiration (Phase 6) reuse Phase 4's SMTP setup — no separate credentials. The
one new piece to configure by hand is `EmailSettings.AdminNotificationEmail` (where the 10-day
expiration notice goes — the Session Manager's inbox, not the candidate's), seeded with a
placeholder alongside the From/Reply-To/privacy-policy fields; see
[`docs/payment-reminders.md`](docs/payment-reminders.md).

The admin backend's login (Phase 9a) supports username/password out of the box; Google and
Microsoft sign-in are **optional**, same pattern as everything else — no credentials configured
just means that sign-in button doesn't render on the login page. Apple sign-in is deliberately not
built yet (cost tradeoff, see [`docs/admin-auth.md`](docs/admin-auth.md)).

The PII purge job (Phase 10) needs no credentials either — its one input,
`SystemSettings.PiiRetentionWindowDays`, is a deployment-wide value with **no default assumed**
(seeded `null`, must be set explicitly via the Phase 9c System Settings admin screen before the job
purges anything); see [`docs/pii-purge.md`](docs/pii-purge.md).

```bash
dotnet user-secrets set "Authentication:Google:ClientId" "<Google OAuth client id>" --project src/VeSessionManager.Web
dotnet user-secrets set "Authentication:Google:ClientSecret" "<Google OAuth client secret>" --project src/VeSessionManager.Web
dotnet user-secrets set "Authentication:Microsoft:ClientId" "<Entra app registration client id>" --project src/VeSessionManager.Web
dotnet user-secrets set "Authentication:Microsoft:ClientSecret" "<Entra app registration client secret>" --project src/VeSessionManager.Web
```

On the server: `Authentication__Google__ClientId` / `Authentication__Google__ClientSecret` /
`Authentication__Microsoft__ClientId` / `Authentication__Microsoft__ClientSecret` environment
variables. In Development, four test users (one per role) are seeded automatically on first run
by `DevAuthSeeder` — log in at `/Account/Login` with any of these and the shared password below:

| Email | Role | Team |
|---|---|---|
| `sysadmin@example.com` | SystemAdmin | none (deployment-wide) |
| `teamadmin@example.com` | TeamAdmin | the seeded Team (Id 1) |
| `sessionmanager@example.com` | SessionManager | the seeded Team (Id 1) |
| `teamlead@example.com` | TeamLead | the seeded Team (Id 1), managed by the SessionManager above |

All four share the password `Dev-Password1!` — Development-only, not a real secret, safe to commit
in source. See [`docs/admin-auth.md`](docs/admin-auth.md) for the full role model and a seeding
gotcha worth knowing if you ever touch `DevAuthSeeder`.

## Environments

Config is selected by environment name (`Test` or `Production`; there is no separate
`Development` environment — the local machine serves that role using the base `appsettings.json`).

**Note:** the Worker project is a plain generic Host, not ASP.NET Core, so it reads
`DOTNET_ENVIRONMENT` rather than `ASPNETCORE_ENVIRONMENT` (the Web project's `WebApplication`
host reads both, preferring `ASPNETCORE_ENVIRONMENT`). Neither is set by default outside a
launch profile, and the Generic Host's own default is `Production` — so running the built DLL
directly (bypassing `launchSettings.json`) picks up `appsettings.Production.json`, which points
at a Linux-only absolute path and will fail to open on a dev machine. Always use
`dotnet run --project ...` locally (it applies `launchSettings.json`'s `DOTNET_ENVIRONMENT` for
you); don't invoke the built `.dll` directly unless you set the environment variable yourself.

The Worker and Web projects share one SQLite file. `dotnet run --project <path>` sets the
working directory to that project's own folder, so the base and Test connection strings use
`Data Source=../../<file>.db` (two levels up from `src/<Project>/`) to land on the same
repo-root file regardless of which project is running. `appsettings.Production.json` in both
projects instead points at the same absolute path
(`/var/lib/vesessionmanager/vesessionmanager.db`) — update that path to match wherever the
service actually gets deployed.

## Deployment

Target is Linux via systemd (see `.github/workflows/build-and-deploy.yml`), triggered on tag
push only (e.g. `v1.0.0`), not on every commit to main. The deploy job in that workflow is
currently a stub — it needs the self-hosted-runner/Tailscale-tailnet and systemd deploy-script
details from the existing NcsScheduler pattern before it can actually deploy anything.
