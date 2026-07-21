# Contributing

## Branching and commits

- Branch prefixes: `feature/`, `fix/`, `chore/`, `hotfix/`, followed by a short kebab-case
  description (e.g. `feature/pii-purge-job`).
- Commit messages follow [Conventional Commits](https://www.conventionalcommits.org/)
  (`feat:`/`fix:`/`docs:`/`chore:`/`refactor:` + description).

## Pull requests

**All changes land on `main` via a pull request — no direct pushes.** One logical change per PR;
title matches the commit convention above.

This is a **convention, not a server-enforced rule.** GitHub branch protection (blocking direct
pushes, requiring status checks/reviews before merge) requires either a paid plan or a public repo
for a private repository — this repo is private and stays on the free plan, so there's no technical
gate stopping a direct push to `main`. It relies on discipline instead. Repo admins may push
directly in a genuine emergency, but shouldn't as routine practice — see `CLAUDE.md`'s Definition of
Done for the review-before-merge policy this pairs with.

## Building and testing

See the [README](README.md) for prerequisites, build/run commands, and the `DOTNET_ENVIRONMENT` /
`ASPNETCORE_ENVIRONMENT` gotcha. Tests are xUnit in `tests/VeSessionManager.Core.Tests`:

```bash
dotnet build
dotnet test
```

CI (`.github/workflows/ci.yml`) runs the same build+test on every push/PR against `main`.

## Releasing

Deploys are triggered by pushing a version tag, never by merging to `main` — see
[`docs/deployment.md`](docs/deployment.md)'s "Triggering a deploy" section.
