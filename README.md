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
