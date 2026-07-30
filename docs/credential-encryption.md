# Credential encryption at rest

Added 2026-07-30 after a security review flagged `Team`'s per-team integration credentials
(ExamTools/Zoom/Square/SMTP) as plaintext columns — see CLAUDE.md's Change Log entry and
`docs/deployment.md`'s "Data Protection key ring" note for the deploy-side implications.

## What's encrypted, and what isn't

Only genuine bearer secrets on `Team` — the ones that alone are enough to authenticate as this
team against an external service:

- `ExamToolsPassword`
- `ZoomClientSecret`
- `SquareAccessToken`
- `SquareWebhookSignatureKey`
- `SmtpPassword`

Deliberately left plaintext: `ExamToolsUsername`, `ZoomAccountId`, `ZoomClientId`,
`SquareLocationId`, `SquareWebhookNotificationUrl`, `SmtpHost`, `SmtpUsername`. These are ids/urls/
usernames — useful to read at a glance for debugging, and not independently useful to an attacker
without the paired secret above.

Explicitly out of scope: `Payment.YouthConfirmationToken`. It's a single-use bearer capability
token (a public link a candidate clicks once), a different threat model from a durable third-party
credential — not touched by this change.

## How it works

`EncryptedStringConverter` (`src/VeSessionManager.Core/Data/EncryptedStringConverter.cs`) is an EF
Core `ValueConverter` backed by ASP.NET Core's Data Protection API (`IDataProtector`), applied to
the five properties above in `AppDbContext.OnModelCreating`.

The important design choice: the converter's read path (`Unprotect`) **falls back to the raw
stored value unchanged** if it isn't valid protected payload, rather than throwing. This means:

- Existing plaintext rows are still perfectly readable — the app behaves identically whether or
  not a given row has been migrated yet. No hard cutover, no crash risk.
- The one-time migration (below) doesn't need to read "around" the converter via raw SQL — a
  completely normal `dbContext.Teams.ToListAsync()` already returns every team's true plaintext
  value, migrated or not.

The write path (`Protect`) always encrypts whatever's currently in memory — so any *new* write
(a Team Settings save, a fresh team's credentials being entered for the first time) is
automatically encrypted going forward with zero extra code at the call site.

## Migrating existing data

`TeamSecretsMigrationService` (`src/VeSessionManager.Core/Admin/TeamSecretsMigrationService.cs`)
does the one-time sweep: for every `Team`, for each of the 5 credential properties that's non-null,
it forces EF to re-save that property (via `EF.Property(...).IsModified = true`, since re-setting a
property to its own already-equal in-memory value wouldn't otherwise register as a change to save).
The re-save runs the value through the converter's encrypt path regardless of whether it needed it.

Invoke it via the Worker's CLI flag:

```bash
dotnet run --project src/VeSessionManager.Worker -- --migrate-team-secrets
```

This runs once, logs how many teams it touched, and exits — it does **not** start the normal
Worker background jobs. It's deliberately a human-triggered one-off, not something that runs
automatically on every startup, since it touches every real team's live external-service
credentials.

**It's idempotent and safe to re-run** — not just for this initial rollout, but as an ongoing
recovery tool:

- **Restoring an old backup.** If a DB backup taken *before* this feature shipped (or before a
  given team was migrated) is ever restored onto a server that already has the current key ring,
  those restored rows are plaintext again. Re-running the command brings them up to the current
  posture using whatever key ring is live now.
- **An interrupted first run.** If the migration is killed partway through, re-running it is safe —
  it re-saves every populated credential every time regardless of prior state, so there's no
  partial-migration state to reconcile.
- **Stray plaintext bypassing the normal save path.** If a credential is ever set via a raw
  `sqlite3` CLI edit or a future bulk-import tool that writes directly to the DB instead of going
  through EF's mapped `DbContext`, re-running the command sweeps it up.

It is **not** a recovery tool for a lost key ring — see below.

## The key ring itself

Both `VeSessionManager.Web` and `VeSessionManager.Worker` register Data Protection with:

- The same application name (`"VeSessionManager"`, hardcoded identically in both `Program.cs`
  files)
- The same persisted key-ring path (`DataProtection:KeyRingPath` config key — `../../.dataprotection-keys`
  in local dev, `/var/lib/vesessionmanager/dataprotection-keys` in Production, mirroring the
  existing `ConnectionStrings:DefaultConnection` per-environment convention)

**If these two ever drift — different app name or different key-ring path — one process's writes
become silently unreadable by the other**, surfacing only as a confusing decrypt-fallback (the
converter's legacy-plaintext fallback would kick in, so it wouldn't even crash — it would just look
like a value never got migrated, or like a value written by one process reads back as its own
ciphertext when read by the other). Keep both registrations in sync if either ever changes.

**If the key-ring directory is ever lost** (disk wipe, accidental deletion, a backup that excluded
it), every encrypted credential becomes permanently unrecoverable — there is no code-level recovery
from this. The only path forward is manually re-entering every team's Zoom/Square/SMTP/ExamTools
credentials through the Team Settings admin page. This is why the key ring must be backed up with
the same discipline as the database file itself (see `docs/deployment.md`).

**The key itself is generated automatically — nothing to create or configure by hand.** The first
time either service starts and finds the configured path empty, Data Protection generates a new
master key there itself (via the OS's CSPRNG) and writes it out as an XML file, the same way the
SQLite DB file gets created on first run.

**Caveat that matters for what this actually protects against: on Linux, the key file itself is
stored unencrypted by default.** Windows gets DPAPI-based encryption-at-rest for the key
automatically; there's no Linux equivalent unless one is explicitly configured (e.g. an X.509
certificate to encrypt the key ring — which just relocates the same secret-management problem to
protecting that certificate instead, not something added here). This means the encryption only
actually protects you in scenarios where **the key ring and the DB end up in different hands** —
e.g. someone gets a copy of the DB backup but not the server itself.

**Do not let the key-ring backup get bundled together with the DB backup into one artifact.** If
your backup process ever ships the whole `/var/lib/vesessionmanager/` directory (DB *and*
`dataprotection-keys/`) as a single archive, and that archive is what leaks (a misconfigured public
bucket, a stray upload, a decommissioned drive), the unencrypted key sitting right next to the
ciphertext defeats the entire point — anyone with that one archive can decrypt everything as
trivially as if the columns had never been encrypted at all. Keep the key ring's backup handled
separately from — or with tighter access than — wherever the DB backup goes, especially whatever
destination is most exposed (a shared drive, a less-access-controlled bucket, an off-site sync).
Bundling them together isn't a subtle risk reduction, it's a silent full reversal of this feature.
