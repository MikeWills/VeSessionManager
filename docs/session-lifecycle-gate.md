# Session lifecycle: a real provenance flag instead of date-window guesses (#88)

## The problem

The historical import (`SessionIngestionService.ImportHistoricalRangeAsync`, issue #67 part 2)
backfills a year of completed sessions in one go. Several jobs need to never act on that backfilled
history — no payment links, no "you owe the FCC a fee" reminders, no license polling, no Zoom/Discord
scheduling — because none of it is real, current work; the sessions already happened, months or years
ago, outside this app's own tracking.

Before this, "was this session backfilled?" was answered by a proxy: how old is it? `PaymentEligibilityWindow`
(30 days from `ScheduledStartUtc`), `VolunteerExaminerSyncService`'s `RosterRetryWindow` (also 30 days),
and a 1-day coarse SQL cutoff in `CandidateRegisteredScanner`/`SessionEventSchedulingService` all asked
some version of "is this session old enough to be suspect" rather than "was this session actually
imported."

**The proxy is wrong in a specific, correctable way**: a real session that just happens to be old —
discovered late, imported from a small gap in polling, or simply from a deployment that's been running
a long time — is indistinguishable from a genuinely-backfilled one under a date window, and gets
wrongly excluded. `PaymentEligibilityWindowTests`' own age-only test cases (now replaced) proved this
by construction: they simulated "a real old session" using nothing but an old `ScheduledStartUtc`, with
no way to also say "and it was never imported."

## The fix

`Session.ImportedHistoricallyUtc` (nullable `DateTime`), stamped in exactly one place —
`SessionIngestionService.ImportHistoricalRangeAsync`, right alongside the existing `ExamToolsClosedUtc`
stamp — and read nowhere except as an exclusion. The routine polling path (`RunAsync`) never touches it.
Null is trustworthy specifically because only one code path can ever set it.

### What now excludes on the flag instead of a date guess

- **`PaymentGenerationService`** — both passes (payment creation, link generation). Replaces
  `PaymentEligibilityWindow` outright; that class is deleted.
- **`FccFeeOutstandingScanner`** — same replacement. In practice this is defense in depth: a
  historical candidate is already terminal (see below) and would never reach this scanner's query
  anyway, but the exclusion is now exact rather than relying on that other mechanism holding.
- **`UlsWatcherService`** — new explicit join filter. Previously rule 1 ("never check licenses for
  historically-imported sessions") held only as a side effect of every historical candidate being
  auto-`Granted` at import time (see `MarkHistoricalCandidatesGranted`); this is the structural
  backstop for whatever gap leaves one non-terminal anyway.
- **`CandidateApplicationStatusExtensions.AwaitingFccGrant`** — the one shared predicate behind
  Applicant Status's Pending list, its nav badge (`NavBadgeCountService`), and the bulk-email screen
  reached from it. Fixed once here, reaches all three call sites at once — the same reason it was
  extracted as a shared predicate in the first place.
- **`CandidateRegisteredScanner`** / **`SessionEventSchedulingService`** — added alongside their
  existing 1-day coarse SQL cutoffs, for exactness. In the typical case the coarse cutoff already
  excludes a real historical import (always many days old), so this mostly matters for the edge case
  of importing a *recent* date range — but the issue asks for the exclusion to be correct regardless
  of how recent the imported range is, not just usually correct.

### What was deliberately left alone

- **`ExamResultSyncService`**'s 14-day `ResultSyncWindow`. This is a *discovery* window — how far back
  the routine sweep looks for a graded result that might have just arrived — not a "never touch this"
  guard. Results can be amended after grading, unlike a closed VE roster, so the same "settle
  permanently past a certain age" treatment would be wrong here. The historical import already has its
  own unbounded escape hatch (`SyncSessionAsync`, no window at all) for exactly this case. Folding
  `ImportedHistoricallyUtc` into this service's routine sweep isn't needed and isn't done.
- **`VolunteerExaminerSyncService`**'s `ignoreRetryWindow` mechanism. The historical import already
  asks for exactly one roster-fetch attempt per imported session (`ignoreRetryWindow: true`), after
  which the session settles under the same 30-day `RosterRetryWindow` every other session uses. This
  is a working, deliberate one-time exception, not a date-window guess standing in for the real flag —
  rewriting it to key off `ImportedHistoricallyUtc` instead risked changing *when* a historical
  session's roster sync gives up, for no benefit this pass needed. Left untouched.

## The correction the new tests pin

Every replaced call site got a new pair of tests, not just one: a session with the flag set is
excluded, and — the actually new assertion — **a session that is simply old, with the flag unset, is
still eligible**. `PaymentGenerationServiceTests.ARealSessionThatIsSimplyOld_StillGetsAPayment` and its
siblings in `FccFeeOutstandingHistoricalImportTests`/`PaymentReminderServiceTests`/`AwaitingFccGrantTests`
all fail against the pre-#88 code for exactly this reason — the old window excluded them by mistake.

## Backfilling existing sessions — a report, not a write

Every session in the database before this migration has `ImportedHistoricallyUtc == null`, including
ones that really were historically imported. Fixing that retroactively needs to identify which
existing `Session` rows came from an import — and there's no clean answer. `HistoricalImportRequest`
records the *request* (team, date range, chunk counts) but has no list of the `Session` ids it created.

Two signals, combined, neither exact on its own:

1. **The audit trail.** `SessionIngestionService.MarkVecSubmitted` writes a `VecSubmissionMarked`
   `AuditLog` row keyed by `Session.Id` whenever the import flips a session's VEC-submission flag.
   Exact when it fires — but silent for a session that happened to already be `Submitted` before the
   import ran (the method's own early return writes nothing in that case).
2. **The creation gap.** The routine sweep never reaches back further than
   `SessionIngestionService.CompletedSessionBackfillWindow` (7 days) from "now." A session whose
   `CreatedUtc` sits well past that window beyond its own `ScheduledStartUtc` could only have arrived
   via a deliberate historical import — a heuristic, not a proof (a self-hoster's clock skew or an old
   restored backup could in principle produce the same shape without ever importing anything).

**Mike's call, asked directly**: dry-run report first, never write blind against production HRCC/MARC
data — a wrong tag silently stops a real session's payment reminders and license checks, which is
worse than the gap this issue exists to close. `--report-historical-imports` (Worker,
`HistoricalImportReport.cs`) lists every un-flagged session either signal matches, with which signal(s)
matched each row, and writes nothing. Reviewing that list and actually applying the backfill is a
separate step this pass does not build — an operator with real context (which teams were ever
historically imported, over what ranges) is the only one positioned to make that call.

Run it from the server, same shape as the other one-off switches:

```bash
sudo -u vesessionmanager env DOTNET_ENVIRONMENT=Production \
  sh -c 'cd /opt/vesessionmanager/worker && exec dotnet ./VeSessionManager.Worker.dll --report-historical-imports'
```
