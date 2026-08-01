# Historical session import, and the narrowed closed-session sweep

Issue #67, built 2026-07-31. Two halves of one principle:

> **Once a session is completed in ExamTools, there is nothing further to pull about the session
> itself.** Candidate-level updates for that session do still arrive — but through a different path
> (`ExamResultSyncService` per applicant id, and the ULS watcher per FRN), neither of which reads
> the session feed.

So the continuous sweep should be small and only about *discovery*, and pulling real history should
be a deliberate one-off. This document covers the first half; the import itself is documented below
it once built.

## Part 1 — the closed-session sweep is now a discovery net, not a re-reader

`SessionIngestionService` makes one extra call per team per tick:
`GetTeamClosedSessionsAsync(credentials, now - CompletedSessionBackfillWindow, now + 1 day)`.
Closed sessions are invisible to `GetTeamSessionsAsync` (confirmed live 2026-07-28), so this feed is
the only way to see them at all.

Two changes:

**The window narrowed from 30 days to 7.** The 30-day figure came from issue #22 ("pull past
sessions, up to a month old"), which was really a *one-time* need implemented as a continuous one.
Part 2 serves that need properly, so the sweep no longer has to. What remains is genuinely valuable
and cheap: a session that completed while the Worker was down is still discovered and self-heals,
because `GetTeamSessionsAsync` never returns a `"done"` session and `NewSessionPastGrace` means a
still-`"pend"` session more than a day past its start is not first-ingested either.

**A session that is already stored locally *and* already observed closing is dropped from the
merge.** For such a session the loop's only remaining effects were `ApplyRescheduleRules` (meaningless
for a session that already happened) and the `local.ExtId ??= remote.SessionDef?.ExtId` backfill (a
one-time historical fix from 2026-07-30, long since complete). Pure overhead, forever, every tick,
every team.

### The trap in that second change

The obvious implementation — skip any id already stored locally — is **wrong, and reintroduces issue
#68's false cancellations.** A session that is locally known but has *not* yet been seen closed still
needs this feed for two things:

1. **The `ExamToolsClosedUtc` stamp itself.** Only a `"done"` session carries the close signal, and
   only this feed ever returns a `"done"` session. That stamp is what tells the cancellation
   heuristic that a later disappearance from the feed is expected rather than a cancellation.
2. **The final candidate sync.** "Poll while the session is open" deliberately includes the run that
   discovers it closed — that run is the last chance to pick up final candidate changes.

Skip on `known && ExamToolsClosedUtc is not null`, never on `known` alone. That is what
`IsSettledLocally` does, and `KnownButNotYetClosedSession_IsStillReadFromTheClosedFeed` is the
regression test that fails if anyone later "simplifies" it.

Dropping settled sessions from `remoteIds` is safe: cancellation detection already ignores anything
carrying a closed stamp, so their absence cannot read as a disappearance.

### Ordering change

The local `Sessions` query moved *above* the closed-feed merge, because the merge now needs to know
what is already stored. No behavioural consequence beyond that.

### Tests

- `SettledClosedSession_IsSkippedFromTheClosedFeedOnEveryLaterTick` — a settled session whose feed
  entry later reports a changed date and a new `ExtId` picks up neither, and is not cancelled.
- `KnownButNotYetClosedSession_IsStillReadFromTheClosedFeed` — the guard rail described above.
- `DoneSessionWithinBackfillWindow_IsIngested` / `AlreadyEndedSession_DisappearingFromFeed_...` moved
  their fixture dates inside the new 7-day bound.

## Part 2 — the one-time historical import

An admin picks a start and end date on Admin → Team Maintenance; the system does a single pass and
ingests what it finds. The motivating case: it's August, the routine sweep now reaches back a week,
and a full year of history is wanted so the stats page (#63) has something to work with.

### Queued for the Worker, not run in the web request

`HistoricalImportRequest` is a request row. Web writes it `Pending`; `HistoricalImportJob` in the
Worker picks it up on its next tick (default 60s) and processes it. Web and Worker are separate
processes sharing one SQLite file, and this shape buys three things: the admin isn't held on a
spinner through a year of API calls; a browser navigation or an app recycle can't abandon a
half-finished import; and ExamTools polling stays owned by the one process that already owns it,
rather than two hammering it concurrently.

Progress lives on the row (`ChunksCompleted` / `ChunksTotal`, plus running session/candidate counts),
saved after **every chunk** — the same "save immediately after each item" rule every scan-based job
here follows, so a crash mid-import never loses the record of what already landed. The page reads
those counters directly; the per-chunk `JobRunHistory` rows exist for the ops dashboard, not for the
progress readout.

`HistoricalImportJob` peeks with a cheap `HasPendingAsync` before logging anything. Writing a
`JobRunHistory` row for every empty queue check would bury the dashboard under a row a minute and
destroy the "silence means nothing happened" property the other jobs rely on.

This is the one genuinely **event-driven** job in the app — everything else is scan-based — because
the event (an admin asked for a specific range) carries information no amount of scanning could
reconstruct. The idempotency guarantee is unchanged: `Status` is both the "needs action" filter and
the double-processing guard.

### Chunking and bounds

One **calendar month** per `GetTeamClosedSessionsAsync` call, with a 2-second pause between chunks.
Calendar months rather than 30-day blocks so boundaries match how anyone would describe the range in
a log line or a progress readout. A year in one request is a heavy unbounded ask of someone else's
servers and gives no progress signal; a month is comfortably within what the closed feed already
serves on every routine tick.

**No cap on how far back a range may reach.** The chunking and the pause are what protect ExamTools,
not an arbitrary limit on what an operator may ask for. The only range rejections are incoherent
ones: end before start, or a start in the future (which could only ever return nothing).

**One import at a time per team.** Two concurrent imports would interleave writes to the same
sessions and double the load for no benefit. Ingestion is idempotent so the *result* would still be
correct — but the throttling intent would be lost.

### What it does NOT do

**Sessions, candidates and VE roster only.** No Square payment links, no Zoom/Discord events, no
emails.

`SessionEventSchedulingService` and `CandidateNotificationService` both check `Session.HasEnded`, so
most of that would be suppressed anyway — but relying on those guards as the *sole* defence for a
year of backdated data is the wrong posture. Generating live checkout links for sessions that
finished in March, or emailing "you're registered!" to someone who tested and passed months ago, is
the most embarrassing failure mode available here. So those steps are never invoked at all, and the
`HasEnded` guards stay as the backstop they were designed to be. (Issue #22 deliberately left
payment-link generation and VE sync running for backfilled sessions when the window was 30 days;
re-confirmed for a *year*, payments are now out and VE sync stays in.)

VE roster sync runs **once after all chunks**, not per chunk — it reconciles every Active session for
the team in a single pass, so running it per chunk would repeat the same work N times.

### `ImportHistoricalRangeAsync` is not `RunAsync`, and must not become it

The import path is a separate method on `SessionIngestionService`, and collapsing the two would be a
serious bug:

- **`RunAsync` cancels sessions that vanish from the feed.** The import's feed is filtered to one
  date range, so *every session outside that range is absent by construction*. Running the
  cancellation pass there would mark a team's entire live schedule `Cancelled`.
  `ImportNeverCancelsSessionsOutsideTheImportedRange` is the regression test.
- **No reschedule handling and no `ExtId` backfill.** The import only ever *creates* sessions that
  are missing and touches nothing that already exists. A historical feed disagreeing about a stored
  session's time is not a reschedule to act on months later.
- **Candidates sync only for sessions the import actually creates.** An already-imported session is
  skipped whole. That makes re-running a range cheap, and — more importantly — keeps
  `WithdrawMissingCandidates` away from historical rosters, where a short or empty export from a
  long-finished session would irreversibly clear real candidates' PII.

Imported sessions are stamped `ExamToolsClosedUtc` immediately (they came from the closed feed, so
they are closed by definition), which puts them in the same shape the continuous path eventually
produces and keeps the routine sweep's cancellation heuristic away from them.

A failed chunk marks the request `Failed` and keeps everything earlier chunks imported; re-queueing
the same range resumes rather than duplicating.

## Companion fix: VE roster sync no longer re-polls finished sessions

Not in issue #67, but a blocker for it. `VolunteerExaminerSyncService` synced **every Active session**
for a team, every tick. So every session a team had ever ingested was re-polled, one API call each,
hourly, permanently. Tolerable while ingestion only reached ~30 days back; importing a year would
have turned it into a standing six-figure-a-month cost against ExamTools' servers.

**`Session.Status` is not the "is this session over" signal, and reading it as one is the whole
bug.** `Status` stays `Active` forever unless a human clicks Mark completed. The UI has read
ExamTools-closed sessions as "Completed" since issue #71 — but that label is *derived*
(`TestingCompletedUtc ?? ExamToolsClosedUtc`), never written back to `Status`. This query was the one
place in the app where a finished session still looked open, which is exactly why the behaviour was
easy to believe already fixed. It had not changed since Phase 7 (`de3288f`).

A session is now skipped when it's done **and** already has a roster. Three ways to be done:

| Signal | Meaning |
|---|---|
| `ExamToolsClosedUtc` | ExamTools says it's closed — the authoritative signal, and it can arrive *before* the scheduled end time |
| `TestingCompletedUtc` | A Session Manager marked it completed |
| `HasEnded` | Backstop for sessions that carry neither stamp and never will: those ingested before `ExamToolsClosedUtc` existed, and any session ExamTools drops without reporting "done" |

**The roster check is not redundant.** VEs are assigned before or during a session, never after, so a
finished session *with* VEs recorded really is finished. But a session that appears and closes inside
a single polling interval would otherwise be skipped before its roster was ever fetched — losing it
permanently. An empty roster keeps being retried, so a sync that failed at the time self-heals.

Tests: `SessionExamToolsHasClosed_IsNotRePolled_EvenBeforeItsScheduledEnd`,
`SessionAManagerMarkedCompleted_IsNotRePolled`, `FinishedSessionWithARoster_IsNotRePolledForever`
(the no-stamp backstop), `FinishedSessionWithNoRoster_IsStillRetried`.

## Schema

Migration `HistoricalImportRequests` — one new table, no changes to existing columns, so the
down-migration is a clean `DROP TABLE` with no data-loss path for pre-existing data.

