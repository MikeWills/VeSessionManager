# VE Tracking (Phase 7)

What `VolunteerExaminerSyncService`/`VolunteerExaminerReportService`
(`VeSessionManager.Core/VolunteerExaminers/`) do and why.

## Data source: automatic, not manual

The spec left Phase 7's data-entry method open — "manual (via admin backend, Phase 9) or ingested
from ExamTools session data if your library exposes VE roster per session — check during this
phase and use whichever is available." Phase 9 doesn't exist yet, but ExamTools does expose a VE
roster per session, confirmed live against `examtools.dev` on 2026-07-20 (`GET
/api/veUser/sessions/{id}/export/full.json`'s `DEVDOC.VEs` array — see `docs/examtools-api.md`).
So this phase is fully automatic: no waiting on Phase 9, no manual entry step to document.

**Why `export/full.json` and not the session-detail endpoint's `sessionVes` field:** the detail
endpoint (`GET /api/veUser/sessions/{id}`) already returns `sessionVes: [{ve, perm, callsign}]`,
but with no display name — just ExamTools' own internal VE id and callsign. Only `export/full.json`
pairs a callsign with a real name (`DEVDOC.VEs: [{call, name, number?}]`). `number`, when present,
is a VEC-issued VE accreditation number (e.g. ARRL's own VE numbering), not an FCC FRN — it is
deliberately **not** mapped onto `VolunteerExaminer.Frn`, which stays unset until a human enters a
VE's actual FRN by hand (no path for that exists yet either, pending Phase 9).

## VolunteerExaminer belongs to a Team

Not in the original shared data model's `VolunteerExaminer` shape — added because the multi-team
foundation's own design note says VEs belong to a Team (see `docs/multi-team.md`). A VE is matched
during sync by `(TeamId, CallSign)`, not callsign alone, so two different teams can each have their
own record for someone with the same callsign (e.g. a VE who occasionally helps two different
clubs) without collision. `CallSign` is always stored upper-invariant; a future manual-entry path
must follow the same normalization to match correctly.

## Sync is scan-based and fully reconciling, like every other phase

`VolunteerExaminerSyncService.RunAsync(Team, ct)` runs every poll (wired into
`SessionIngestionJob`'s per-team loop, right after ingestion — see `SessionIngestionJob`'s own doc
comment) for every one of that team's non-cancelled `Session` rows:

1. Fetch that session's current roster from `export/full.json`.
2. Find-or-create each roster VE by `(TeamId, CallSign)`. On create: `Name`/`CallSign` set from the
   roster. On an existing match: `Name` is updated if ExamTools now reports something different —
   unlike `Candidate.Frn`'s "never overwrite a manually-entered value" rule, there's no manual-entry
   path for `VolunteerExaminer.Name` yet, so ExamTools stays the single source of truth.
3. Reconcile `SessionVolunteerExaminer` links for that session: add missing links, **remove** links
   for VEs no longer on the roster. This is a real difference from `SessionIngestionService`'s own
   candidate sync (which only ever adds/updates, never removes on disappearance, since a
   withdrawal/no-show is a manual Session Manager action) — a VE roster genuinely can shrink before
   session day if someone's assignment changes, and there's no equivalent "manual removal" action
   to defer to instead. The `VolunteerExaminer` row itself is never deleted, only its link to that
   one session.
4. Cancelled sessions are never touched — their last-known roster is frozen, matching how
   Zoom/Discord/payment state is also left as-is once a session is cancelled.

**No new tracking field was needed** (unlike `ZoomDiscordSyncedStartUtc` or the email `SentUtc`
fields elsewhere) — full reconciliation every poll is correct and cheap at this app's scale (one
`export/full.json` call per active session per ~5-minute tick), and avoids the staleness a
"synced once" flag would introduce for a roster that can keep changing until session day.

## Report: session count per VE

`VolunteerExaminerReportService.GetSessionCountsAsync(teamId, fromUtc, toUtc, ct)` — pure
aggregation, no side effects, no UI yet (Phase 9's admin backend will call this directly once it
exists). Counts non-cancelled sessions only; `fromUtc`/`toUtc` bound `Session.ScheduledStartUtc`
inclusively and either may be `null` for an open-ended range. Results are ordered by count
descending, then name.

**EF Core InMemory gotcha hit while building this:** `OrderBy` chained directly onto a
`GroupBy(...).Select(...)` projection over a join could not be translated by the InMemory provider
("could not be translated... switch to client evaluation"). Fixed by materializing the grouped
counts with `ToListAsync()` first, then ordering the resulting list in memory. Worth remembering
for any future report/aggregation query built the same way.

## Schema

No new tables — `VolunteerExaminer`/`SessionVolunteerExaminer` already existed from Phase 0's
initial migration (they were in the original shared data model). Migration
`Phase7VolunteerExaminerMultiTeam` only adds `VolunteerExaminer.TeamId` (backfilled to the seeded
team's id, `1`, for any pre-existing rows — none existed in practice) plus its FK and the
`(TeamId, CallSign)` unique index.
