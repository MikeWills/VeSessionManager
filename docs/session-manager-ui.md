# Session Manager Candidate Actions (Phase 9b)

The Claude Design mockup handoff (`design_handoff_vesessionmanager_admin_ui/`, delivered as a zip
and extracted into the repo — the design pass the spec's Phase 9 checkpoint calls for) drove the
UI; recreated pixel-close in Razor Pages (`Pages/SessionManager/`) with a self-contained
`wwwroot/css/app.css` design-system stylesheet (dark chassis, IBM Plex Sans/Mono, chip/meter/kebab/
modal components, light+dark via `data-theme`) and a small vanilla-JS file (`wwwroot/js/app.js`, no
framework — theme toggle + kebab-menu open/close + modal open/close), not the mockup's static
per-file `<script>` blocks.

## Business logic lives in Core services, pages are thin wiring

- `CandidateActions/CandidateActionService` — mark failed, delete/no-show with immediate PII null,
  set FRN, mark paid manually, flag refund requested, create retest payment
- `Sessions/SessionActionService` — mark session completed + bulk `Tested` flip + felony-disclosure
  email fan-out, clear reschedule flag
- `VolunteerExaminers/VolunteerExaminerRosterService` — manual VE add/remove on one session's roster

Each is its own result-enum-returning, audit-logged, directly-unit-tested class
(`CandidateActionServiceTests`/`SessionActionServiceTests`/`VolunteerExaminerRosterServiceTests`),
following the `VecSubmissionService` shape from Phase 8.

The three email-sending actions (resend confirmation, ARRL Youth Program instructions, felony
disclosure instructions) were added to `CandidateNotificationService` instead of the new services,
per the spec's own "every email send should use this same engine" note — `CandidateEmailSendResult`
is their shared outcome enum. Two new `EmailTemplate` keys (`FelonyDisclosureInstructions`,
`ArrlYouthProgramInstructions`) seeded per-team by `EmailDefaultsSeeder`, same as every other
template.

## Known, accepted tensions / simplifications

- `VolunteerExaminerRosterService`'s manual add/remove is not "sticky" against
  `VolunteerExaminerSyncService`'s Phase 7 full-reconciliation-from-ExamTools-every-poll behavior —
  a manual edit can be overwritten by the next poll if ExamTools' own roster for that session
  disagrees; this is by design (ExamTools stays the source of truth), not a bug to chase.
- A candidate's payment *row* in the UI shows one "primary" payment (most recent `Unpaid`, else most
  recent overall) even though a candidate can have multiple `Payment` rows (initial + retest) —
  matches the mockup's one-row-per-candidate table, but "Mark paid manually"/"Flag refund requested"
  only ever act on that one primary payment, not a specific one the user picked.

## Beyond the mockup

Nav also gained two small report/dashboard pages not in the mockup's four designed screens
(`VeRoster.cshtml` wrapping Phase 7's `VolunteerExaminerReportService`, `VecSubmission.cshtml`
wrapping Phase 8's `VecSubmissionReportService` + an inline mark-submitted action) — styled with the
same design-system components since they're plain reports, not something needing a fresh design
pass.

Session list/detail page authorization extends `[Authorize(Roles=...)]` to include `TeamAdmin`
alongside `SystemAdmin`/`SessionManager` (the 9a placeholder pages only had the latter two) —
applying `SessionAccessScope`'s already-documented TeamAdmin-is-a-superset-of-SessionManager rule to
real pages for the first time; every POST handler also independently re-checks
`SessionAccessScope.CanEdit` against the session's actual team (defense in depth, since a session ID
is a guessable route parameter).

**Note:** the original build also included "add walk-in candidate" and "move candidate to a
different session" — both were removed shortly after (2026-07-21) as redundant with ExamTools
itself; see the "Duplicative-with-ExamTools features removed" entry in CLAUDE.md's Known Constraints.

## Live-verified

Real browser click-through against manually-seeded test sessions/candidates (no live ExamTools data
in dev) covering every action that shipped: mark failed, create retest payment, add/remove VE, mark
session completed (confirmed the felony-disclosure gate skips quietly when SMTP isn't configured,
doesn't fail the whole action), delete/no-show with PII clearing, clear reschedule flag, VEC
submission toggle from both the detail page and the new dashboard page, and the light/dark theme
toggle.
