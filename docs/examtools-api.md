# ExamTools/HamStudy API Reference

What the Phase 1 ingestion client (`VeSessionManager.Core/ExamTools/`) relies on. Shapes were
verified against real responses on the dev site (`examtools.dev`) on 2026-07-19; runnable
requests live in `api-examples/` (Bruno collection). ExamTools has no published API docs — this
is all discovered behavior, so re-verify if something starts failing after an upstream deploy.

Hosts: `https://examtools.dev` (test) / `https://exam.tools` (production). An older client
library used `https://alpha.exam.tools` for production — if prod logins fail, try that host.

## Authentication

`POST /api/ve/login` with form-urlencoded fields `username`, `password`, `remember=0`.

- Success sets a session cookie; every subsequent call just carries the cookie jar.
- **The endpoint returns HTTP 200 even for bad credentials.** Failure is an
  `{"error": "..."}` JSON body — check for the `error` key, not the status code.
- Convention carried over from the prior client library: send a `Hello-Richard` header on the
  login request identifying the automation and the account running it, as a courtesy to the
  ExamTools maintainer.
- Expired cookies surface as 401/403 on API calls; the client re-logs-in once and retries.

## Endpoints used by ingestion

| Endpoint | Returns |
|---|---|
| `GET /api/veUser/sessions?team={teamId}` | All sessions for the team. Fields used: `_id`, `date` (UTC ISO), `vec` (e.g. `"arrl"`), `state` (`"pend"`/`"done"`), `applicantCount`, `sessionDef.summary`. `sessionVes` is always `[]` in this list view. |
| `GET /api/veUser/sessions/{id}` | Single-session detail; same shape but `sessionVes` populated (`perm: 10` = lead/co-lead) and `sessionDef` gains `city`/`state`/`zip`. **Not** used for VE roster — `sessionVes` entries have `ve` (an internal Mongo ObjectId) and `callsign` but no display name; see `export/full.json` below for the endpoint Phase 7 actually uses. Not currently called by ingestion for any other purpose either. |
| `GET /api/veUser/sessions/{id}/export/basic.json` | `{ session: {date, state}, applicants: [...] }` — the candidate registration feed. Applicant fields used: `id`, `firstname`/`middle`/`lastname`/`suffix`, `email`, `frn`, `has_felony`, `created`. Also available: `pin`, `phone`, `callsign`, `licenseClass`, address fields, `finalized`. |
| `GET /api/veUser/sessions/{id}/applicant/{applicantId}` | Per-applicant detail: everything above plus `status` (`"reg"`), `exams[]`, `hasSigned`, `sentEmails{}`. Not used yet — likely useful for later phases (ExamTools tracks which emails it already sent). |
| `GET /api/veUser/sessions/{id}/export/full.json` | `{ DEVDOC: { ..., VEs: [{call, name, number?}], applicants: [...] } }` — used by Phase 7 (`VolunteerExaminerSyncService`) purely for `DEVDOC.VEs`, the only endpoint that pairs a VE's callsign with a real display name (`sessionVes` above has callsign only). `number` (when present) is a VEC-issued VE accreditation number, **not** an FCC FRN — deliberately not mapped onto `VolunteerExaminer.Frn`. The `applicants[]` in this payload are ignored (ingestion already gets candidates from `export/basic.json`); `DEVDOC.applicants[].signingVes` also carries per-candidate VE names, unused for now — Phase 7 only needs the session-level roster, not who-signed-whom. Wrapper key may differ on prod, per the note below. |

## Semantics worth knowing

- **No cancelled state exists.** A cancelled session simply disappears from the team feed; a
  reschedule shows up as a changed `date` on the same `_id`. Phase 1's detection logic is built
  on exactly this (see `SessionIngestionService`).
- **Stale `"pend"` sessions exist.** The team feed can contain sessions years past their date
  still in state `"pend"` (observed live on the dev feed: sessions from 2023/2024). Ingestion
  refuses to first-ingest a `"pend"` session more than a day past its start so downstream phases
  never create Zoom/Discord events for dead sessions.
- **Completed-session backfill (issue #22, 2026-07-28).** A `"done"` session was never first-ingested
  at all before this — teams wanted to start tracking past candidates/VE stats for sessions that
  already happened. `SessionIngestionService` now also first-ingests a `"done"` session, but only
  within a trailing ~30-day window (`CompletedSessionBackfillWindow`) — same reasoning as the
  1-day `"pend"` grace above: the feed returns unfiltered full history, so a `"done"` session from
  years ago is exactly as undesirable to backfill as a zombie `"pend"` one. The `"pend"` window
  itself is untouched. Because a newly-backfilled session's scheduled time is already in the past,
  `Session.HasEnded(now)` gates two downstream passes so they don't act on it as if it were live:
  `SessionEventSchedulingService` skips Zoom meeting/Discord event creation
  (`SchedulingResult.SessionsSkippedPastDue`), and `CandidateNotificationService`'s automatic
  `RegistrationConfirmation` scan skips sending a "you're registered!" email for something already
  over (the manual "resend confirmation" admin action is intentionally unaffected — a human
  explicitly clicking resend means it regardless of date). Payment-link generation and VE roster
  sync are deliberately left untouched — a late/retroactive payment and VE-stat tracking are exactly
  what this backfill is for.
- **FRN placeholder:** applicants who register without an FRN come through with an all-zeros
  `frn` (`"0000000000"`). Ingestion maps that to `Frn = null` + `FrnMissingAtRegistration = true`.
- `GET .../applicant` (collection, no id) is a 404 — there is no plain applicant-list endpoint;
  `export/basic.json` is the list. `applicantCount` on the session lets the poller skip the
  PII-bearing export call when nothing is registered.
- `export/full.json` returns a fuller session document (team, VEs, stats, applicants) wrapped
  under a `DEVDOC` key on the dev site — the wrapper key may differ on prod. **Live-verified
  2026-07-20** against a real examtools.dev session: `DEVDOC.VEs` is `[{call, name, number?}]`,
  confirmed as the VE roster source for Phase 7 (see `docs/ve-tracking.md`).
- Other discovered-but-untested paths (from the site's JS bundles): `.../applicant/{id}/email`,
  `export/basic` (non-JSON), `vecDownload/*.zip`, `form605.pdf`, `laurel_export.csv`,
  `w5yi_export.csv`.
