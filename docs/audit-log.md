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

Growth is unbounded and is **not** this document's subject — it is issue #86, together with the same
problem on `JobRunHistories` (#296). Worth noting the two interact: if audit retention is ever built,
it becomes the first legitimate delete path, and `AuditLogAppendOnlyTests` will fail. That is the
intended behaviour. The fix at that point is to make the deletion explicit and narrow, and to update
this document — not to widen the test until it passes.

## Source-address recording

Since #265, `AuditLog.SourceIpAddress` is populated for authentication events and PII export only.
See `AuditLog`'s own remarks for why it is deliberately absent from the ~175 ordinary call sites.
