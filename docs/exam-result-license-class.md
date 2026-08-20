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

See `ExamResultSyncService.ResolveLicenseClasses`. **Fed only the elements that were passed** — see
the next section for why that sentence is load-bearing.

## Pass and fail are decided by what was passed (corrected 2026-08-09)

Until 2026-08-09 the outcome was decided by `gradedExams.Any(e => !e.Passed)`: *any* failed graded
element made the whole sitting a failure. That is wrong, and it was wrong for the most ordinary thing
that happens at an exam session.

**Reported live on John Davey at HRCC.** He sat Element 2 and Element 3, passed the Technician,
missed the General — so he walks out a newly licensed Technician and FCC will issue him a call sign.
The app recorded him as **Failed, with no license class at all**. The same logic also broke the
retake-in-one-sitting case Mike described: fail Element 3, sit it again, pass, and the failed attempt
still poisoned the result.

The rule now: **a failed element only matters when nothing passed.**

- Any element passed → `Tested`, and `ResolveLicenseClasses` runs over **the passed elements only**.
  A failed reach at a higher class cannot drag the earned class up, and a failed lower element cannot
  drag it down.
- Nothing passed → `Failed`, exactly as before.

Reaching above your current class and missing is a normal, expected outcome — the table above already
described a candidate who walks in holding something, and this is how they get there.

### Why it needed a repair path, not just a fix

The bug was self-protecting. `Failed` was a **permanent exclusion** from the scan, so the candidates
it harmed were precisely the ones a corrected version would never look at again. Fixing the logic
alone would have left every existing victim wrong forever.

So `Failed` is no longer a permanent exclusion. A candidate the *app* auto-failed
(`ResultMarkedByUserId is null`, and no license class was ever resolved) is re-examined on the next
poll, and if they passed something the status is cleared back to `Unmatched` — the state a passing
candidate would have been left in — so `UlsWatcherService` picks them up and carries them on to
`Received`/`Granted` like anyone else. Audited as `CandidateAutoFailedCorrected`, counted separately
from a fresh result so a repair is never misread as one on the ops dashboard.

Three things keep that from turning into churn:

1. **A human `Failed` verdict is still final.** `ResultMarkedByUserId` is what distinguishes the two,
   and a Session Manager who marked someone failed is not overruled by a feed.
2. **`ApplyResult` is idempotent for a genuine failure** — already `Failed` and still failing writes
   no audit entry and increments no counter. Without that, re-polling would bury the real entries
   under 14 days of identical noise.
3. **The re-poll is bounded by `ResultSyncWindow`.** A genuinely failed candidate costs one
   applicant-detail call per poll while their session stays inside the 14-day window, then never
   again.

**Sessions older than the window need the Session Detail refresh button** (`SyncSessionAsync`, which
has no window bound by design). Seven candidates were affected at the time of the fix, all HRCC; two
were inside the window and self-healed, the rest needed the button. Only those who actually passed
something change — for the others the re-poll confirms the original verdict and writes nothing.

## Backfill for existing candidates

`ExamResultSyncService`'s scan used to stop looking at a candidate forever once `Tested` was true.
That's still true for the withdrawn path (`NotTested`) and for a human-entered `Failed`, but **no
longer for an auto-`Failed` candidate** — see the correction above, which is exactly the assumption
that had to be undone. The query also re-includes any already-`Tested`,
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
`_AppLayout.cshtml`) in response to a feature request — a team-wide (not per-session) worklist for
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

## A partially-graded sitting froze the class too low (corrected 2026-08-20, #437)

**Reported live on Chang Sun at HRCC.** ExamTools showed Elements 2, 3 and 4 all passed — Extra —
while the app recorded `Unlicensed → General` and would never revise it.

The class used to be written under `if (NewLicenseClass is null)`: once, from whatever elements were
graded at the moment of the first poll that saw any graded element, and never again. **ExamTools
grades element by element as VEs enter results**, so a poll landing after E2 and E3 were entered but
before E4 recorded General permanently. `ResolveLicenseClasses` was correct throughout — `{2,3,4}`
→ Extra. The bug was entirely in *when* it was allowed to run.

Two guards had to change, and the second is why nobody noticed sooner:

- The write guard, now `LicenseClassRevision.ShouldReplace` — **revise upward, never downward.**
- The scan filter, which stopped fetching a Tested candidate who already had a class **at all**, so
  the later element could not be observed even in principle. It now keeps re-reading until
  `ExamToolsClosedUtc` is set.

**Why upward-only is safe rather than merely convenient:** within one sitting the class can only go
up, because a VE team never re-administers an element a candidate already holds credit for — the same
premise this whole page rests on for deriving class from elements at all. So the protection the
original guard was written for (a feed must not overwrite a recorded result) is kept intact in the
direction that matters, and a partial re-read, an amended paper or a re-examination can never demote
anybody.

⚠️ **The worse half was never the display.** `UlsWatcherService` confirms an upgrade only when
`lookup.OperatorClass == candidate.NewLicenseClass`. A class frozen too low never matches what FCC
reports, so an **upgrading** candidate would never reach `Granted` and would sit pending
indefinitely — the same failure that file documents for 20 real candidates, reached through a
different door. A first-time licensee like Chang Sun is spared it, because `isNewLicense` matches on
grant date regardless of class.

⚠️ **Counting had to move with it.** `CandidatesBackfilledLicenseClass` used to key off "is this
candidate already Tested", which was safe only because such a candidate was never re-read. Once an
open session is re-read every tick, that counter reported a backfill every tick for every settled
candidate. It now counts only when something was actually written. An existing test caught this
before it shipped.

### The closed-session bound shut the repair route (same day, follow-up)

Mike asked the obvious next question — *would a manual refresh fix Chang Sun?* — and the answer as
first shipped was **no**, which made the fix unable to repair the case that prompted it.

`SyncSessionAsync` (the "Refresh now" path) removes the 14-day `ResultSyncWindow`, but it calls the
same `SyncSessionCandidatesAsync` and so inherited the same per-candidate gate. Chang Sun is Tested,
has a class, and their session is finalized in ExamTools — so all three clauses were false and they
were skipped on both paths.

**A class frozen too low is almost always noticed after the session is finalized**, by somebody
comparing the app against ExamTools. That is how this one was found. So the bound was exactly wrong
for the population that needs repairing, and `SyncSessionAsync`'s doc comment promising an "escape
hatch" was false for the second time in its life.

`includeSettled` now lifts the settled gate on the human-triggered path only. The scheduled scan is
unchanged, so a finished session still costs nothing; a person pressing Refresh pays one
applicant-detail call per candidate, once, which is what they asked for. What it deliberately does
**not** lift: `NotTested`, and a human `Failed` verdict. "Re-read everyone" means everyone the feed is
allowed to speak for — overruling a Session Manager's verdict by pressing a button would be a much
easier accident than the scheduled job doing it.

