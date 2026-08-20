# Runbook — Restore from backup

**When:** the box is lost, the database is corrupt, or a restore test is being proved.
**Why it works this way:** [`docs/credential-encryption.md`](../credential-encryption.md),
`BACKUP.md` (gitignored, on this working tree).

---

## Know which copy you are restoring from

| Source | What it is | Where |
|---|---|---|
| **`.bak-<stamp>` snapshots** | Pre-deploy rollback points, newest 5, **same disk** | `/var/lib/vesessionmanager/` and `/var/lib/vesessionmanager-keys/` |
| **Off-box backup** | The real backup (#256, built 2026-08-14): database and key ring to **separate Wasabi buckets under separate keys**, key ring GPG-encrypted on top | BackupScripts repo — procedure is `runbooks/restore.md` **there**, not here |

The Wasabi retrieval and decryption steps belong to BackupScripts and are not duplicated here. This
runbook is the **app-side** half: the order the two halves go back in, and how to prove it worked.

The **VEC archive directory** (`/var/lib/vesessionmanager/vec-archives`) is backed up as well, so a
full restore has three things to put back, not two. It is ordinary files — no key pairing, no
ordering constraint — so restore it whenever is convenient and confirm the `team/vec/year/month`
tree came back populated.

---

## Restore order — the key ring goes back first

1. **Stop both services.** Web first, then Worker.

   ```bash
   sudo systemctl stop vesessionmanager-web
   sudo systemctl stop vesessionmanager-worker
   ```

2. **Restore the key ring** to `/var/lib/vesessionmanager-keys/`, owned by `vesessionmanager`,
   mode `0700`. Confirm it is not empty — an empty directory is the failure mode, because Data
   Protection then silently generates a *fresh* key.

   ```bash
   sudo ls -l /var/lib/vesessionmanager-keys/
   ```

3. **Restore the database** to `/var/lib/vesessionmanager/vesessionmanager.db`, same ownership as
   the original. A `.bak-<stamp>` snapshot is a single self-contained file produced by SQLite's
   `.backup` — there are **no `-wal`/`-shm` sidecars to put back alongside it.**

4. **Prove the pair matches, without starting the jobs:**

   ```bash
   sudo -u vesessionmanager sh -c 'cd /opt/vesessionmanager/worker && exec dotnet ./VeSessionManager.Worker.dll --verify-keyring'
   ```

   This runs the key-ring guard read-only and exits. It exists precisely so a test restore does not
   have to start the Worker's background jobs — which on restored data would poll ExamTools, create
   Zoom/Discord events and **mail real candidates**.

   ⚠️ The `cd` is load-bearing: the content root is the *current directory*, not the DLL's. Run from
   anywhere else and no `appsettings` file is found, the connection string is null, SQLite opens an
   anonymous temporary database, and the first symptom is `no such table: Teams` — which reads as a
   damaged database when the real one was never opened.

5. **Start the Worker**, confirm the log line, **then** start Web.

   ```bash
   sudo systemctl start vesessionmanager-worker
   sudo journalctl -u vesessionmanager-worker -n 30 --no-pager | grep -i "key ring"
   # Data Protection key ring verified — N team(s), all stored credentials readable
   sudo systemctl start vesessionmanager-web
   ```

6. **Confirm end to end.** Team Settings shows a team's credentials as configured, and one
   ingestion poll succeeds. That is what actually exercises a decrypted ExamTools password.

---

## If the key ring is gone but the database survived

⚠️ **Do not re-enter credentials to "fix" a half-restored system** until you are certain the key
ring is unrecoverable. Re-saving a credential encrypts it under the *new* key and destroys any
chance of the old ciphertext ever being readable again. Go to
[`key-ring-problems.md`](key-ring-problems.md) first.

## After any restore

A restored database's credentials are **live**. Before letting the Worker run against it in anger,
decide whether this box should be reaching ExamTools, Zoom, Discord, Square and SMTP at all — a
restore into a staging box with production credentials will happily mail real candidates. Email
test mode (Admin → System Settings) redirects **every** send deployment-wide and is the switch for
this; it refuses to turn on without an override address.
