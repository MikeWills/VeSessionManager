# Runbook — Roll back a release

**When:** a deployed release is broken and the fix is not minutes away.
**Who:** whoever can tag and can SSH to the box.

⚠️ **There is no symlink/`releases/` scheme on this box.** `deploy.yml` `rsync --delete`s straight
over `/opt/vesessionmanager/{worker,web}/`, so the previous build is *gone* the moment the sync
runs. Rolling back means **deploying an earlier tag**, not swapping a symlink.

---

## Decide first: did the bad release change the schema?

```bash
git diff --stat <last-good-tag>..<bad-tag> -- src/VeSessionManager.Core/Migrations/
```

- **No migration** → code-only rollback. Step A alone is enough.
- **A migration ran** → code rollback alone will **not** undo it. An older binary against a newer
  schema may start and behave wrongly rather than fail loudly. Do step A *and* step B.

## Step A — redeploy the last good tag

Re-tagging the same commit is cleanest; force-moving an existing tag is not.

```bash
git tag v0.4.1-rollback <last-good-commit>
git push --tags
```

Then follow [`deploy-a-release.md`](deploy-a-release.md) from step 2 — the workflow is identical,
including its own pre-deploy snapshots.

## Step B — restore the database (only if a migration ran)

The pre-deploy snapshots live beside the originals, newest 5 kept, named `.bak-<14 digits>`:

```bash
sudo ls -lt /var/lib/vesessionmanager/vesessionmanager.db.bak-*
sudo ls -ltd /var/lib/vesessionmanager-keys/.bak-*
```

Pick the stamp taken **immediately before the bad deploy**. The two halves are taken as a pair and
retained equally — restore the pair, not one half.

1. Stop both services (Web first).
2. Restore the **key ring** first, then the database — a database restored against a missing or
   wrong key ring makes the app refuse to start, which is correct but confusing mid-incident.
3. Start the **Worker**, confirm the `key ring verified` line, then start Web.
4. Confirm in Team Settings that a team's credentials still read as configured, and watch one
   ingestion poll succeed — that exercises a decrypted ExamTools password end to end.

Full detail, including the off-box case: [`restore-from-backup.md`](restore-from-backup.md).

⚠️ **Everything written since that snapshot is lost** — ingested sessions, payments recorded by
webhook, audit entries. If the bad release ran for hours, weigh a forward fix against the data loss
before restoring.

## What a rollback cannot undo

- **Emails already sent.** No unsend.
- **Zoom/Discord events already created.** They stay; the next poll's query-before-create matching
  should find them rather than duplicating, but confirm.
- **Square payment links already issued**, and any payment taken against one.
- **An ARRL filing already POSTed.** ARRL has no unsend and cannot dedupe — see
  [`arrl-filing-unconfirmed.md`](arrl-filing-unconfirmed.md).

## After

Write down which tag was rolled back and why, in the PR or issue that carried the bad change —
CLAUDE.md's Definition of Done expects rollback decisions to be recorded somewhere findable.
