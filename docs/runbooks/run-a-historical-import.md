# Runbook — Run a historical import

**When:** backfilling closed sessions and candidates from ExamTools over a date range that the
routine sweep no longer reaches (it looks back about a week).

**Who:** an admin, at **Admin → Team Maintenance**.
**Why it works this way:** [`docs/historical-import.md`](../historical-import.md).

---

## Before starting

- **It is idempotent.** Re-running a range is safe and cheap — sessions already imported are skipped
  whole. That is deliberate, because the failure is asymmetric.
- **One import at a time per team.** A second concurrent import would interleave writes and double
  the load for no benefit.
- **Do not deploy while one is running.** A deploy stops both services mid-import; an abandoned
  `Running` request is reclaimed and resumed by the Worker, but the deploy window is exactly when
  you least want to find out.

## What it does and does not do

**Does:** sessions, candidates, and (once, after all chunks) VE roster reconciliation.

**Does not:** Square payment links, Zoom/Discord events, or **any email**. Those steps are never
invoked at all — not merely suppressed by their `HasEnded` guards, which stay as the backstop they
were designed to be. Generating live checkout links for sessions that finished in March, or emailing
"you're registered!" to somebody who tested months ago, is the most embarrassing failure mode
available here.

Imported sessions are marked **submitted to the VEC** (they were, by hand, at the time) and imported
candidates are assumed **granted**.

## Steps

1. **Admin → Team Maintenance**, pick a start and end date.
   - No cap on how far back the range may reach — chunking and a 2-second pause between chunks are
     what protect ExamTools, not an arbitrary limit.
   - The only rejections are incoherent ranges: end before start, or a start in the future.
2. **Submit.** Web writes a `Pending` request row; the Worker picks it up on its next tick
   (default 60s). You are not held on a spinner, and a browser navigation cannot abandon it.
3. **Watch progress on the page.** `ChunksCompleted` / `ChunksTotal` plus running session and
   candidate counts, saved after **every chunk** — one calendar month per chunk, so boundaries match
   how anyone would describe the range.
4. **When it finishes**, spot-check: a session near the start of the range and one near the end,
   each with candidates and a VE roster.

## Troubleshooting

| Symptom | What to do |
|---|---|
| The request sits `Pending` | The Worker is not ticking — [`worker-not-processing.md`](worker-not-processing.md) |
| It stops partway and the row says `Running` with no progress | An abandoned run is reclaimed and resumed automatically; give it a tick before intervening |
| Some sessions came in with **empty VE rosters** | Sessions imported before the 2026-08-07 roster fix keep their empty rosters. **Re-run the import over the same date range** to fill them |
| A session is stuck in a state re-running does not clear | Re-running the range is the supported way to clear it; hand-editing the database is not |
| Per-chunk rows flooding Job Run History | Expected — those rows exist for the ops dashboard, not for the progress readout. The job peeks cheaply before logging, so an empty queue writes nothing |

## Note on `Status`

An imported session's `Status` stays `Active` forever unless a human clicks Mark completed —
`Status` only ever leaves `Active` on **cancellation**, it is never set to Completed. "Completed" in
the UI is derived at render time from `TestingCompletedUtc ?? ExamToolsClosedUtc`. Do not read
`Status == Active` as "this session has not happened yet"; it means "not cancelled".
