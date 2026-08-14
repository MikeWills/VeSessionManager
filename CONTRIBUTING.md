# Contributing

## Branching and commits

- Branch prefixes: `feature/`, `fix/`, `chore/`, `hotfix/`, followed by a short kebab-case
  description (e.g. `feature/pii-purge-job`).
- Commit messages follow [Conventional Commits](https://www.conventionalcommits.org/)
  (`feat:`/`fix:`/`docs:`/`chore:`/`refactor:` + description).

## Pull requests

**All changes land on `main` via a pull request — no direct pushes.** One logical change per PR;
title matches the commit convention above.

**`main` is protected** (enabled 2026-08-13), so this is enforced rather than trusted:

- a pull request is required — a direct push to `main` is rejected
- **`build-and-test` must be green** before the merge button is available
- the branch must be up to date with `main` before merging
- force pushes and branch deletion are blocked

**No approving review is required**, deliberately: this is a single-maintainer project, and a
mandatory approval would mean nothing could ever merge. That is a staffing fact, not a statement
about review being unimportant — see `CLAUDE.md`'s Definition of Done for the review-before-merge
policy this pairs with, including Claude's own pre-PR review pass.

Administrators are not included in the rule, so a direct push remains technically possible in a
genuine emergency. It is not routine practice, and the protection exists so that "I'll just push
this one small thing" stops being a decision anyone has to make at 2am.

If you are contributing from a fork: open a PR as normal. Workflow runs from forks require
maintainer approval, so your checks may sit queued until someone starts them.

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
