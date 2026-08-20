# Worker resilience: one failure must not stop every job

Audit findings T08 and T10, both fixed 2026-08-03. Two independent problems, related only in that
each came from the Web and Worker processes sharing one SQLite file and one code path.

## T10 — a transient database error could stop the whole Worker

### The failure

.NET's default `BackgroundServiceExceptionBehavior` is `StopHost`: anything escaping a
`BackgroundService.ExecuteAsync` terminates the **entire host**, not just the job that threw. This
repo already knows that rule — CLAUDE.md's Known Constraints record the 2026-07-21 incident where an
unconfigured Square credential thrown from a *constructor* killed ExamTools, Zoom and Discord polling
along with payment generation. The constructor rule was applied and has held.

But the rule only ever covered construction. Every job also does real work in its tick *before*
`JobRunHistoryLogger` (which catches exceptions from the job body) is involved:

- `SessionIngestionJob` — loads `SystemSettings` and the full team list, and stamps
  `LastIngestionRunUtc` at the end of each team
- `PerTeamDailyJob` — loads the team list
- `UlsWatcherJob` — reads settings and queries `JobRunHistories` for the current slot
- `HistoricalImportJob` — peeks the request queue

Web and Worker share one SQLite file, so any of those can fail transiently with "database is locked".
Before this fix, one such failure — at any of those points, in any job — stopped **every** job in the
Worker permanently, until somebody noticed and restarted the service. The symptom is the worst kind:
the Worker is simply gone, with one error line and then silence.

A second, subtler path led to the same place. `JobRunHistoryLogger` shares its scoped `DbContext`
with the job's own services. When a job failed partway through a `SaveChangesAsync`, the entity that
caused the failure was **still tracked** — so the logger's `finally` save attempted it again, threw
the same error, and escaped through the finally block. One team's bad row became a full Worker
outage. Its start-row save was unprotected for the same reason.

### The fix

**`JobTick.GuardedAsync`** (`src/VeSessionManager.Worker/JobTick.cs`) wraps one iteration of each
job's timer loop. A failed tick is logged and abandoned; the loop continues. That is safe precisely
because every job here is scan-based and idempotent — the next tick re-derives whatever this one
missed, which is the same property that makes a missed tick harmless. `OperationCanceledException`
is rethrown rather than swallowed, so shutdown still stops the loop promptly.

Two jobs used `continue` at tick level to skip work (`HistoricalImportJob`'s empty-queue peek,
`UlsWatcherJob`'s already-ran-this-slot check). Inside the guarded delegate those became `return`,
which has identical meaning — end this tick, wait for the next — but `continue` will not compile
across a lambda boundary, so this is a change to watch for if either is edited.

**`JobRunHistoryLogger`** was hardened in the same pass:

- The start-row save is wrapped. If it fails the job still runs — losing a dashboard row is
  emphatically better than skipping the work — and a flag records that there is no row to complete.
- The completion save clears the change tracker **on the failure path** before re-attaching the
  history row, so a poisoned entity left by the failed job is dropped rather than retried. The save
  is itself wrapped, so even that cannot escape.

Note the layering: the tick guard alone would already prevent a host stop, since the logger runs
inside it. The logger hardening buys something different — it keeps *one team's* failure from
aborting the remaining teams in the same tick.

## T08 — the Web/Worker race could create two payments for one candidate

### The failure

`PaymentGenerationService` decides whether to create a payment with
`!c.Payments.Any(p => p.Reason == InitialExam)`. Web (a manual refresh) and Worker (its scheduled
tick) can both run that pipeline for the same team, and both can evaluate that check before either
one saves — so both conclude no payment exists and both create one. Nothing in the schema prevented
it: `Payments`' only unique index was on `YouthConfirmationToken`.

The consequence is not cosmetic. Two Unpaid rows both get real Square checkout links, and the
candidate later receives two payment reminders. Money can move twice.

Narrowing the session-Detail refresh to one session (see `docs/team-maintenance.md`) shrank the
collision surface but did not remove it — Team Maintenance's team-wide refresh still exists, and one
session is enough for a collision on that session's candidates.

### The fix

A **filtered unique index** on `(CandidateId, Reason)` where `Reason = 0`, in
`AppDbContext.OnModelCreating` and migration `UniqueInitialExamPaymentPerCandidate`. Filtered because
a **Retest payment legitimately repeats** — a candidate may sit and pay for several. The filter
expression is built from `(int)PaymentReason.InitialExam` rather than a literal `0`, so it cannot
drift if the enum is ever renumbered (which is itself an open audit item, T21).

That converts an invisible double-charge into a caught constraint violation. To make the violation
survivable, payment creation now **saves per candidate** instead of as one batch at the end: with a
single batch save, one collision would roll back every other candidate's payment in the same pass.
The `DbUpdateException` is caught per candidate, the failed entity is **detached** (a failed entity
stays tracked and would be retried by every later save in the pass, including
`JobRunHistoryLogger`'s), and the loop continues.

**The catch then asks the database what actually happened** rather than assuming. Catching
`DbUpdateException` alone would lump two very different causes together: the expected uniqueness
collision, and a transient "database is locked" — entirely realistic here, since Web and Worker share
one SQLite file. Both are survivable, and the next tick retries either way, so this is purely about
diagnosis: reporting lock contention as "the other process already created it" would send someone
hunting a race that isn't happening. A single follow-up query decides, and the two outcomes are
counted separately (`PaymentsSkippedAlreadyExisted` vs. `PaymentsFailed`) and logged at different
levels. Deliberately no SQLite error codes, so this survives a change of database.

### The migration deletes duplicates first, and only the provably inert ones

`CreateIndex` throws if the table already violates the constraint — and both Web and Worker call
`Database.Migrate()` at startup, so a failure here is not "a migration didn't apply", it is "the
deployment will not boot". A database that ran the buggy code may well contain duplicates.

So the migration first deletes duplicate InitialExam rows that are **provably inert**: same
candidate, still `Unpaid`, and never given a Square link or order id. The oldest row per candidate
(`MIN(Id)`) is always kept.

It is deliberately *not* a blanket dedupe. If a duplicate was ever linked or paid, it stays, and
`CreateIndex` fails loudly — because that case means two live checkout links existed for one
candidate and money may have moved twice. A human has to look at that, and silently deleting the
evidence would be the wrong call.

Verified by applying the migration to the real development database (three teams, ~1700 payments):
it applied cleanly, and the Worker came up and ran every job normally afterwards.

## Testing note

The unique index and its filter **cannot be verified on the EF InMemory provider**, which enforces
neither. Those tests use a real `DataSource=:memory:` SQLite context, following
`VecExamToolsCodeSqliteTests` — the same rule CLAUDE.md's Known Constraints already state for
provider-dependent behaviour.

---

# Naming contention, so the "is SQLite still right?" question has a graph (#434, 2026-08-20)

Follow-up to [#403](https://github.com/MikeWills/VeSessionManager/issues/403), which asked whether
this app has outgrown SQLite and concluded **no — and size will never be the reason**. The database
is single-digit megabytes against a multi-terabyte ceiling. The real constraint is that Web and
Worker are two processes sharing one file, so only one may write at a time.

That analysis listed rising lock failures as the trigger to revisit the decision, and then ran into
the problem this change fixes: **contention was handled in three places and named in none.**

| Site | Then | Now |
|---|---|---|
| `JobTick.GuardedAsync` | Logged one Error naming the job | Same, plus *which kind* of failure |
| `JobRunHistoryLogger` (start row, completion row, job body) | Logged an Error | Same, plus the cause — and the start row is now timed |
| `PaymentGenerationService` | Already distinguishes a lock from the known uniqueness race by re-querying | Unchanged |

**Every existing behaviour is identical.** Faults are still swallowed, jobs still run when their
bookkeeping write fails, the host still survives, nothing retries. The change is wording plus one
timer — which is what makes it safe to put on the path that every job step takes on every tick.

## The finding that shaped it: `IsTransient` is a lie on SQLite

The obvious implementation is `DbException.IsTransient`. It is documented as covering "failure to
acquire a database lock", explicitly so that retry strategies can be written *"without knowledge of
specific database error codes"* — precisely the provider-agnostic abstraction #434 asked for, and
precisely what keeps a future move to MySQL/Postgres cheap.

⚠️ **`Microsoft.Data.Sqlite.SqliteException` does not override it.** Verified against the real
driver, both by constructing `SqliteException("…", 5)` and by provoking an actual `SQLITE_BUSY` with
two connections: a genuine locked-database error inherits `DbException`'s base implementation and
reports **`IsTransient == false`**.

A classifier written on `IsTransient` alone would compile, read correctly, pass review, and **never
once fire on the only provider we run** — instrumentation that silently measures nothing, which is
worse than none, because the resulting flat graph would read as "no contention".

So `DatabaseContention.IsContention` asks the portable question **first** (a future provider needs
no change here) and falls back to SQLite's own `SQLITE_BUSY`/`SQLITE_LOCKED` codes only because this
provider declines to answer it. `DatabaseContentionTests` pins the finding: if a future package
version implements `IsTransient`, that test fails and the fallback can be deleted rather than left
to rot.

The fallback is an acceptable coupling *because this is instrumentation, not control flow*. Nothing
retries or behaves differently on the answer — a misclassification mislabels a log line. Had this
driven real decisions, the SQLite branch would have been the wrong trade.

## The signal that actually moves first

Not failures — **successful writes that took too long**.

Microsoft.Data.Sqlite retries a busy database internally until the command timeout,
[30 seconds by default](https://learn.microsoft.com/dotnet/standard/data/sqlite/database-errors),
and the connection strings set only `Data Source=`, so that default is in force. A save that waited
twelve seconds and then succeeded raises no exception and logs nothing: it is indistinguishable from
an instant one. **By the time contention surfaces as a failure it has been getting worse, unseen,
for a long time.**

`JobRunHistoryLogger` now times its start-row write and logs a Warning past
`DatabaseContention.SlowWriteThreshold` (1 second). Local SQLite writes are single-digit
milliseconds when uncontended, so a second-scale one did not do more work — it sat waiting. That
write happens on every job step, for every team, on every tick, which makes it the best sampling
point available without instrumenting every query.

## What this deliberately does not do

- **No new table, no counter store.** Logs and the existing dashboard are enough to see a trend;
  a metrics pipeline is a bigger decision than this earns.
- **No `Command Timeout` change.** It is a real untouched knob (#403 lists it as a cheap step before
  any engine move), but raising it *before* measuring would hide the very signal this adds.
- **No retry.** Every job here is scan-based and idempotent; the next tick re-derives what this one
  missed. That is the existing design and it is still right.
- **No engine change.** #403 settled that. This is what tells us when to reopen it.
