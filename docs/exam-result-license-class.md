# License class tracking (initial → new)

Added 2026-07-29, prompted by two related questions: whether a *passed* candidate ever gets a
visible "done testing" signal the way a failed one does, and whether the app could show what license
class a candidate held walking in versus what they earned walking out.

## "Did passing candidates get a finished status too?"

Yes, already — `ExamResultSyncService`'s passing branch always set `Candidate.Tested = true`,
independently of the failing branch setting `ApplicationStatus = Failed`. `ApplicationStatus`
deliberately stays at `Received` for a pass (not some new "Finished" value) because a pass still
needs the FCC ULS watcher to confirm the actual `Granted` transition days later — `Tested` and
`ApplicationStatus` are two different axes on purpose (see `Candidate.Tested`'s own doc comment).
Both the session Detail page and the CandidateDetail page already render `Tested` as a separate
✓/· indicator alongside the status label, so a passed-but-not-yet-granted candidate reads as
"Received" + a checked "Tested" box. No enum/behavior change was needed here — just confirmed and
documented, since the distinction wasn't obvious from the UI alone.

## Initial and new license class

`Candidate.InitialLicenseClass`/`NewLicenseClass` (nullable `LicenseClass`: `None`/`Technician`/
`General`/`Extra`) are derived entirely from which exam elements ExamTools reports as graded+passed
in a sitting — **not** from the FCC ULS `AM.dat` operator-class field, which this app has never
fetched (only `HD.dat`/`EN.dat` are parsed — see `FccUlsRecordParser`). Pulling `AM.dat` would have
meant a new daily-file download, a new USI join, and a timing problem (capturing the class *before*
FCC processes the exam's upgrade, which this app has no reliable way to observe directly).

Instead, the elements graded this sitting are sufficient on their own: a VE team never re-administers
an element a candidate already holds credit for. Concretely, with Element 2 = Technician, 3 = General,
4 = Extra (Element 1/Morse code retired 2007):

- Lowest element passed this sitting − 1 → the class already held walking in (`InitialLicenseClass`).
  E.g. lowest passed = 4 → walked in with General (Element 2+3 credit already established).
- Highest element passed this sitting → the class earned walking out (`NewLicenseClass`).

| Elements passed this sitting | Initial | New |
|---|---|---|
| {2} | None | Technician |
| {2,3} | None | General |
| {2,3,4} | None | Extra |
| {3} | Technician | General |
| {3,4} | Technician | Extra |
| {4} | General | Extra |

See `ExamResultSyncService.ResolveLicenseClasses`. Only set on a full-pass sitting — a candidate with
any failed graded element is marked `Failed` and never gets a license class (they didn't earn one).

## Backfill for existing candidates

`ExamResultSyncService`'s scan used to stop looking at a candidate forever once `Tested` was true.
That's still true for the failed/withdrawn paths (`Failed`/`NotTested` are excluded permanently — a
failed sitting never earns a class), but the query now also re-includes any already-`Tested`,
non-`Failed` candidate still missing `NewLicenseClass` — covering every "current, past, and future"
candidate the field didn't exist for yet, including already-`Granted` candidates from long-closed
sessions. This follows the same idempotent-field-as-query-filter pattern as every other job in this
app (see CLAUDE.md's Established Patterns): once `NewLicenseClass` is set, the candidate is never
refetched again, so the backfill is self-limiting and needs no separate one-off migration script. A
future candidate simply won't have a `NewLicenseClass` until they're actually tested and pass, exactly
as expected.

## Display

`CandidateDetail.cshtml` shows a "License class" row (e.g. "Technician → General") whenever both
fields are set. Deliberately not added to the session Detail page's already-dense candidate table —
that page shows many candidates at once and the per-candidate detail page is the natural home for it.

## Applicant Status page

Built 2026-07-29 (`Pages/SessionManager/ApplicantStatus.cshtml(.cs)`, nav link added to
`_AppLayout.cshtml`) per the TODO.md feature request — a team-wide (not per-session) worklist for
tracking who's still waiting on the FCC, since the per-session Detail page only shows one session at
a time and nothing else surfaced this across the whole team.

Two sections on one page, both team-scoped the same way `VeRoster.cshtml.cs` is
(`SessionAccessScope.TryResolveViewableTeamId`, a team `<select>` for a multi-team user):

- **Pending FCC grant** — every candidate with `Tested = true` and `ApplicationStatus` still
  `Unmatched` or `Received` (i.e. passed, but not yet `Failed`/`NotTested`/`Granted`), sorted by
  how long they've been waiting (`ApplicationDateEnteredUtc`, falling back to `DateRegisteredUtc`
  for a candidate the FCC watcher hasn't matched to an application yet). Shows the same
  `InitialLicenseClass → NewLicenseClass` line as the candidate detail page — already computed by
  `ExamResultSyncService` by the time a candidate lands here, no extra lookup needed. **A candidate
  drops off this list the instant `FccUlsWatcherService` flips them to `Granted`** — per the
  original request, nobody needs to keep tracking them once they're done.
- **Recently issued** — candidates `Granted` with `LicenseGrantDateUtc` in the last
  `ApplicantStatusModel.RecentlyIssuedWindowDays` (7, not configurable yet — the request called it
  "maybe a week," not a firm number) — lets a Session Manager actually confirm a specific person's
  license/upgrade came through before they age out of Pending for good. Kept as a separate section
  rather than merged into Pending, which stays strictly "not yet granted."

No new backing fields — both sections are plain filters over `Candidate`/`InitialLicenseClass`/
`NewLicenseClass`, all of which already existed for the candidate detail page above.
