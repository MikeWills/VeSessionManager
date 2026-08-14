# Security Policy

## Reporting a vulnerability

**Please do not open a public issue for a security problem.**

Report it through GitHub's private vulnerability reporting: go to the
[Security tab](https://github.com/MikeWills/VeSessionManager/security/advisories/new) and open a
draft advisory. That channel is private between you and the maintainer until a fix is published.

This is a volunteer-run project maintained by one person, so please allow a few days for a first
response. There is no bug bounty.

Useful things to include: what you did, what happened, what you expected, and — if you can — whether
the issue needs an authenticated account and which role. A short reproduction is worth more than a
scanner report.

## What is in scope

The thing worth protecting here is **candidate data**. A deployment holds names, email addresses,
dates of birth, physical addresses and FCC registration numbers for people who took an amateur radio
exam, plus payment records and integration credentials for the team running it.

Especially interesting:

- **Authorization gaps between teams.** Every id-taking handler is supposed to re-check ownership;
  one that doesn't would let a Session Manager on one team reach another team's candidates.
- **Anything that reads or writes a `Team`'s stored credentials.** Those columns are encrypted at
  rest (see [`docs/credential-encryption.md`](docs/credential-encryption.md)); a path that leaks
  plaintext, logs it, or renders it back to a page is a real finding.
- **The Square webhook** (`/webhooks/square/{teamId}`) — it is deliberately anonymous and verifies an
  HMAC signature per team. Signature bypass, replay, or cross-team confusion all matter.
- **Anything that sends email on an unauthenticated request**, or that reflects an attacker-supplied
  host into a link (password reset, VE self-service). Absolute URLs are built from a pinned
  `App:PublicBaseUrl` for exactly this reason.
- **The VE self-service token flow** — entered from a mailed link, so token guessability, reuse after
  expiry, and scope are all fair game.

## What is out of scope

- **The Development seed accounts and their password.** `DevAuthSeeder` creates four users with a
  published password, and they exist only when the environment is `Development`. That is deliberate,
  documented, and not a finding.
- **Missing security headers on a deployment you control.** How you configure your own reverse proxy
  is up to you; report it only if the application itself sets something wrong.
- **Denial of service through volume.** This is a small self-hosted app for a volunteer exam team.
- **Findings that require an already-compromised server**, such as reading the SQLite file or the
  Data Protection key ring directly off disk. That the key ring must be protected as carefully as the
  database is a documented deployment property, not a vulnerability.

## Supported versions

The latest tagged release only. This is a single-maintainer project with one production deployment;
fixes go to `main` and ship in the next tag rather than being backported.

## If you run this yourself

Two things carry the most risk in practice, and both are operational rather than code:

1. **Back up the Data Protection key ring separately from the database, and never in the same
   archive.** The key ring decrypts the credential columns; one archive holding both is equivalent to
   storing the credentials in plaintext. See [`docs/deployment.md`](docs/deployment.md).
2. **Set the PII retention window.** `SystemSettings.PiiRetentionWindowDays` is `null` by default and
   the purge job does nothing until it is set, so candidate PII is kept forever until you choose a
   window. See [`docs/pii-purge.md`](docs/pii-purge.md).
