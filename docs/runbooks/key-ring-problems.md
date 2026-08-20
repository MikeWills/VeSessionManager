# Runbook — Data Protection key ring problems

**When:** the Worker or Web refuses to start naming teams and columns; every integration suddenly
fails to authenticate; or the key ring is being moved, restored, or its match is being proved.

**Why it works this way:** [`docs/credential-encryption.md`](../credential-encryption.md).

---

## ⚠️ Read this before touching anything

**Never "fix" a key-ring problem by re-entering credentials in Team Settings.** Saving a credential
encrypts it under whatever key is loaded *now* and overwrites the original ciphertext permanently.
If the real key ring turns up an hour later, it no longer opens anything.

Two failure modes look identical and always will:

- a **wrong or missing key ring**, and
- **un-migrated plaintext**.

`EncryptedStringConverter`'s read path returns the raw stored value when `Unprotect` throws — which
is exactly what makes the legacy-plaintext migration safe. Nothing throws, nothing logs, and every
integration quietly authenticates with a base64 blob. `DataProtectionKeyRingGuard` is the backstop
that converts that silence into a startup crash.

---

## Symptom: a service refuses to start, naming teams and columns

That is the guard, working. It means a credential still looks like ciphertext *after* being read
through the converter.

1. **Do not deploy over it. Do not re-enter credentials.**
2. Confirm the configured path is what you expect, and that it is not empty:

   ```bash
   sudo ls -l /var/lib/vesessionmanager-keys/
   ```

   An empty directory is the classic cause: Data Protection generates a fresh key and the old
   ciphertext becomes unreadable.

3. If keys were recently moved or a path changed, **put the original key ring back** and restart.
   The pre-deploy snapshots are inside the key ring directory itself:

   ```bash
   sudo ls -ltd /var/lib/vesessionmanager-keys/.bak-*
   ```

4. If the key ring came from a restore, follow
   [`restore-from-backup.md`](restore-from-backup.md) — the order matters.

## Symptom: everything starts, but every integration fails to authenticate

Same cause, one layer quieter — a credential is being sent as an undecryptable blob. Prove it
read-only:

```bash
sudo -u vesessionmanager sh -c 'cd /opt/vesessionmanager/worker && exec dotnet ./VeSessionManager.Worker.dll --verify-keyring'
```

Run it from the app directory — the content root is the current directory, not the DLL's. Elsewhere
it finds no `appsettings` at all and reports `no such table: Teams`, which reads as a damaged
database.

## Legitimate use of `--migrate-team-secrets`

It encrypts credentials that are genuinely still plaintext. It is idempotent and safe to re-run:

- after restoring rows from a **pre-encryption** backup,
- after an interrupted first run,
- to sweep up anything written outside EF's mapped `DbContext`.

It is **not** a recovery tool for a lost key ring.

⚠️ **The guard deliberately runs before this switch.** A `--migrate-team-secrets` run against the
*wrong* key ring would rewrite every credential with the undecryptable value it just read, and
destroy the originals. If the guard is refusing, that refusal is protecting you — resolve it first.

```bash
sudo -u vesessionmanager sh -c 'cd /opt/vesessionmanager/worker && exec dotnet ./VeSessionManager.Worker.dll --migrate-team-secrets'
```

## Moving the key ring to a new path

Copy **before** deploying the config change; verify; only then remove the old copy.

```bash
# 1. BEFORE deploying the config change
sudo mkdir -p /var/lib/vesessionmanager-keys
sudo cp /var/lib/vesessionmanager/dataprotection-keys/*.xml /var/lib/vesessionmanager-keys/
sudo chown -R vesessionmanager:vesessionmanager /var/lib/vesessionmanager-keys
sudo chmod 700 /var/lib/vesessionmanager-keys

# 2. Confirm they arrived — an empty directory is the failure mode
sudo ls -l /var/lib/vesessionmanager-keys/

# 3. Deploy (tag a release). Both services pick up the new path together.

# 4. Verify
sudo journalctl -u vesessionmanager-worker -n 30 --no-pager | grep -i "key ring"

# 5. Only after the UI confirms a team's credentials still work
sudo rm -rf /var/lib/vesessionmanager/dataprotection-keys
```

The guard is the backstop, not the procedure — do not treat "it will refuse to start" as permission
to skip the copy.

## Standing rules

- Web and Worker must register Data Protection with the **same application name** (`VeSessionManager`)
  and the **same** `DataProtection:KeyRingPath`. Drift does not throw; one process's writes just
  become unreadable by the other.
- The key ring lives at `/var/lib/vesessionmanager-keys/`, mode `0700` — a **sibling** of the
  database directory, never a child. A child directory puts the key inside any archive of the
  database, which reverses the entire point of encrypting the columns.
- Back it up **separately** from the database, ideally to a different destination with tighter
  access.
- If the key ring is genuinely unrecoverable, there is no code-level recovery: every team re-enters
  every ExamTools/Zoom/Square/SMTP credential by hand.
