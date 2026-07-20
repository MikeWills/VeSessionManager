# VEC Submission Tracker (Phase 8)

What `VecSubmissionService`/`VecSubmissionReportService`
(`VeSessionManager.Core/VecSubmissions/`) do and why.

**Renamed from "ARRL Submission Tracker" (2026-07-21), per explicit user request.** The spec
originally named this phase and its fields after ARRL specifically, but submission goes to
whichever VEC a given session is actually under (`Session.VecId`) — ARRL is the common case for
this deployment today, not the only one this data model supports (a team can work with multiple
VECs, per `docs/multi-team.md`). `Session.ArrlSubmissionStatus`/`ArrlSubmittedDate`/
`ArrlSubmittedByUserId` became `VecSubmissionStatus`/`VecSubmittedDate`/`VecSubmittedByUserId`
(migration `Phase8VecSubmissionRename`, a real `RenameColumn`/`RenameIndex` — not a drop+recreate,
so no data loss). The genuinely ARRL-specific features elsewhere in the spec — `Vec.SupportsYouthProgram`
and the `ArrlYouthProgramInstructions` email template — were **not** renamed, since those really are
about ARRL's own specific youth discount program, not a generic VEC concept.

## No job, no UI yet — pure logic for Phase 9 to call

Unlike every prior phase, this one has no background job at all: submitting a session's results to
a VEC is a manual, out-of-band process (the spec's own words: "manual process, not automated"), so
there's nothing to poll or scan. Both services exist purely so Phase 9's (not yet built) admin UI
has real logic to call the moment it exists — `VecSubmissionService.MarkSubmittedAsync` for the
session detail view's toggle button, `VecSubmissionReportService.GetPendingSubmissionCountAsync`
for the dashboard indicator. Both are registered in the Worker's DI container already so they're
ready to resolve, even though nothing calls them yet.

## No schema change beyond the rename

`Session.VecSubmissionStatus`/`VecSubmittedDate`/`VecSubmittedByUserId` (as `Arrl*`) already
existed in the shared data model since Phase 0, already configured in `AppDbContext` (the
`VecSubmittedByUser` FK). Confirmed via a throwaway `dotnet ef migrations add` that produced an
empty `Up()`/`Down()` before writing any Phase 8 code — the only schema change this phase needed
was the later rename.

## The toggle is one-way and idempotent

The spec describes "toggle Not Submitted → Submitted" — there's no "un-submit" action, so
`MarkSubmittedAsync` only ever moves that direction. Calling it again on an already-`Submitted`
session is a no-op (`VecSubmissionMarkResult.AlreadySubmitted`) that leaves the original
`VecSubmittedDate`/`VecSubmittedByUserId` untouched — a duplicate call (e.g. a double-click once
Phase 9's button exists) must not silently reassign credit for the submission to whoever clicked
second. A real state change also writes one `AuditLog` row (`Action = "VecSubmissionMarked"`,
`Details` names the session's actual VEC via `session.Vec.Name` — not hardcoded to "ARRL"), matching
the precedent `SessionIngestionService`'s reschedule-flagging already set for user/system-triggered
`Session` state changes worth auditing.

## "Pending submission" definition

The spec's own phrasing is compressed: "sessions with `Granted` or otherwise-complete candidates
where status is still `NotSubmitted`." Read as: a session counts as pending once it has *any*
candidate in a terminal/complete `ApplicationStatus` (`Granted`, `Failed`, or `NotTested` — the
same terminal set `SessionIngestionService.IsTerminal` already uses) and the session's own
`VecSubmissionStatus` is still `NotSubmitted`. A session with a mix of terminal and non-terminal
candidates still counts (there's something concrete ready to submit even if not everyone's done).
Cancelled sessions never count. Not required to be "every candidate terminal" — that would
under-count sessions where at least one result is ready to report even while another candidate's
outcome is still pending.
