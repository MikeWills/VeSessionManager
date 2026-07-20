# ARRL Submission Tracker (Phase 8)

What `ArrlSubmissionService`/`ArrlSubmissionReportService`
(`VeSessionManager.Core/ArrlSubmissions/`) do and why.

## No job, no UI yet — pure logic for Phase 9 to call

Unlike every prior phase, this one has no background job at all: submitting a session's results to
ARRL is a manual, out-of-band process (the spec's own words: "manual process, not automated"), so
there's nothing to poll or scan. Both services exist purely so Phase 9's (not yet built) admin UI
has real logic to call the moment it exists — `ArrlSubmissionService.MarkSubmittedAsync` for the
session detail view's toggle button, `ArrlSubmissionReportService.GetPendingSubmissionCountAsync`
for the dashboard indicator. Both are registered in the Worker's DI container already so they're
ready to resolve, even though nothing calls them yet.

## No schema change

`Session.ArrlSubmissionStatus`/`ArrlSubmittedDate`/`ArrlSubmittedByUserId` already existed in the
shared data model since Phase 0, already configured in `AppDbContext` (the `ArrlSubmittedByUser`
FK). Confirmed via a throwaway `dotnet ef migrations add` that produced an empty `Up()`/`Down()`
before writing any code — no migration ships with this phase.

## The toggle is one-way and idempotent

The spec describes "toggle Not Submitted → Submitted" — there's no "un-submit" action, so
`MarkSubmittedAsync` only ever moves that direction. Calling it again on an already-`Submitted`
session is a no-op (`ArrlSubmissionMarkResult.AlreadySubmitted`) that leaves the original
`ArrlSubmittedDate`/`ArrlSubmittedByUserId` untouched — a duplicate call (e.g. a double-click once
Phase 9's button exists) must not silently reassign credit for the submission to whoever clicked
second. A real state change also writes one `AuditLog` row (`Action = "ArrlSubmissionMarked"`),
matching the precedent `SessionIngestionService`'s reschedule-flagging already set for
user/system-triggered `Session` state changes worth auditing.

## "Pending submission" definition

The spec's own phrasing is compressed: "sessions with `Granted` or otherwise-complete candidates
where status is still `NotSubmitted`." Read as: a session counts as pending once it has *any*
candidate in a terminal/complete `ApplicationStatus` (`Granted`, `Failed`, or `NotTested` — the
same terminal set `SessionIngestionService.IsTerminal` already uses) and the session's own
`ArrlSubmissionStatus` is still `NotSubmitted`. A session with a mix of terminal and non-terminal
candidates still counts (there's something concrete ready to submit even if not everyone's done).
Cancelled sessions never count. Not required to be "every candidate terminal" — that would under-count
sessions where at least one result is ready to report even while another candidate's outcome is
still pending.
