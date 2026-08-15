# The audit log: what "append-only" actually means here

Written 2026-08-14, resolving the L-06 half of issue #313. The finding was not that something is
broken — it is that a property everyone relies on had never been written down, so nobody could say
how strong it was.

## The claim, precisely

**Append-only is a code convention, enforced by absence. It is not enforced by the database.**

What is true:

- One write path. `AuditLogExtensions.AddAuditLog` is the only place an `AuditLog` row is
  constructed, and `AuditLogs.Add` is the only mutation of the set anywhere in `src/`.
- One read path. The admin page, team-scoped.
- No update or delete path exists anywhere in `src/` — verified during the 2026-08-11 audit and now
  guarded by `AuditLogAppendOnlyTests`, which fails the build if `Remove`, `RemoveRange`,
  `ExecuteDelete`, `ExecuteUpdate` or a direct `new AuditLog` appears.

What is **not** true, and must not be inferred:

- There is no database trigger, no hash chain, and no write-once storage. **Anyone with the SQLite
  file can rewrite history, and nothing would show.** On this deployment that is the service account,
  root, and anyone holding a backup.

## Why that is the accepted position

The person who could rewrite the log is the person who owns the box. Tamper-evidence protects a log
from *its own administrator*, and every mechanism for it either fails to that same person or moves
the problem somewhere else:

- **A hash chain** (each row hashing over the previous) detects edits — but whoever can edit the rows
  can recompute the chain. It only bites if the chain head lands somewhere the editor cannot reach.
- **The off-box backup (#256) is that somewhere**, in principle: database and key ring go to separate
  Wasabi buckets under separate keys. A chain head published there each night would make silent
  rewriting genuinely hard.

That is a real design and it was considered. It was not built because the threat it addresses —
a SystemAdmin covering their tracks on a volunteer exam roster — is not the threat this deployment
faces, and a detection mechanism nobody has agreed to check on a schedule is theatre. **If the
question ever changes** (a second organisation self-hosting, an accreditation body asking for an
evidentiary trail), the chain-plus-off-box-head design above is the thing to build, and this section
is the argument for it.

## What the guard test does and does not buy

`AuditLogAppendOnlyTests` protects the half that is real: it stops a delete path being added quietly.
That matters because the alternative is not "someone maliciously adds one" — it is a plausible
"clean up audit entries older than N" landing alongside unrelated work, restoring the gap without
anyone noticing there had been a property to lose.

It is a source scan, because there is nothing to observe at runtime: the absence of a delete path
cannot be asserted by calling anything. Same shape as `NoNulBytesInSourceTests`.

## Retention

**Built 2026-08-14** (#86, with `JobRunHistories` in #296). This section used to predict what would
happen when it was, and the prediction is worth keeping because it was followed exactly:

> if audit retention is ever built, it becomes the first legitimate delete path, and
> `AuditLogAppendOnlyTests` will fail. That is the intended behaviour. The fix at that point is to
> make the deletion explicit and narrow, and to update this document — not to widen the test until
> it passes.

What exists now:

- `SystemSettings.AuditLogRetentionDays` — **null by default, meaning keep everything**, and null is
  what every deployment has until an admin types a number on Admin → System Settings. The job wakes
  daily, finds no window, logs one INFO line and deletes nothing. Same explicit-opt-in rule as
  `PiiRetentionWindowDays` and `VeContactRetentionYears`, and for the same reason: nothing in this
  table *has* to be deleted, so the default must be the one that loses nothing.
- **A second sanctioned path was added 2026-08-15** (#188): deleting a user account also deletes
  *that account's own lifecycle rows* — the entries about the user — and writes a fresh entry naming
  the removed email. It is narrow in the way that matters: an account that acted on anything *else*
  is refused outright, with the blockers named, so this cannot erase a record of what somebody did.
  Which of three options to take was an explicit decision, recorded on #188, exactly as this section
  demands.
- `RecordRetentionService` (`Core/Retention`) and `UserManagementService` (`Core/Admin`) are the
  **only** places an audit row is deleted, and `AuditLogAppendOnlyTests` exempts them **by filename** — not by relaxing the forbidden-operation
  list. A *third* delete path anywhere still fails the build, which is the property the guard was
  protecting in the first place. Renaming or moving either file also fails, deliberately: it forces
  whoever moves it to come back here and re-affirm the exemption rather than carry it along
  silently. And the test asserts each exempted file still contains an `AuditLogs` delete, so an
  exemption cannot rot into an open door standing after the room behind it was demolished.
- `RecordRetentionJob` runs it daily. It is **not** folded into `PiiPurgeJob` despite the identical
  cadence — neither table holds personal data, and a run filed under "PII purge" is a run nobody
  finds when they go looking for why audit history disappeared.

The deletion is logged at INFO with its count whenever a window is set. Deleting audit history
silently would be a small betrayal of what the table is for.

**This is a growth control, not a privacy one.** These rows carry no PII by design (see above), so
turning it on buys disk and nothing else — and costs history that cannot be recovered. The
deployment default of "keep everything" is the right one for a table whose value is that it is
complete.

## Source-address recording

Since #265, `AuditLog.SourceIpAddress` is populated for authentication events and PII export only.
See `AuditLog`'s own remarks for why it is deliberately absent from the ~175 ordinary call sites.
