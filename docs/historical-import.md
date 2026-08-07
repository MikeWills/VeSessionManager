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

### An abandoned `Running` request is reclaimed and resumed (2026-08-03)

A clean *failure* was always handled. A **restart** was not: `RunNextPendingAsync` flipped the row to
`Running`, only `Pending` rows were ever selected again, its catch filter deliberately excludes
`OperationCanceledException`, and nothing anywhere reset a stale row. So a graceful shutdown
mid-import — a deploy, a `systemctl restart`, a crash — left the request `Running` **forever**, and
because `QueueAsync`'s one-at-a-time guard counts `Running`, that team could never queue another
import again. Hand-editing the database was the only recovery, and a deploy window is precisely when
it happens (audit finding T11).

Both `HasPendingAsync` and `RunNextPendingAsync` now also select a request that has been `Running`
longer than `StaleRunningThreshold` (30 minutes). The two must keep using the same predicate — the
Worker calls `HasPendingAsync` first as a cheap peek, so a reclaimable request that only the second
method recognised would never be looked at.

The threshold is generous on purpose, because the failure is asymmetric: re-running is idempotent,
while reclaiming too eagerly only wastes ExamTools calls. And it cannot collide with a live run —
one Worker runs per deployment and processes requests one at a time.

**Resuming picks up at the interrupted chunk**, via `Chunks(...).Skip(request.ChunksCompleted)`. The
counter is incremented only after a chunk's import returns, so the first chunk not skipped is exactly
the one that was cut off. Without this the reclaim would re-walk the whole range: every earlier chunk
re-fetched from ExamTools for nothing, and the progress counters climbing past `ChunksTotal` to show
a nonsense "15/12" on the admin page. A fresh `Pending` request skips zero, so the normal path is
unchanged.

### Imported sessions are marked submitted to the VEC (2026-08-01)

Sessions arrived with `VecSubmissionStatus` at its default, `NotSubmitted`. That is wrong for
backdated data: the VEC paperwork for a session six months ago was filed at the time, outside this
app. Importing half a year therefore dumped the entire range into the submission tracker as though
it were outstanding work — with `VecSubmissionService.MarkSubmittedAsync` being a per-session action
off the Detail page, that's one manual click each. Reported the day after the first real import.

The import now marks each session in the range Submitted, credited to the
`HistoricalImportRequest.RequestedByUserId` admin, with an audit entry worded to make the provenance
obvious: *"auto-marked … by historical import (predates tracking in this app)"*. A reader must be
able to tell an assumption from a Session Manager's confirmation.

Three rules this follows, each with a test:

- **Marking happens outside the create branch.** An import skips a session it already has, so if the
  marking only ran on creation, a range imported before this existed would stay `NotSubmitted`
  forever and re-running would fix nothing. Re-running the range is the supported way to clear such
  a backlog — which is exactly how the first six months of real imported data got fixed.
- **An already-`Submitted` session is left completely alone** — original date and user preserved,
  mirroring `MarkSubmittedAsync`'s own rule. A re-run must never reassign credit for a submission a
  person actually recorded.
- **The routine poll never does this.** Only the historical path may assume paperwork was filed;
  a session ingested normally is genuinely outstanding until someone says otherwise.

The assumption is worth stating plainly: **the import asserts that everything in the range was
already submitted.** For a genuinely historical range that is true by construction. Import a range
that overlaps recent, not-yet-submitted sessions and you will mark them submitted when they aren't —
keep the end date behind your real submission backlog.

## Companion fix 3: payment work is bounded by session age (2026-08-01)

Found the day after the first real import, by reading the Worker log — not by anything failing.

`PaymentGenerationService` filtered on `Session.Status == SessionStatus.Active` and **nothing else**.
Per CLAUDE.md that means *"not cancelled"*, never *"not finished"*, so the import's year of
backfilled candidates all queued up for payment: it created **~1710 Unpaid `InitialExam` payments**
for people who tested months earlier.

They were inert only because that team had no Square credentials. **The first poll after Square was
configured would have generated ~1710 live payment links** for candidates from last winter — and
`PaymentReminderService`, whose queries had the same `Status == Active`-only bound, would then have
emailed them all about it once SMTP was configured. Both were one config change away from firing.

`PaymentEligibilityWindow` (30 days, anchored on `ScheduledStartUtc`) now bounds four queries:
payment creation, Square link generation, reminders, and expiration.

Three things worth knowing about the shape of the fix:

- **A window, not `HasEnded`.** "Has the session ended?" is the wrong test for money — a payment
  reminder keys off `Candidate.ApplicationDateEnteredUtc`, which FCC sets *after* the session runs,
  so reminders legitimately target sessions that already ended. A blanket `HasEnded` guard would
  have broken the real feature. Same reasoning, and same shape, as
  `ExamResultSyncService.ResultSyncWindow`.
- **Anchored on `ScheduledStartUtc`, never `ExamToolsClosedUtc`** — the import stamps the close field
  at *import* time, so anchoring there would make every backfilled session look like it closed today.
  Exactly the trap the exam-result window already documents.
- **Bounding link generation is the half that protects real people.** Guarding creation alone would
  have stopped new bad rows while leaving the ~1710 existing ones live. The decision was to leave
  that data in place rather than mass-delete real rows, which is only safe *because* the link query
  is bounded too. `ExistingUnpaidPaymentOnALongPastSession_GetsNoSquareLink` is the regression test.

## Imported candidates are assumed granted (2026-08-01)

Session-lifecycle rule, decided after the import surfaced the bugs below: **a historical load assumes
everyone was granted.** There is no reason to keep asking FCC whether a licence from one to four
years ago was issued — it either happened long ago or never will.

Left non-terminal, those candidates are polled by `UlsWatcherService` (one HTTP call each, twice a
day, forever) and counted as outstanding on Applicant Status. Marking them `Granted` makes them
terminal, and terminal is already the universal "stop processing" signal across this codebase.

Two limits on what is asserted:

- **Only the status.** `CallSign` and `LicenseGrantDateUtc` stay null, because they were never
  verified. Inventing them would put fabricated licence data in a table other screens read.
- **A candidate the watcher already matched is untouched.** Where `UlsWatcherService` pulled a real
  call sign from ExamTools' ULS API during an earlier run, that candidate is already terminal and
  keeps its real data. In the live backfill all 542 affected rows had no call sign, so nothing
  verified was overwritten.

No per-candidate audit entry: an import writes thousands at once and the audit log is a fixed 200-row
window with no filtering (issue #86). The aggregate lands in `IngestionResult.CandidatesAssumedGranted`
and the import's log line instead.

Applied to existing data 2026-08-01: **542 candidates** (536 `Unmatched`, 6 `Received`; HRCC 524,
MARC 18), backup `vesessionmanager.db.bak-before-assumed-granted-20260801-2230`.

## Companion fix 5: roster retries give up eventually (2026-08-01)

Companion fix 1 (above) settles a finished session only once it **has** a roster, precisely so a
session that appeared and closed inside one polling interval isn't written off before its roster was
ever fetched — "an empty roster keeps being retried, so a sync that failed at the time self-heals."

That is right only while the roster is still plausibly fetchable. Session 819
(`6567ff0cfb29450af7ba19da` — a Mongo ObjectId whose embedded timestamp is **2023-11-30**) came in
via the historical import, and ExamTools returns **HTTP 500** for its roster every single time. So it
never got a roster, never settled, and produced one failed API call plus one `[ERR]` line **every
hour, indefinitely**.

`RosterRetryWindow` (30 days, anchored on `ScheduledStartUtc` for the same reason as everything else
here) now settles a finished session whether or not a roster was ever obtained. Nobody assigns VEs to
a session from two years ago, so an empty roster that old is a fact about ExamTools rather than a
sync worth retrying. Both halves are pinned:
`FinishedSessionOlderThanTheRetryWindow_WithNoRoster_IsNotRePolled` and
`FinishedSessionInsideTheRetryWindow_WithNoRoster_IsStillRePolled`.

## Companion fix 4: the log stopped being unreadable (2026-08-01)

Same root cause, cosmetic symptom. `SessionEventSchedulingService` selected sessions where
`ScheduledStartUtc != ZoomDiscordSyncedStartUtc` and filtered `HasEnded` ones out in memory — but a
past session can *never* satisfy that equality, because it is deliberately never synced. So all 794
backfilled sessions were loaded, filtered and log-counted on **every tick, forever**, with the count
only growing. `CandidateNotificationService` did the same with 1991 candidates. Two INFO lines per
team per tick, drowning every real line.

Both queries now carry a coarse `ScheduledStartUtc >= now - 1 day` bound. A session starting more
than a day ago has certainly ended (durations are hours), so the precise in-memory `HasEnded` check
still sees everything it needs, and the skip counters still report genuinely-just-ended sessions.

Note the counter semantics changed deliberately: a long-past session is no longer *counted* as
skipped, because it is never loaded. `LongPastSession_IsNotEvenConsidered_AndIsNotCountedAsSkipped`
pins that, and an existing test was updated from a 15-day-old session to a 4-hour-old one to keep
covering the "counted as skipped" path.

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

A session is now skipped when it's done **and** has taken its final post-close roster poll. Three
ways to be done:

| Signal | Meaning |
|---|---|
| `ExamToolsClosedUtc` | ExamTools says it's closed — the authoritative signal, and it can arrive *before* the scheduled end time |
| `TestingCompletedUtc` | A Session Manager marked it completed |
| `HasEnded` | Backstop for sessions that carry neither stamp and never will: those ingested before `ExamToolsClosedUtc` existed, and any session ExamTools drops without reporting "done" |

### Finished is not settled — one more successful poll is (amended 2026-08-07)

The original rule retired a session as soon as it was finished and had *any* VE stored, justified on
"VEs are assigned before or during a session, never after". True of the exam, false of the paperwork.
The app polls hourly, so **anything ExamTools records between the last mid-session poll and the close
was simply never seen** — and a mid-session roster is not the final roster. That was invisible while
session detail still offered a manual "+ Add VE"; removing that action the same day (ExamTools is the
only route in now — see `docs/session-manager-ui.md`) made it a real gap.

A longer window was the wrong shape for it. A session is never updated again after it closes, so what
is owed is not *more* polling but **exactly one more poll, after the close**:

`Session.VeRosterFinalSyncedUtc` is stamped only by a roster fetch that succeeded **while the session
was already finished**. The settle rule keys on that stamp instead of on the roster count. So a
session is polled once more after closing — the final update, capturing whatever the last mid-session
poll missed — and then never again.

Three properties fall out of stamping on success only, rather than on "we tried":

- A fetch that throws leaves the stamp null, so the final poll retries by construction instead of
  being written off on one transient ExamTools error.
- A session that appears *and* closes inside a single polling interval has no stamp either, so it
  still gets its roster — the case the roster-count check used to cover.
- A legitimately VE-less session settles, where "has a roster" would have retried it forever.

`RosterRetryWindow` stays as the backstop for a final poll that can never succeed (the HTTP-500 2023
session below). Existing rows migrate with a null stamp, which costs one extra poll each for sessions
inside that 30-day window and nothing at all for older ones — they settle on the retry clause without
an API call.

Tests: `ClosedSession_IsPolledExactlyOnceMore_AndPicksUpALateVe`,
`FinalPollThatFails_DoesNotFinalise_AndIsRetried`,
`SessionAManagerMarkedCompleted_IsPolledOnceMore_ThenSettles`,
`SessionWithNeitherClosedStamp_IsRetiredByHasEnded_AfterItsFinalPoll` (the no-stamp backstop),
`FinishedSessionThatWasNeverFinalised_IsStillRetried`.

## Companion fix 2: exam result sync is bounded by a time window

Issue #81 — same bug class as the VE roster one above, same root cause (`Status == Active` meaning
"not cancelled", not "not finished"), found by auditing the other callers.

`ExamResultSyncService` scanned every Active session whose start had passed. Its per-candidate gate
(`!c.Tested || c.NewLicenseClass is null`, excluding terminal statuses) meant a fully-resolved
session cost nothing — but **any candidate that never resolves was one `GetApplicantDetailAsync` per
tick, forever.** A no-show whose ExamTools record carries no result data is the common case, and
nothing ever moves it to a terminal status.

The import makes it sharper: imported candidates arrive `Tested = false`, so a year of history is a
one-time burst of one call each on the next tick, plus a permanent residue for every one that never
resolves.

Now bounded by `ExamResultSyncService.ResultSyncWindow` (14 days). Results are normally entered the
same day or the next; a session that ran months ago will not start producing new results because we
asked again.

**Anchored on `ScheduledStartUtc`, not `ExamToolsClosedUtc`.** The import stamps the close field at
*import* time, so anchoring there would leave a freshly-imported March session eligible for the full
window and preserve the very burst the bound exists to stop
(`RecentlyImportedButLongPastSession_IsNotPolled_DespiteAFreshClosedStamp`).

### The escape hatch this needed

A window means a session graded later than 14 days would never sync. `ManualCandidateRefreshService`
now runs `ExamResultSyncService` as a `ManualExamResultSync` step — it had been **missing entirely**,
despite that class's doc comment claiming to mirror `SessionIngestionJob`'s pipeline, which has run
this step since 2026-07-28. So "Refresh now" (and the per-session Refresh candidates button) is the
on-demand path. This also required registering `ExamResultSyncService` in the **Web** project's DI,
where it had never been needed before.

## Schema

Migration `HistoricalImportRequests` — one new table, no changes to existing columns, so the
down-migration is a clean `DROP TABLE` with no data-loss path for pre-existing data.

## The VE roster step was a no-op for imported sessions (fixed 2026-08-07)

Reported live: *"I just did a history load but it didn't load the VEs."* Sessions and candidates
imported fine; volunteer examiners did not.

Two features in direct conflict, each correct on its own:

- `VolunteerExaminerSyncService.RosterRetryWindow` (30 days) settles a finished session **whether or
  not a roster was ever obtained**. It exists because a real 2023 session pulled in by an earlier
  import returned HTTP 500 for its roster on every attempt, producing a failed API call and an ERROR
  line every hour, forever.
- A historical import creates sessions that are, **by definition**, older than that window.

So the import's own roster step settled every session it had just created before fetching a single
roster. The step ran, logged nothing unusual, and did nothing.

`RunAsync` gains `ignoreRetryWindow`, set by the historical import and nothing else — the import is
the one caller that knows these sessions are old *on purpose*. The routine hourly path is unchanged
and still protected from the 500-forever case. Same shape as
`ExamResultSyncService.SyncSessionAsync` ignoring its own `ResultSyncWindow` for a session-scoped
refresh.

The hatch skips the *window*, not the settle rule: an imported session is finished, so a successful
roster fetch stamps `VeRosterFinalSyncedUtc` straight away and re-running the import does not
re-fetch it (`OldFinishedSession_AlreadyFinalised_IsStillSkipped_EvenWithIgnoreRetryWindow`).

The other half of the settle rule still applies: a session that already has VEs recorded is skipped
even with the flag set, so re-running an import does not re-fetch rosters it already has.

**Existing imports do not self-heal.** The routine sync still honours the window, so sessions
imported before this fix keep their empty rosters — **re-run the import over the same date range** to
pull them in. That is safe and idempotent: sessions and candidates already present are updated rather
than duplicated, and only the sessions still missing a roster cost an API call.
