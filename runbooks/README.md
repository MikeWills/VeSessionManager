# Runbooks

Operational procedures — **read while doing or fixing something**, not while designing it.

The split is by *when it is read*, not by topic. A runbook is steps, checks and the one warning that
prevents the expensive mistake. The reasoning behind those steps stays in
[`../docs/`](../docs), which is read when changing how something works. Where a runbook states a
rule without arguing for it, it links to the design doc that does.

## Deployment and the box

| Runbook | Read it when |
|---|---|
| [`deploy-a-release.md`](deploy-a-release.md) | Shipping a merged change |
| [`roll-back-a-release.md`](roll-back-a-release.md) | A release is broken and the fix is not minutes away |
| [`stand-up-a-new-server.md`](stand-up-a-new-server.md) | Fresh box, rebuild, or somebody self-hosting |
| [`restore-from-backup.md`](restore-from-backup.md) | The box is lost, the database is corrupt, or a restore test is being proved |
| [`key-ring-problems.md`](key-ring-problems.md) | A service refuses to start naming teams and columns, or every integration suddenly fails to authenticate |

## Something is not working

| Runbook | Read it when |
|---|---|
| [`worker-not-processing.md`](worker-not-processing.md) | Sessions stop appearing, emails stop going out, Job Run History has gone quiet |
| [`square-payment-not-recorded.md`](square-payment-not-recorded.md) | "The candidate says they paid and the app says unpaid" |
| [`candidate-did-not-get-email.md`](candidate-did-not-get-email.md) | A missing email, or one that arrived with a blank link |
| [`arrl-filing-unconfirmed.md`](arrl-filing-unconfirmed.md) | An ARRL submission came back `Unknown` |

## Routine procedures

| Runbook | Read it when |
|---|---|
| [`run-a-historical-import.md`](run-a-historical-import.md) | Backfilling closed sessions beyond the routine sweep's reach |

---

## Not here

- **Off-box backup and restore** (Wasabi retrieval, GPG) is not this app's. The production box runs
  **BackupScripts**, a general-purpose backup project that happens to cover this server among
  others — it is not a component of this app, and it does not track this app's directory layout.
  Its restore procedure is `runbooks/restore.md` **there**. This repo's
  [`restore-from-backup.md`](restore-from-backup.md) is the app-side half only: the order the parts
  go back in, and how to prove it worked. Any other deployment arranges its own off-box backup.
- **Server-side helper scripts** (`vesessionmanager-backup-db`, `vesessionmanager-backup-keyring`,
  `setup-server.sh`, `harden-deploy-sudoers.sh`) live in gitignored `ops/`. A change to one is a
  hand-copied server-side edit that no PR carries.

## The four warnings worth knowing before you need any of these

1. **Never re-enter credentials to fix a key-ring problem.** It encrypts them under the wrong key
   and destroys the originals permanently.
2. **Never press "Submit to VEC" twice after an `Unknown`.** ARRL cannot dedupe and has no unsend;
   absence of a receipt is not absence of a filing.
3. **The pre-deploy `.bak-<stamp>` snapshots are rollback points, not backups** — same disk, newest
   five. The backup that survives losing the box is the separate off-box job.
4. **Any by-hand `dotnet` invocation on the box needs its working directory**
   (`sh -c 'cd /opt/vesessionmanager/worker && exec dotnet ./VeSessionManager.Worker.dll …'`).
   Elsewhere it finds no `appsettings` at all and reports `no such table: Teams`, which reads as a
   damaged database when the real one was never opened.

## Adding one

Name it for the task or the symptom, not the subsystem — `square-payment-not-recorded.md`, not
`square.md`. Somebody reaching for a runbook knows what is wrong, not which class is responsible.
Add a row above, and link the design doc rather than restating it.
