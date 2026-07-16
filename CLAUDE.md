# CLAUDE.md

This is a Visual Studio project that is designed to automate many of the mundane tasks that a Amateur Radio Volunteer Examiner (VE) Session Manager (SM) needs to do to run a session include creating a Zoom session, sending payment links and reminder emails. See docs/spec.md for details.

## Current State

- No code has been scaffolded yet — the repo currently contains only this file and `docs/spec.md`. There is no solution/project file, no build, and no tests to run.
- Next step is Phase 0 in `docs/spec.md` (project foundation: ASP.NET Core 10 solution with a Worker Service + web project sharing a class library, EF Core + initial migration, `JobRunHistory` logging helper).
- Once Phase 0 lands, replace this section with real build/test/lint commands and update Testing / Quality below with the actual xUnit project layout.

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