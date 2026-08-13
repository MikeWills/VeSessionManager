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
  in local dev, **`/var/lib/vesessionmanager-keys` in Production**, mirroring the existing
  `ConnectionStrings:DefaultConnection` per-environment convention)

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

### The key ring moved out of the database directory (2026-08-10)

Until v0.3.0 the key ring lived at `/var/lib/vesessionmanager/dataprotection-keys` — *inside* the
same directory as `vesessionmanager.db`. That satisfied "outside the app path, so `rsync --delete`
cannot touch it", but not the paragraph below: one `tar` of `/var/lib/vesessionmanager/`, one disk
image, one careless backup, and the ciphertext travelled with the key that opens it.

It is now `/var/lib/vesessionmanager-keys`, mode `0700`, a **sibling** of the data directory rather
than a child. The deploy takes a pre-deploy snapshot of it, into its own directory rather than beside
the database — putting the copy next to the ciphertext would rebuild the exact problem.

Migration runbook (copy keys → verify → deploy → verify → remove the old directory) is in
`docs/deployment.md`. Done on the beta server 2026-08-11; the startup log confirmed
`Data Protection key ring verified — 3 team(s), all stored credentials readable`.

### `DataProtectionKeyRingGuard`: why config alone was not enough

The read path's legacy-plaintext fallback (above) is what makes the migration safe, and it is also
what makes a **wrong or missing key ring indistinguishable from a not-yet-migrated row**. Nothing
throws, nothing is logged, the app starts normally, and every integration quietly authenticates with
a base64 blob. The failures then surface as Zoom/Square/SMTP errors that point anywhere but here.

`DataProtectionKeyRingGuard` runs at startup in both hosts, after `Database.Migrate()`, and refuses
to start if any stored credential still looks like ciphertext *after* being read through the
converter — which is precisely a credential this process cannot decrypt. Detection needs no raw SQL
and no new column: a Data Protection payload is base64url of a blob whose first four bytes are the
magic header `09 F0 C9 F0`, always encoding to the prefix `CfDJ8`.

Two details that are load-bearing:

- **It runs before the Worker's one-off `--` switches.** A `--migrate-team-secrets` run against the
  wrong key ring would rewrite every credential with the undecryptable value it just read back,
  destroying the originals.
- **Its error message tells you not to "fix" it by re-entering credentials**, because doing so
  overwrites the originals under the new key and makes them unrecoverable for good. That instruction
  is asserted in a test — it is the part most likely to be lost in a future reword.

It also puts teeth behind the Web/Worker agreement described above, which until now was a documented
constraint with nothing enforcing it.

### Verifying a restored key ring: `--verify-keyring` (2026-08-12)

```bash
sudo -u vesessionmanager dotnet /opt/vesessionmanager/worker/VeSessionManager.Worker.dll --verify-keyring
```

Runs the guard, prints its verdict, exits 0 (readable) or 1 (not), and starts nothing else. Added
because proving a restored backup previously meant booting a normal Worker — which starts nine
background jobs against restored, *live* credentials, so a test restore could poll ExamTools, create
Zoom/Discord events and mail real candidates. There was no safe way to ask the question.

Three deliberate differences from the startup guard:

- **It skips `Database.Migrate()`.** A check that is safe to run on any schedule must not write to
  the database it is checking, and a restored backup older than the running binary should be
  reported, not silently upgraded by the act of verifying it.
- **Zero teams is a failure, not a pass.** The guard passes when it finds nothing *unreadable*, so an
  empty database verifies without checking anything — correct for a startup guard, useless as proof
  a restore worked. `DataProtectionKeyRingGuardTests.NoTeams_Passes` documents the pairing.
- **It reports through stderr and an exit code, not an unhandled exception.** The caller is a shell
  script or a person mid-restore. A failure to *complete* the check is distinguished from a check
  that completed and failed — those call for different next steps.

Because it never writes a credential, it cannot cause the destroy-the-originals failure the guard's
own message warns about. Restore procedure — for this app and the rest of the box — is
`runbooks/restore.md` in the BackupScripts repo.

**Do not let the key-ring backup get bundled together with the DB backup into one artifact.** If
your backup process ever ships the whole `/var/lib/vesessionmanager/` directory (DB *and*
`dataprotection-keys/`) as a single archive, and that archive is what leaks (a misconfigured public
bucket, a stray upload, a decommissioned drive), the unencrypted key sitting right next to the
ciphertext defeats the entire point — anyone with that one archive can decrypt everything as
trivially as if the columns had never been encrypted at all. Keep the key ring's backup handled
separately from — or with tighter access than — wherever the DB backup goes, especially whatever
destination is most exposed (a shared drive, a less-access-controlled bucket, an off-site sync).
Bundling them together isn't a subtle risk reduction, it's a silent full reversal of this feature.

## Never compare an encrypted column server-side

*Issue [#279](https://github.com/MikeWills/VeSessionManager/issues/279), 2026-08-11.*

`IngestionStatusService` tested `t.ExamToolsPassword != null && t.ExamToolsPassword != ""` inside a
LINQ projection. EF translates the `""` constant **through the converter too**, emitting a comparison
against a freshly `Protect("")`'d ciphertext — non-deterministic, so it can never equal any stored
value. **The predicate was always true.**

The comment that justified it stated the false premise outright: *"presence survives encryption — a
non-empty ciphertext means a non-empty plaintext."* It does not. `Protect("")` returns a perfectly
non-empty ciphertext.

The consequence was a screen that disagreed with the job. An admin clearing a team's ExamTools
password stored `Protect("")`; the Ingestion Status page and the site-wide health banner reported the
team as configured and due, while `SessionIngestionJob` correctly skipped it via
`Team.IsExamToolsConfigured`, which uses `IsNullOrWhiteSpace`. Nothing logged on either side.

**Two things follow, and the second is the one that generalizes.**

A stored blank is *ciphertext*, not SQL `NULL` and not `''`. So no predicate of any kind can identify
it — not `!= ""`, not `LENGTH(...)`, nothing. **Decryption is the only way to tell a blank from a
secret**, which is why the fix materializes the one column and tests it in memory rather than trying
to write a cleverer `WHERE`. The other four credentials are still never touched.

`!= null` alone is safe: EF special-cases null and does not send it through the converter.

The write path was tightened at the same time so blank stores `null` rather than `""`, but that only
prevents new occurrences — any database that has been running may already hold `Protect("")`, which
is precisely why the read side had to become correct on its own.

`EncryptedColumnPredicateSqliteTests` pins this, **and it has to be a SQLite test**: on the InMemory
provider the same expression is evaluated as plain LINQ over decrypted values, where it behaves
exactly as written. A test of this on InMemory passes against the bug.
