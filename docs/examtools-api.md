# ExamTools/HamStudy API Reference

What the Phase 1 ingestion client (`VeSessionManager.Core/ExamTools/`) relies on. Shapes were
verified against real responses on the dev site (`examtools.dev`) on 2026-07-19; runnable
requests live in `api-examples/` (Bruno collection). ExamTools has no published API docs — this
is all discovered behavior, so re-verify if something starts failing after an upstream deploy.

Hosts: `https://examtools.dev` (test) / `https://exam.tools` (legacy production). **Confirmed live
2026-07-28: `https://alpha.exam.tools` is the current real production host** (HRCC's real
credentials/session data live there, verified directly against the API) — this is what
`ExamTools:BaseUrl` should point at for a real team, not `exam.tools`.

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
| `GET /api/veUser/sessions?team={teamId}` | **Only ever returns `state: "pend"` sessions** — confirmed live 2026-07-28 against 40 real HRCC sessions spanning 2024-04-27 to 2026-12-16, every single one `"pend"`, none `"done"`, even years-old ones. See "Closed sessions are a separate feed" below. Fields used: `_id`, `date` (UTC ISO), `vec` (e.g. `"arrl"`), `state`, `applicantCount`, `sessionDef.summary`. `sessionVes` is always `[]` in this list view. |
| `GET /api/veUser/sessions/{startDate}/{endDate}?group=all&team={teamId}` | **The actual source of closed (`state: "done"`) sessions** — `startDate`/`endDate` are `yyyy-MM-dd`. `group=all` means "include every status" (confirmed via the browser UI's own status dropdown: Open/All/Current/Pending/In-progress/Closed — not "every team"), not a synonym for omitting `team`; omitting `team` instead returns every team the login belongs to mixed together (confirmed live: one HRCC-account query without `team=` returned a session with `teamId: "KM6Z-F"` mixed in). `ExamToolsClient.GetTeamClosedSessionsAsync` calls this with a trailing ~30-day window to match `CompletedSessionBackfillWindow`. |
| `GET /api/veUser/sessions/{id}` | Single-session detail; same shape but `sessionVes` populated (`perm: 10` = lead/co-lead) and `sessionDef` gains `city`/`state`/`zip`. **Not** used for VE roster — `sessionVes` entries have `ve` (an internal Mongo ObjectId) and `callsign` but no display name; see `export/full.json` below for the endpoint Phase 7 actually uses. Not currently called by ingestion for any other purpose either. |
| `GET /api/veUser/sessions/{id}/export/basic.json` | `{ session: {date, state}, applicants: [...] }` — the candidate registration feed. Applicant fields used: `id`, `firstname`/`middle`/`lastname`/`suffix`, `email`, `frn`, `has_felony`, `created`. Also available: `pin`, `phone`, `callsign`, `licenseClass`, address fields, `finalized`. |
| `GET /api/veUser/sessions/{id}/applicant/{applicantId}` | Per-applicant detail: everything above plus `status`, `exams[]`, `hasSigned`, `sentEmails{}`. Used by `ExamResultSyncService` for `exams[]` — see "Applicant exam results" below. |
| `GET /api/veUser/sessions/{id}/export/full.json` | `{ DEVDOC: { ..., VEs: [{call, name, number?}], applicants: [...] } }` — used by Phase 7 (`VolunteerExaminerSyncService`) purely for `DEVDOC.VEs`, the only endpoint that pairs a VE's callsign with a real display name (`sessionVes` above has callsign only). `number` (when present) is a VEC-issued VE accreditation number, **not** an FCC FRN — deliberately not mapped onto `VolunteerExaminer.Frn`. The `applicants[]` in this payload are ignored (ingestion already gets candidates from `export/basic.json`); `DEVDOC.applicants[].signingVes` also carries per-candidate VE names, unused for now — Phase 7 only needs the session-level roster, not who-signed-whom. Wrapper key may differ on prod, per the note below. |

## Semantics worth knowing

- **No cancelled state exists.** A cancelled session simply disappears from the team feed; a
  reschedule shows up as a changed `date` on the same `_id`. Phase 1's detection logic is built
  on exactly this (see `SessionIngestionService`).
- **Stale `"pend"` sessions exist.** The team feed can contain sessions years past their date
  still in state `"pend"` (observed live on the dev feed: sessions from 2023/2024). Ingestion
  refuses to first-ingest a `"pend"` session more than a day past its start so downstream phases
  never create Zoom/Discord events for dead sessions.
- **Closed sessions are a separate feed, not a `state` value in the pend list (found live 2026-07-28,
  fixed same day).** Issue #22's original implementation checked `remote.State == "done"` on the
  result of `GetTeamSessionsAsync` — logically correct, but that endpoint **never** returns a
  `"done"` session in real data, so the check could never fire; a real HRCC session from the night
  before was still missing from the local DB after ingestion ran clean with zero errors. Found by
  querying the live API directly and diffing against the browser UI's own session list, which
  showed sessions the ingested feed didn't. Fixed by adding `GetTeamClosedSessionsAsync` (the
  date-range endpoint above) and merging its results into `SessionIngestionService`'s candidate set
  for new-session ingestion, deduped by `_id` against the pend feed.
- **Completed-session backfill (issue #22, 2026-07-28).** A `"done"` session was never first-ingested
  at all before this — teams wanted to start tracking past candidates/VE stats for sessions that
  already happened. `SessionIngestionService` now also first-ingests a `"done"` session (sourced from
  the closed-sessions feed above), but only within a trailing ~30-day window
  (`CompletedSessionBackfillWindow`) — same reasoning as the 1-day `"pend"` grace above: a `"done"`
  session from years ago is exactly as undesirable to backfill as a zombie `"pend"` one. The `"pend"`
  window itself is untouched. Because a newly-backfilled session's scheduled time is already in the past,
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
- `export/full.json` returns a fuller session document (team, VEs, stats, applicants). **Live-verified
  2026-07-20** against a real examtools.dev session: on dev it's wrapped under a `DEVDOC` key,
  `DEVDOC.VEs` is `[{call, name, number?}]`, confirmed as the VE roster source for Phase 7 (see
  `docs/ve-tracking.md`). **Live-verified 2026-07-29 against real prod (`alpha.exam.tools`) data —
  the wrapper key genuinely differs on prod, more than expected: prod doesn't wrap the payload at
  all.** `VEs`/`applicants` sit at the top level instead of under `DEVDOC`, same field shape
  (`[{call, name}]`). This silently meant `VolunteerExaminerSyncService` found zero VEs for every
  real HRCC session (issue #38) — `ExamToolsFullExport.ResolveVes()` now checks both shapes.
- Other discovered-but-untested paths (from the site's JS bundles): `.../applicant/{id}/email`,
  `export/basic` (non-JSON), `vecDownload/*.zip`, `form605.pdf`, `laurel_export.csv`,
  `w5yi_export.csv`.

## Applicant exam results (2026-07-28)

`ExamResultSyncService` closes a gap found live tonight: a real HRCC candidate ("Terrance A Harris")
failed his General exam at a real session, but the app had no idea — `ApplicationStatus` was still
`Unmatched` and `Tested` was still `false`, because nothing had ever told the Session Manager to click
"Mark failed." ExamTools had the graded result the entire time, on an endpoint ingestion had never
called before. Verified live via `GET /api/veUser/sessions/{sessionId}/applicant/{applicantId}`:

```json
{
  "status": "closed",
  "exams": [
    {
      "element": 3,
      "graded": true,
      "total": 35,
      "passing": 26,
      "correct": 23,
      "answered": 35,
      "passed": false,
      "valid": true,
      "startedAt": "2026-07-29T01:59:50.578Z",
      "stoppedAt": "2026-07-29T02:13:28.271Z"
    }
  ]
}
```

`ExamToolsApplicantDetail`/`ExamToolsExamResult` only map `exams[].graded`/`exams[].passed` — the
richer per-exam stats (`total`/`correct`/etc.) and the full registration PII this endpoint also
returns aren't needed by anything today. A candidate can have more than one entry in the same sitting
(passes a lower element, then attempts and fails a higher one) — any graded-and-failed entry is
treated as an overall Failed regardless of other elements passed the same session, since a retest fee
is owed either way. See `ExamResultSyncService`'s own doc comment for the full field-setting/audit
behavior and how it interacts with `PaymentReminderService`'s existing Reason=Retest logic.

## Per-team host override (issue #18, 2026-07-28)

`ExamTools:BaseUrl` (`ExamToolsOptions.BaseUrl`) is the deployment-wide default host, but a `Team`
can now override it via a nullable `Team.ExamToolsBaseUrl` column — e.g. a "dev team" running
against `examtools.dev` for testing while real teams poll `alpha.exam.tools`, all from the same
deployment. Chosen over an alternative `Team.ExamToolsEnvironment` (Dev/Production) enum design
because a free-text override matches the existing pattern every other per-team credential already
uses (nullable `Team` column, direct DB edit, no admin UI dropdown to maintain) and needs no code
change/redeploy if a third ExamTools host ever shows up — an enum would need both.

- `ExamToolsCredentials.For(team, globalDefaultBaseUrl)` is the one place the
  override-falls-back-to-global logic lives; both `SessionIngestionService` and
  `VolunteerExaminerSyncService` build their credentials through it instead of reading
  `Team.ExamToolsBaseUrl` directly.
- `ExamToolsClient` caches one `HttpClient`/cookie-jar pair per team, keyed by `TeamId`. Because the
  base URL can now change per-team at runtime (an admin edits `Team.ExamToolsBaseUrl` after the
  singleton already built a session for that team), `GetOrCreateTeamSession` compares the cached
  session's `BaseUrl` against the credentials' current one on every call and transparently rebuilds
  (disposing the stale `HttpClient`) on a mismatch, rather than requiring a process restart to pick
  up the change.
- Editable on the admin Team Settings page (`Pages/Admin/TeamSettings.cshtml`) alongside the rest
  of the ExamTools credentials — blank clears the override back to the global default.
