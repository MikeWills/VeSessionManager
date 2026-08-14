# Configuration & Secrets

Where every credential lives, and what happens when one is missing. The short version is on the
[README](../README.md); this is the full reference.

## The rule: credentials live on the `Team` row, not in config files

Almost every integration credential is a column on `Team`, hand-edited through **Admin → Team
Settings**, and **encrypted at rest** (see [`credential-encryption.md`](credential-encryption.md)).
Config files hold only things that are the same for every team on a deployment — which host to talk
to, not who to talk to it as.

That is what makes one deployment able to serve several teams with different Zoom accounts, different
Square merchants, and different mail servers.

Three exceptions, all deliberate:

| Credential | Where | Why |
|---|---|---|
| `Discord:BotToken` | user-secret / env var, shared | One bot serves every team; each team supplies only its own `DiscordGuildId` |
| `Authentication:Google:*`, `Authentication:Microsoft:*` | user-secret / env var, shared | OAuth apps are registered per deployment, not per team |
| `SystemSmtp*` | `SystemSettings` (deployment-wide) | Account mail — password reset — is not any one team's candidate mail |

On a server, config values become environment variables with `__` for `:` —
`Discord__BotToken`, `Authentication__Google__ClientId`, and so on.

## Optional means optional

**ExamTools is the only hard requirement.** Everything else — Zoom, Discord, Square, SMTP, Google and
Microsoft sign-in — is optional and behaves the same way when unconfigured: the work is skipped with
one quiet log line per poll, not an error, and the tracking field stays null so the *next* poll picks
it up automatically once credentials appear. **There is no backfill step.** Add the credentials and
the work starts happening.

That extends to the UI: a sign-in provider with no credentials simply does not render its button.

## ExamTools (required)

Set per team: `ExamToolsUsername`, `ExamToolsPassword`, `ExamToolsTeamCode`.

`ExamTools:BaseUrl` stays in `appsettings.json` — which host to hit (`examtools.dev` in development,
the live site in production) is a deployment-wide choice, and the per-environment files pick the
right one automatically.

The seeded `Team` row from the `Phase6_5MultiTeamFoundation` migration has **blank** credential
columns on purpose — a migration must never contain a real secret — so ingestion for that team is
skipped until you fill them in.

> **Everything this app does in ExamTools is attributed to the stored credential's account, not to
> the person who clicked.** ExamTools is starting to expose its audit log to VEs, so those entries
> are visible to your team, and they all read as whichever VE's login sits in
> `Team.ExamToolsUsername` — with no trace of the actual end user.
>
> The app is read-only against ExamTools today (sessions, candidates, VE rosters, exam results, the
> ULS mirror), so there is nothing to misattribute yet. It matters twice: pick a credential whose
> name you are content to see against every automated action, and treat it as a real cost when
> weighing any future write-back feature — this app's audit log would know who acted, and ExamTools'
> would not. It is part of why "add walk-in candidate" and "move candidate between sessions" were
> built and then removed in favour of doing those things in ExamTools directly.

See [`examtools-api.md`](examtools-api.md).

## VECs and fee configurations

Not credentials, but ingestion is silently skipped without them. Each VEC needs an **ExamTools code**
(if it differs from its display name) *and* a **fee configuration**. All fourteen FCC-accredited VECs
are seeded with verified codes on Worker startup — see [`vec-examtools-code.md`](vec-examtools-code.md).

In Development the Worker also seeds a starter `Vec` and `FeeConfiguration` (`DevDataSeeder`).

## Zoom and Discord

Per team: `ZoomAccountId`, `ZoomClientId`, `ZoomClientSecret`. A `ZoomUserId` of `"me"` is right for
most single-license accounts.

Discord's bot token is one shared credential:

```bash
dotnet user-secrets set "Discord:BotToken" "<token>" --project src/VeSessionManager.Worker
```

Each team then sets its own `DiscordGuildId` — not a secret, and `null`/`0` reads as "not configured"
the same as a missing token. **The shared bot must be invited into that team's Discord server** before
events will create.

Creating the Zoom Server-to-Server OAuth app and the Discord bot is dashboard work, not something
runnable from this repo: [`zoom-discord-scheduling.md`](zoom-discord-scheduling.md#account-setup-one-time-before-the-four-secrets-in-the-readme-mean-anything).

## Square

Per team: `SquareAccessToken`, `SquareLocationId`, `SquareWebhookSignatureKey`,
`SquareWebhookNotificationUrl`, and `SquareEnvironment` (Sandbox by default).

The webhook route is **`/webhooks/square/{teamId}`** — the team is identified from the route *before*
signature verification, because verification needs that team's own key. So
`SquareWebhookNotificationUrl` must carry the numeric team id, e.g. `https://<host>/webhooks/square/1`.

**Sandbox and Production are separate worlds.** A subscription registered under one mode receives
*zero* delivery attempts for the other — not a 401, no attempt at all — and reusing one mode's
signature key against the other's subscription makes every delivery fail verification. Because
`SquareEnvironment` is per team, two teams on one deployment can legitimately run in different modes,
each needing its own subscription and its own key.

Without a token, payment-link generation is skipped quietly: `Payment` rows are still created as
`Unpaid`, just without a link until Square is configured. See [`square-payments.md`](square-payments.md).

## Email

Per team: `SmtpHost`, `SmtpPort`, `SmtpUsername`, `SmtpPassword`, `SmtpUseStartTls` — with **no
baked-in default on any of the five**, deliberately, so a team reads as "not configured" until
someone actually configures it rather than the instant the repo is cloned.

`EmailSettings` and `EmailTemplate` are per team too: each gets its own From/Reply-To/privacy-policy
settings and its own template wording, seeded once with placeholder content and **meant to be edited
by a human** before real use. Edit them at **Admin → Email Templates**.

`EmailSettings.AdminNotificationEmail` is where operational notices go (payment expiry, for
instance) — the Session Manager's inbox, not a candidate's.

**Before you email anyone real:** turn on **Test Mode** (Admin → System Settings) with an override
address. Every email in the app routes through it. See [`test-mode.md`](test-mode.md),
[`email-notifications.md`](email-notifications.md) and [`email-reference.md`](email-reference.md).

## FCC / ULS

**No credentials.** License and application state is read through ExamTools' ULS mirror, so if
ExamTools is configured, this works. Cadence is set by `UlsWatcherStartHourEt` /
`UlsWatcherIntervalHours` in System Settings, which three jobs share. See
[`uls-watcher.md`](uls-watcher.md).

## Sign-in providers

Username and password work out of the box. Google and Microsoft are optional:

```bash
dotnet user-secrets set "Authentication:Google:ClientId" "<id>" --project src/VeSessionManager.Web
dotnet user-secrets set "Authentication:Google:ClientSecret" "<secret>" --project src/VeSessionManager.Web
dotnet user-secrets set "Authentication:Microsoft:ClientId" "<id>" --project src/VeSessionManager.Web
dotnet user-secrets set "Authentication:Microsoft:ClientSecret" "<secret>" --project src/VeSessionManager.Web
```

Apple sign-in is deliberately not built (cost tradeoff — see [`admin-auth.md`](admin-auth.md)).

## PII retention

`SystemSettings.PiiRetentionWindowDays` is seeded **null**, and the purge job does nothing until it
is set. There is no assumed default: how long a team keeps candidate data is a decision, not a
default. See [`pii-purge.md`](pii-purge.md).

## Development seed accounts

In Development only, `DevAuthSeeder` creates four users — one per role — sharing the password
`Dev-Password1!`. Development-only, not a real secret, and safe in source.

| Email | Role | Team |
|---|---|---|
| `sysadmin@example.com` | SystemAdmin | none (deployment-wide) |
| `teamadmin@example.com` | TeamAdmin | seeded Team (Id 1) |
| `sessionmanager@example.com` | SessionManager | seeded Team (Id 1) |
| `teamlead@example.com` | TeamLead | seeded Team (Id 1), managed by the SessionManager above |

These do not exist in any other environment. On a real deployment the first account comes from
`--create-admin` — see the README.

## Environments

Config is selected by environment name: **Test** or **Production**. There is no separate
`Development` environment name in deployment terms — the local machine serves that role using the
base `appsettings.json`.

> **The Worker reads `DOTNET_ENVIRONMENT`, not `ASPNETCORE_ENVIRONMENT`.** It is a plain generic
> Host, not ASP.NET Core. Web reads both, preferring `ASPNETCORE_ENVIRONMENT`. Neither is set by
> default outside a launch profile, and the generic Host's own default is **Production** — so running
> the built Worker DLL directly, bypassing `launchSettings.json`, silently picks up
> `appsettings.Production.json` and its Linux-only absolute paths. Locally, always use
> `dotnet run --project …`.

Worker and Web share one SQLite file. `dotnet run --project <path>` sets the working directory to
that project's folder, so the base and Test connection strings use `Data Source=../../<file>.db` to
land on the same repo-root file whichever project is running. `appsettings.Production.json` points at
an absolute path instead.

`AllowedHosts` and `App:PublicBaseUrl` in Web's `appsettings.Production.json` are **pinned to a
specific hostname**. A deployment served under any other name returns 400 for every request until
both are updated. That is deliberate: the framework default `"*"`, combined with absolute URLs
derived from the request host, was an account-takeover vector.
