# VE Session Manager

Automation for the mundane half of running an amateur radio exam session.

If you are a **Volunteer Examiner Session Manager**, you already know the routine around a session:
create the Zoom meeting, post the Discord event, chase payments, send the confirmation email, send
the reminder, then watch the FCC for a week to see whose license actually got granted. This app does
that work, driven by the sessions you have already scheduled in **ExamTools**.

It is a self-hosted web app plus a background worker. You run it on your own server; nothing about it
is a service anyone sells you.

## What it does

- **Reads your sessions and candidates from ExamTools** and keeps following them — reschedules,
  cancellations, walk-ins and roster changes all flow in on their own.
- **Creates the Zoom meeting and the Discord event** for each session, and keeps the links attached
  to it.
- **Generates a Square payment link per candidate**, records payment when Square says so, and chases
  what is still owed.
- **Sends candidate email** — registration confirmation, day-before reminder, FCC fee reminder — from
  templates you edit, in your team's own wording.
- **Watches the FCC** through ExamTools' ULS mirror for the license grant or upgrade that resulted
  from each exam, and shows you who is still pending.
- **Tracks the volunteer side too** — VE rosters, accreditation, license expiry, sessions worked.
- **Purges candidate PII** on a retention window you choose.

Everything except ExamTools is optional. Configure only the parts you use.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) to build
- A Linux server if you want to run it for real (systemd + a reverse proxy); Windows and macOS are
  fine for development
- An **ExamTools account** for your team — the one hard requirement
- Optionally: a Zoom Server-to-Server OAuth app, a Discord bot, a Square account, an SMTP mailbox

SQLite is the database. There is nothing else to install.

## Quick start

```bash
git clone https://github.com/MikeWills/VeSessionManager.git
cd VeSessionManager

dotnet build
dotnet test

dotnet run --project src/VeSessionManager.Worker    # background jobs
dotnet run --project src/VeSessionManager.Web       # admin backend, http://localhost:5158
```

The database is created and migrated automatically on first start — no `dotnet ef database update`
step. In Development, four seeded accounts (one per role) let you sign in immediately; they are
listed in [`docs/configuration.md`](docs/configuration.md#development-seed-accounts) and exist only in
Development.

## Setting up a real deployment

### 1. Create the first administrator

A new database has **no account anyone can sign into**, on purpose — this app never ships a
credential that works before you have set it up. The Web service refuses to start until an
administrator exists, logging `Critical` and exiting rather than serving a login page where nothing
can succeed.

```bash
dotnet VeSessionManager.Web.dll --create-admin --email you@example.org --name "Your Name" [--callsign WX0MIK]
```

It applies migrations first (so it works before either service has ever run), prints a generated
password **once**, and exits. Save it — only the hash is stored. Lost it? Run the command again with
a different email.

To choose the password yourself, pass it in the environment so it stays out of shell history and
`ps`:

```bash
VSM_ADMIN_PASSWORD='choose-something-long' dotnet VeSessionManager.Web.dll --create-admin --email you@example.org --name "Your Name"
```

### 2. Start order on a new server

1. **`--create-admin`** — before either service; it applies migrations too.
2. **Worker** — nothing left to migrate, so it just starts polling.
3. **Web** — comes up clean now that an account exists.

Web and Worker both migrate at startup and would otherwise race on the same SQLite file. Running
`--create-admin` first means neither service is ever the one applying migrations.

### 3. Configure your team

Sign in and work through **Admin**:

- **Team Settings** — your ExamTools credentials, and whichever of Zoom / Discord / Square / SMTP you
  use. Everything is optional except ExamTools.
- **System Settings → Test Mode** — turn it **on** with an override address until you are ready to
  email real candidates. Every email in the app routes through it.
- **System Settings → System Email** — required before password reset can send anything.
- **System Settings → PII retention window** — null by default; the purge job does nothing until you
  set it.
- **VECs** — each VEC needs an ExamTools code (if it differs from the name) *and* a fee
  configuration, or its sessions are silently skipped at ingestion.

Full reference, including what happens when a credential is missing:
[`docs/configuration.md`](docs/configuration.md).

### 4. Put it on a server

[`docs/deployment.md`](docs/deployment.md) has a complete Linux setup: service account, directory
layout and permissions, systemd units for both processes, an Apache virtual host, and the manual
build-and-publish commands.

**Two things there are not optional**, and both are about the same risk. The Data Protection key ring
encrypts your stored credentials, so it must live outside the deployed application directory, and it
must be backed up **separately from the database** — one archive containing both is the same as
storing every credential in plaintext. If the key ring is lost, every stored credential is
permanently unrecoverable.

The app is a plain `dotnet publish` output and two systemd units, so deploy it however you already
deploy things. A tag-triggered GitHub Actions pipeline is included if you want one — it is reusable,
and [`docs/deployment.md`](docs/deployment.md#automated-deploy-github-actions) covers what to set.

## Documentation

| | |
|---|---|
| [`ARCHITECTURE.md`](ARCHITECTURE.md) | How the pieces fit together, and why |
| [`docs/configuration.md`](docs/configuration.md) | Every credential, and what happens without it |
| [`docs/deployment.md`](docs/deployment.md) | Server setup, systemd, CI/CD |
| [`CONTRIBUTING.md`](CONTRIBUTING.md) | Local dev, tests, branching |
| [`SECURITY.md`](SECURITY.md) | Reporting a vulnerability |
| [`docs/spec.md`](docs/spec.md) | Full build plan and data model |
| [`runbooks/`](runbooks/) | Operational procedures: deploy, roll back, restore, and symptom-first diagnostics |
| [`docs/`](docs/) | One deep-dive per subsystem |

## Status

All planned phases are built and running against real sessions. Outstanding work — features, ops
tasks, and review findings alike — lives in
[GitHub issues](https://github.com/MikeWills/VeSessionManager/issues), which is the single list of
record.

## Contributing

Issues and pull requests are welcome. [`CONTRIBUTING.md`](CONTRIBUTING.md) covers local setup and
conventions; the short version is Conventional Commits, one logical change per PR, and tests for
anything with behaviour.

## License

[MIT](LICENSE) — © 2026 Mike Wills.
