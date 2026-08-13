# Audit 2026-08-11 — Task List

Findings from the nine-agent full-codebase audit, as discrete pickable work items.
Narrative and cross-cutting analysis: [`audit-2026-08-11-report.md`](audit-2026-08-11-report.md).

## Every task here is now a GitHub issue

Filed 2026-08-11 as **#231–#315**, all labelled `audit-2026-08-11`. **The issue is the source of truth
for status** — this file does not track what is done. Work from the issue; use this file for the
cross-cutting picture and for the "verified clean" notes that keep you from re-auditing settled ground.

| Task ID | Issue |
|---|---|
| T-01 – T-04 | #231 – #234 |
| THEME-VE-SCOPE (the blocking decision) | **#235** |
| T-05 – T-68 | #236 – #299 *(issue = 231 + N)* |
| D-01 NUL bytes · D-02 schedule drift · UlsLookupOptions | #300 · #301 · #302 |
| Dead code sweep (D-03 – D-17) | #303 |
| DUP-01 · DUP-02 · DUP-03/04/05 · DUP-06/07/08 · DUP-09 · DUP-10–14 | #304 – #309 |
| S-01 · S-02 – S-10 | #310 · #311 |
| Low-severity web/auth hygiene · L-06/L-07 · L-15–L-19 · L-20–L-25 | #312 – #315 |

Useful filters: `label:audit-2026-08-11 label:security` (21) · `label:ops` (6) ·
`label:bug` (42) · `label:"good first issue"` (5) · `label:needs-design` (2).

**#235 blocks five issues** (#236–#240) and needs a decision, not code — start there if you want the
biggest reduction in open work per hour spent.

## How to use this file

**If you are a sub-agent picking up work:**

1. Pick a task by ID, then **open its issue** (table above) — it may already be assigned, in progress,
   or closed. Prefer P0 over P1 over P2. Prefer tasks in a `THEME` group together — they share a fix
   and reviewing them separately wastes effort.
2. **Verify the finding before acting on it.** Line numbers are from commit `c2c8ea2`; files move. The
   2026-08-03 audit had five findings that were wrong on re-check, one of which would have deleted a
   live authorization check. Each task below carries a **Confidence** rating — anything below
   *Confirmed* means read the code first and be prepared to close the task as invalid.
3. Follow CLAUDE.md's Established Patterns. Several tasks here exist *because* a pattern was
   re-derived instead of reused.
4. Definition of done: `dotnet build` clean (warnings are errors in this repo), `dotnet test` green,
   and a regression test where the task names one.
5. **These should be GitHub issues before they are worked** — CLAUDE.md makes issues the single list of
   outstanding work. One task here = one issue.

**Priority tiers**
- **P0** — correctness/security/ops defects with a real failure path. 19 items.
- **P1** — real but bounded: latent bugs, wrong messages, perf that matters at this scale, ops hygiene.
- **P2** — dead code, duplication, tidiness. Safe to batch.

**Themes** — tasks sharing a root cause. Fix the theme, not the instance.
- `THEME-TRACKER` — `ChangeTracker.Clear()` as a per-row error handler (SEC/TRACE, 4 tasks)
- `THEME-VE-SCOPE` — VolunteerExaminer is global, callers assume team-scoped (5 tasks)
- `THEME-SILENT` — failure that renders as success (5 tasks)
- `THEME-SCAN` — unbounded historical scans (3 tasks)
- `THEME-TESTS` — the suite renders pages but never posts (2 tasks)

---

# P0 — Do first

## THEME-TRACKER

### T-01 — `ChangeTracker.Clear()` in the VE license sweep voids the whole batch
**Area** Traceability L3 · **Files** `src/VeSessionManager.Core/Uls/VolunteerExaminerLicenseWatchService.cs:97`
(batch loaded tracked at `:53-59`, saved at `:92`) · **Effort** S · **Confidence** Confirmed

The per-row catch calls `dbContext.ChangeTracker.Clear()`, which detaches every entity — including the
whole `due` list. One FRN collision on VE #7 of 250 means VEs #8–250 are mutated **detached**
(`LicenseLastCheckedUtc`, `Frn`, `LicenseStatus`, `CallSignHistory.Add` at `:182`), `SaveChangesAsync`
writes nothing, `result.Checked++` still runs. Job History is green, reports "checked 243", zero rows
written — and because the stamp never persisted, the next run repeats it identically.

**Fix** Detach only the failing entity and its added history rows
(`dbContext.Entry(ve).State = EntityState.Detached`), or re-query per row.
**Test** A fake that throws on row 3 of 5; assert rows 4 and 5 persist and `Checked` matches rows
actually written.

### T-02 — The same `Clear()` silently disables per-team ingestion throttling
**Area** Traceability L4 · **Files** `src/VeSessionManager.Worker/SessionIngestionJob.cs:71,88-89`;
`src/VeSessionManager.Core/Jobs/JobRunHistoryLogger.cs:126,134,155` · **Effort** S–M · **Confidence** Confirmed

`Team` entities are loaded from the same scoped DbContext the pipeline uses. On any failed pipeline
step, `TryCompleteHistoryAsync` calls `ChangeTracker.Clear()`, detaching `team`. The subsequent
`team.LastIngestionRunUtc = …; SaveChangesAsync()` then persists **nothing**.

Consequence: `IngestionScheduleService.IsDue` returns true on every 300s tick instead of every 60
minutes, for that team, **forever and unlogged**. Also makes the Job Schedule page's per-team last-run
wrong. This is degrading production behavior today.

**Fix** Stamp through a separate scope/context, or
`dbContext.Teams.Where(t => t.Id == team.Id).ExecuteUpdateAsync(...)`.
**Test** Force a step failure; assert `LastIngestionRunUtc` advanced.
**Related** T-03 (same scope decision), T-24 (scope-per-team).

### T-03 — Roster sync catches per session but never clears the poisoned tracker
**Area** Traceability L3 · **Files** `src/VeSessionManager.Core/VolunteerExaminers/VolunteerExaminerSyncService.cs:178-181`
· **Effort** S · **Confidence** Confirmed

Mirror image of T-01. The per-session catch logs but leaves the failed session's `Add`/`Remove` entries
tracked, so every later session's save re-attempts them and fails too. One bad session takes the whole
team's run.

**Fix** Scoped detach of that session's entries in the catch.

### T-04 — Merge rollback leaves the tracker claiming the merge succeeded
**Area** Traceability L3 · **Files** `src/VeSessionManager.Core/VolunteerExaminers/VolunteerExaminerMergeService.cs:111-118,128-132`
· **Effort** XS · **Confidence** Confirmed

`SaveChangesAsync` at `:99` already marked survivor/duplicate/moved rows `Unchanged`. `RollbackAsync`
reverts the database but not the tracker, so for the rest of the scoped request the context reports the
merge as applied while the DB does not.

**Fix** `ChangeTracker.Clear()` after rollback.

---

## THEME-VE-SCOPE

> ## RESOLVED 2026-08-11 — cross-team reach is intended; T-05 and T-06 are closed
>
> The deployment hosts **cooperating teams, not unrelated organisations** — `VolunteerExaminer`'s own
> comment says so where the contact fields are declared, and shares them across teams deliberately.
> Reasoning, supporting counts and the conditions that would reverse it are in
> **`docs/ve-management.md` → "Cross-team reach is intended, and was re-confirmed on 2026-08-11"**.
>
> Evidence that decided it: **54 of 175 VEs already serve more than one team**, so cross-team service
> is the normal case; and contact details are **essentially unpopulated (0 addresses, 0 notes, 1
> email, 1 phone of 175)**, so the disclosure the finding describes is not reachable today. Both
> pages involved are already `SystemAdmin`/`TeamAdmin`-only.
>
> - **T-05 (#236) — closed, working as intended.**
> - **T-06 (#237) — closed, working as intended.**
> - **#235 — closed.**
>
> **T-07, T-08 and T-09 remain open and are ordinary bugs.** None of them depended on this decision;
> each is wrong under any answer. Sharing a person's record with a team that will work alongside them
> is intended. Sharing it with whoever guesses a call sign, silently and unlogged, is not.
>
> **Re-measure before assuming this still holds:** a team joining that the others do not know, contact
> details actually getting populated, or the multi-team VE overlap falling toward zero — any of the
> three flips the answer to SystemAdmin-gated joins, which was the runner-up.

### T-05 — A TeamAdmin can pull any VE in the deployment onto their own team and read their PII
**Area** Security (High) · **Files**
`src/VeSessionManager.Core/VolunteerExaminers/VolunteerExaminerImportService.cs:271-292` (`AddOneAsync`),
`:212-225` (`ApplyRowAsync`), `:59-62` (CSV path); reached from
`src/VeSessionManager.Web/Pages/SessionManager/VeDirectory.cshtml.cs:179-208`
· **Effort** M · **Confidence** Confirmed

`AddOneAsync` matches the submitted call sign against **every** `VolunteerExaminer` row with no team
filter, then unconditionally grants a membership on the acting user's team. The page checks only that
the target team is one the user owns — never that the user may reach the *person*.

Path: TeamAdmin of Team B submits `callSign=W1ABC` (call signs are public and rendered on session
pages). VeDirectory now lists that person's `Email, Phone, AddressLine1/2, City, State, PostalCode` —
fields VeDirectory's own doc comment calls "a data-protection boundary… not public FCC record data".
VeDetail then exposes `Notes` and permits **overwriting the shared person row**. `OnGetExportAsync`
puts it in a CSV. `VeImport` does 500 per upload.

**Fix** Split "match an existing person" from "may this team claim them." Require positive evidence of
an existing relationship (a `SessionVolunteerExaminer` link to one of the team's sessions), or create a
new record, or gate the join behind SystemAdmin.

### T-06 — VeMerge scopes the survivor and not the duplicate
**Area** Security / Traceability L2 (found independently by two agents) · **Files**
`src/VeSessionManager.Web/Pages/SessionManager/VeMerge.cshtml.cs:93-108`, POST guard `:54`
· **Effort** S · **Confidence** Confirmed

`LoadAsync` correctly refuses a survivor outside `ResolveViewableTeamIds`. The `others` query
immediately below has no filter, and the POST guard only checks the id appeared in that unscoped list,
so it does not help. `MergeAsync` takes no scope of its own — the page is the only gate.

Impact: with a genuine call-sign collision (which the page itself warns about, since the FCC reissues
call signs), a Team B admin can merge in a Team A record. `MergeTeamMemberships`,
`MergeAccreditations` and `FillBlankContactDetails` all run; the original is retired with
`MergedIntoVolunteerExaminerId`. **Irreversible; the owning team gets no signal.**

**Fix** Per the theme decision. If (a): filter `others` by `viewableTeamIds` and re-check `duplicateId`
against that scope before `MergeAsync`.

### T-07 — Session invitation recipients are not team-scoped
**Area** Traceability L3 · **Files**
`src/VeSessionManager.Core/VolunteerExaminers/VeSessionInvitationService.cs:99-101`
· **Effort** S · **Confidence** Confirmed

`Where(v => volunteerExaminerIds.Contains(v.Id))` with no team scope, while `GetCandidatesAsync:48-52`
correctly scopes the *offered* set. Ids arrive from the posted form. A tampered POST sends
attacker-authored subject/body from the team's own SMTP to any VE on the deployment, including other
teams' rosters and retired members.

**Fix** Join through `VeTeamMemberships` on `session.TeamId` in the recipient query. The equivalent
guard already exists two methods away as `mustBelongToVolunteerExaminerId`.

### T-08 — An admin can claim any VE row as their own login's identity
**Area** Traceability L2 · **Files** `src/VeSessionManager.Web/Pages/Admin/Users.cshtml.cs:176-177`;
`src/VeSessionManager.Core/Admin/UserManagementService.cs:198` · **Effort** S · **Confidence** Confirmed

`SetVolunteerExaminerAsync`'s `volunteerExaminerId` comes from the form and is validated against
nothing the acting user can see. `AuthorizeManageAsync` authorizes only the *target user*; the service
checks only that the VE exists and is unclaimed. Grants no access (identity-only by design) but
permanently claims the record — the rightful team then hits `VolunteerExaminerAlreadyLinked`.

**Fix** Scope the id to VEs reachable via `VeTeamMembership` from the acting user's teams.

### T-09 — The CSV import preview is a cross-tenant existence-and-name oracle
**Area** Security (Medium) · **Files**
`src/VeSessionManager.Core/VolunteerExaminers/VolunteerExaminerImportService.cs:58-62,119-137`;
`Pages/SessionManager/VeImport.cshtml.cs` `OnPostUploadAsync` · **Effort** S · **Confidence** Confirmed

Upload 500 call signs with no `Name` column and stop at the preview. Every row returning `AddToTeam` is
a VE that exists somewhere in the deployment but not on your team, and the rendered `Name` is that
other team's record. Read-only, no audit entry (`VeDirectoryImported` is only written on Apply).

**Fix** Render the *submitted* name only, and collapse `AddToTeam` to `Create` in the display when the
match came from outside the acting user's scope.

---

## THEME-SILENT

### T-10 — Every status and error message on the VE self-service page is unrendered
**Area** Traceability L2 · **Files** `src/VeSessionManager.Web/Pages/Shared/_PublicLayout.cshtml`
(the fix); `Pages/VeSelfService/Details.cshtml.cs:108,125-135,149-154,168-169` (the 13 messages)
· **Effort** XS · **Confidence** Confirmed

`Details.cshtml` sets `Layout = "_PublicLayout"`. That layout has zero `TempData` references — the
status block lives only in `_AppLayout.cshtml:231-239`. So all 13 `TempData["StatusMessage"]` /
`["ErrorMessage"]` values render as nothing, and the whole switch is dead code.

A VE submitting an email change that returns `AlreadyInUse` / `InvalidEmail` / `Throttled` /
`SystemEmailNotConfigured` / `NoCurrentEmail` is redirected to a byte-identical page. This is the app's
only unauthenticated PII-editing surface.

**Fix** Add the two TempData blocks to `_PublicLayout.cshtml`. Also fixes future error messaging on the
other 12 pages using that layout.

### T-11 — Manual refresh reports success after a total pipeline failure
**Area** Traceability L2 · **Files** `src/VeSessionManager.Web/Pages/Admin/TeamMaintenance.cshtml.cs:162-164`;
`Pages/SessionManager/Detail.cshtml.cs:194-197`; result type in `Core/Ingestion/ManualCandidateRefreshService.cs`
· **Effort** S · **Confidence** Confirmed

`TeamPipeline` runs every step through `JobRunHistoryLogger`, which catches and does **not** rethrow —
it records `Success=false` and returns normally. A total failure therefore yields
`ManualRefreshResult(0,0,0)` and both handlers set a green status unconditionally: *"Refreshed HRCC — 0
new candidate(s), 0 updated, 0 confirmation email(s) sent."* Exactly the documented `sent 0, failed 1`
shape.

**Fix** Give `TeamPipelineResult`/`ManualRefreshResult` a step-failure count — `JobRunHistoryLogger`
already computes it — and render `ErrorMessage` when non-zero.

### T-12 — The key-ring guard misses the sixth encrypted column
**Area** Security (Medium, silent-failure class) · **Files**
`src/VeSessionManager.Core/Data/DataProtectionKeyRingGuard.cs:44,74-89` vs `Data/AppDbContext.cs:318-327`
· **Effort** S · **Confidence** Confirmed

Six columns use `EncryptedStringConverter`: five on `Team`, plus `SystemSettings.SystemSmtpPassword`.
The guard iterates `dbContext.Teams` only. Its own doc comment predicted this gap; the column it misses
is the one added before the guard existed.

On a wrong/lost key ring the guard logs `Data Protection key ring verified — N team(s), all stored
credentials readable` and starts. The system SMTP password is then a `CfDJ8…` blob, so **password reset**
and **VE self-service sign-in links** fail to authenticate to SMTP — and `PasswordResetService:105-111`
deliberately swallows send failures to avoid an enumeration oracle, so the user sees "check your inbox"
forever. Worst case: a deployment with zero teams checks nothing at all and reports success.

**Fix** Load the `SystemSettings` singleton in `VerifyAsync` and run `LooksLikeCiphertext` over it.
**Test** A reflection-driven test over every `HasConversion(encryptedString)` property, in the shape
`CandidatePiiFieldsTests` already uses — this is what stops the next one.

### T-13 — `MarkSubmittedAsync`'s three-value result collapsed into two branches
**Area** Traceability L2 · **Files** `src/VeSessionManager.Web/Pages/SessionManager/Index.cshtml.cs:428-430`
· **Effort** XS · **Confidence** Confirmed

`SetStatus(result == Marked, …, "Session is already marked submitted.")` against a three-value enum
(`Marked`/`AlreadySubmitted`/`SessionNotFound`), so `SessionNotFound` tells the user the opposite of
what happened. **The three-branch fix already exists** at `Detail.cshtml.cs:140-145`, with the comment
explaining it — the list copy was never updated.

**Fix** Mirror `Detail.OnPostToggleVecSubmissionAsync`. Consider whether the two copies should be one
(see D-01).

---

## THEME-SCAN

### T-14 — VE roster sync still loads every historical session, every tick
**Area** Performance (High) · **Files**
`src/VeSessionManager.Core/VolunteerExaminers/VolunteerExaminerSyncService.cs:95-99`
· **Effort** S · **Confidence** High

`Status == SessionStatus.Active` means "not cancelled", so it bounds nothing. The file *knows* — 45
lines of comment describe exactly this — but the fix landed **in memory, after materialization**
(`:147-149`, `sessions.RemoveAll(...)`). So the HTTP calls are correctly bounded (the reported symptom,
which is why it reads as fixed) while the query still loads every historical session plus roster links
plus each linked VE, every tick, to discard them on the next line.

**Fix** Add `&& s.VeRosterFinalSyncedUtc == null` to the `Where` — translatable, removes nearly every
historical row. Keep the in-memory `RemoveAll` for the `IsFinished`/`HasEnded` half, which genuinely
cannot translate. The `ignoreRetryWindow: true` historical-import path still works (those rows have a
null stamp).

### T-15 — Ingestion loads the team's entire session→candidate→payment graph every tick
**Area** Performance (High) · **Files** `src/VeSessionManager.Core/Ingestion/SessionIngestionService.cs:254-257`
· **Effort** M · **Confidence** High

Bounded by `TeamId` alone — no date filter, no `Take`, no `AsNoTracking`, and two nested collection
Includes making it a genuine cartesian product. Every other consumer of this data got a window added
after the historical import (`PaymentEligibilityWindow` 30d, `ResultSyncWindow` 14d,
`SessionEventSchedulingService` 1d), each with a comment. This one never did.

**Fix** Split it. The cancellation diff (`:364-370`) reads only scalar `Session` fields — strip the
Includes or project there. Candidates+payments are only touched for sessions in `remoteSessions`
(bounded by the feed) — load those in a second query filtered to `remoteIds`. The eager-load rationale
at `:252-254` stays satisfied.

### T-16 — `UlsWatcherService` is the only watcher with no per-run cap
**Area** Performance (High) · **Files** `src/VeSessionManager.Core/Uls/UlsWatcherService.cs:54-58,62-66`
· **Effort** M · **Confidence** High

Selects every non-terminal candidate with an FRN, for all time, all teams — then one sequential HTTP
call per row, twice daily, forever. Both siblings cap themselves and say why
(`LicenseWatchService.cs:57`, `VolunteerExaminerLicenseWatchService.cs:35`, both
`MaxLookupsPerRun = 250` with `.OrderBy(w => w.LastCheckedUtc).Take(...)`). This one has neither, and
no "last checked" column, so a `Take` cannot be added safely today without starving rows.

The historical import backfilled ~1,700 candidates for one team;
`SessionIngestionService.MarkHistoricalCandidatesGranted:176-183` exists to keep them out of this scan
and its comment says "one HTTP call per candidate, twice a day, forever" — the risk is understood, just
undefended for the non-imported path.

**Fix** Add `Candidate.UlsLastCheckedUtc` and adopt the same `OrderBy(...).Take(250)`, or bound on
session age. **Coordinate with T-17** — both touch this service.

---

## Remaining P0

### T-17 — ULS watcher compares a UTC calendar date against FCC wall-clock dates
**Area** Traceability L3 (High) · **Files** `src/VeSessionManager.Core/Uls/UlsWatcherService.cs:134,177,185`;
`Uls/ExamToolsUlsLookupClient.cs:143` (`AsUtcDate`) · **Effort** S · **Confidence** Confirmed

`candidate.Session.ScheduledStartUtc.Date` is a **UTC** calendar date; FCC's date-only values are
wall-clock, stamped at UTC midnight. For any session at/after ~19:00 ET the stored UTC date is **the
next day**.

Concrete: a Thursday 20:00 ET session has `ScheduledStartUtc` = Friday 00:00 UTC. The VEC files that
night; FCC's receipt date is Thursday. `receiptDate.Date < sessionDate` → `ApplyPendingApplication`
returns `false` for **every candidate, forever**. They never leave `Unmatched`; `FccHoldReason`,
`FccPaymentStatus`, `UlsApplicationFileNumber`, `ApplicationDateEnteredUtc` are never written — which
also means `FccPaymentStatus = PendingVerification` never appears, so **the new FCC-fee 5-day reminder
(#219) can never fire**. Line `:185`'s `effectiveDate.Date >= sessionDate` fails the class's own
documented verification case (an upgrade whose effective date equals the session date).

**Fix** Convert `ScheduledStartUtc` through `UlsSchedule.EasternTimeZone` before `.Date`, per the helper
CLAUDE.md already mandates for this bug class.
**Test** An evening-ET session with an FCC receipt date on the session's ET calendar day.

### T-18 — Renewal monitor has no per-row catch on a save documented as expected-to-throw
**Area** Traceability L3 (High) · **Files** `src/VeSessionManager.Core/Uls/LicenseWatchService.cs:76-96,132-136`
· **Effort** S · **Confidence** Confirmed

The per-row loop has no try/catch, yet `:132` documents a save expected to throw: a vanity rename
colliding with `IX_WatchedLicenses_TeamId_CallSign` (unique). A team watches `KD9ABC` and `W5XYZ`;
`W5XYZ` renews to `KD9ABC`; the violation escapes `RunAsync`, abandoning every remaining watched
license in that run. "Loud" was intended for the collision; taking the rest of the run with it was not.

**Fix** Per-row try/catch with **scoped** detach, matching the corrected shape from T-01.

### T-19 — Merge leaves three references dangling behind a global query filter
**Area** Traceability L3 (High) · **Files**
`src/VeSessionManager.Core/VolunteerExaminers/VolunteerExaminerMergeService.cs:83-90`;
`Data/AppDbContext.cs:197` (the filter) · **Effort** M · **Confidence** Confirmed

Merge repoints `SessionVolunteerExaminer`, `VeTeamMembership`, `VeVecAccreditation` and
`VeCallSignHistory` — but **not** `User.VolunteerExaminerId`, `VeSelfServiceToken`, or
`VeEmailChangeRequest`. `HasQueryFilter(v => v.MergedIntoVolunteerExaminerId == null)` then hides the
target.

The one VE of 176 who has self-service gets merged: `User.VolunteerExaminerId` points at an invisible
row, so `/Account/MyVeDetails` cannot resolve them **permanently**, and the filtered unique index blocks
re-linking if the survivor is already claimed. Worse, `VeSelfServiceLinkService.cs:150` and
`VeEmailChangeService.cs:155` both `Include` a **required** navigation to `VolunteerExaminer` — EF
renders that as an INNER JOIN, the filter applies to the joined side, so the *token row itself* vanishes
and an outstanding link reports "invalid/expired".

**Fix** Repoint all three inside the existing transaction at `:81`.

### T-20 — Zoom's duplicate-prevention guard silently stops working past 30 meetings
**Area** Traceability L4 (High) · **Files** `src/VeSessionManager.Core/Zoom/ZoomClient.cs:86`;
`ZoomMeetingListWireResponse` in `Zoom/ZoomModels.cs` · **Effort** S · **Confidence** Confirmed

`ListMeetingsAsync` requests `/v2/users/{id}/meetings?type=scheduled` with **no `page_size` and no
`next_page_token` handling**; Zoom's default page size is 30, and the wire DTO has no token field.

This call exists *solely* as the query-before-create dedup guard (`FindExistingMeetingAsync`). Past 30
scheduled meetings the list truncates, so a poll that crashed after Zoom's create succeeded but before
the id was saved finds no match and **creates a duplicate Zoom meeting** — the exact bug class the guard
was built for after the 2026-07-21 Discord incident.

**Fix** `?page_size=300` and follow `next_page_token` to exhaustion; add `NextPageToken` to the DTO.

### T-21 — The Square SDK client is cached per team and never invalidated
**Area** Traceability L4 (High) · **Files** `src/VeSessionManager.Core/Square/SquareClient.cs:154-168`
· **Effort** S · **Confidence** Confirmed

`GetOrCreateClient` keys on TeamId only and never rebuilds on credential or environment change — unlike
`ExamToolsClient.GetOrCreateTeamSession`, which rebuilds when `BaseUrl` changes.

CLAUDE.md's own documented post-deploy step is *"set live teams back to Production in Team Settings."*
Doing that in a running Worker **has no effect** — the cached client keeps hitting Sandbox until the
process restarts. Same for rotating an access token: every link generation keeps failing with the
revoked token and nothing indicates why.

**Fix** Cache a `(client, accessToken, environment)` tuple and rebuild when either differs, mirroring
`ExamToolsClient`.

### T-22 — The session filter form's sort direction binds to nothing
**Area** Traceability L1 (High) · **Files** `src/VeSessionManager.Web/Pages/SessionManager/Index.cshtml:22`
vs `Index.cshtml.cs:178` · **Effort** XS · **Confidence** Confirmed

`<input type="hidden" name="sortDirection" …>` but the property is
`[BindProperty(SupportsGet = true, Name = "dir")]`, so the binder only reads `dir`. Submitting the
filter form silently discards the user's sort direction.

**Fix** `name="dir"`. **Covered by** T-38's form-shape test.

### T-23 — The deploy account's sudoers grant is effectively unrestricted root
**Area** Ops / Security (High) · **Files** `ops/setup-server.sh:104`; comment at
`.github/workflows/deploy.yml:136-137` · **Effort** M · **Confidence** Confirmed

The grant includes `/usr/bin/rsync *` as root with an unconstrained wildcard. `sudo rsync` can read or
overwrite any file on the box (`/etc/shadow`, `/root/.ssh/authorized_keys`, sudoers itself), and
`rsync -e` / `--rsync-path` can execute commands. Whoever holds `SSH_PRIVATE_KEY` owns the server, not
just this app. `/usr/bin/cp <db> *` similarly allows writing DB contents anywhere.

This directly contradicts the reasoning written into the workflow: *"the narrow rules are what stop a
compromised deploy key from touching anything else on the box."* The `systemctl` rules are indeed
narrow; the `rsync` rule beside them makes that narrowness decorative.

**Fix** An `rrsync`-style restricted wrapper pinned to `/opt/vesessionmanager/{worker,web}/`, or drop
`--rsync-path="sudo rsync"` by making `deploy` a member of the `vesessionmanager` group with group-write
on the deploy tree. If neither, correct the comment — it currently claims a property that does not hold.

### T-24 — WAL-unaware backup: the deploy snapshot silently drops transactions
**Area** Ops (High) · **Files** `BACKUP.md:35`; `.github/workflows/deploy.yml:103-105`
· **Effort** S · **Confidence** Confirmed locally; **verify production's mode first**

`BACKUP.md:35` states the DB is "in rollback-journal mode, not WAL (nothing sets `PRAGMA
journal_mode`)". The second half is right — no `PRAGMA` anywhere in `src/` — but the conclusion is
wrong: EF Core enables WAL on databases it creates, journal mode persists in the file header, and
`pragma journal_mode` on the live file returns `wal`. The 2.5 MB `-wal` and `-shm` files in the repo
root agree.

The deploy's pre-release snapshot copies the main file only, so in WAL mode it is **missing every
transaction since the last checkpoint** — and it opens cleanly, so the loss is silent.

**Fix** Confirm production's mode (`sqlite3 … "pragma journal_mode;"`), correct `BACKUP.md:35`, and
switch the snapshot to `VACUUM INTO` / `.backup`, which `BACKUP.md:39-47` already recommends and which
is correct in either mode.

### T-25 — No off-box backup exists for the database or the key ring
**Area** Ops (High) · **Files** `BACKUP.md:3,102-104`; `ops/setup-server.sh:184-187`
· **Effort** L · **Confidence** Confirmed

Both Tier-1 artifacts — `/var/lib/vesessionmanager/vesessionmanager.db` (every candidate, payment, VE
home address, audit entry) and `/var/lib/vesessionmanager-keys/` (without which every stored credential
is permanently undecryptable) — have no backup off the box. `BACKUP.md:102` says so plainly: *"Both sit
on the same disk as the thing they protect."* `ops/setup-server.sh:184` lists off-box key-ring backup as
a remaining manual step; nothing verifies it was ever done.

**Fix** Build the job `BACKUP.md` already designs. Two destinations, key ring more tightly controlled
than the DB, `VACUUM INTO` per T-24. **Then run the restore test** `BACKUP.md:116` asks for — the
`Data Protection key ring verified` startup line is the proof.
**Do T-24 first** so the backup is not WAL-unaware.

---

# P1 — Real but bounded

## Security

### T-26 — `SetRoleAsync` does not rotate the security stamp
`src/VeSessionManager.Core/Admin/UserManagementService.cs:100-116` · S · Confirmed
`DeactivateAsync:328` correctly calls `UpdateSecurityStampAsync`; `SetRoleAsync` does not, so a demoted
admin keeps the role claim baked into their cookie until `SecurityStampValidator` next revalidates
(framework default 30 min). Most admin pages re-read `user.Role` and fail closed — **two do not**:
`Pages/Admin/SystemSettings.cshtml.cs:58,86` (PII retention window, test mode, deployment-wide SMTP —
the sender used for password-reset mail) and `Pages/Admin/Vecs.cshtml.cs:53,77` (shared VEC reference
data including `ExamToolsCode`, which controls ingestion matching for every team).
**Fix** Add the stamp rotation; and re-check `user.Role` inside those four POST handlers so the
attribute is not the sole gate.

### T-27 — Unvalidated ExamTools base URL exfiltrates the stored password
`src/VeSessionManager.Core/Admin/TeamSettingsService.cs:55` → `ExamTools/ExamToolsClient.cs:132-142,183-192`
· S · Confirmed
No scheme check, no host allowlist, no validation. Secrets are deliberately write-only (the page shows a
masked placeholder), so a TeamAdmin who never knew the password can post
`baseUrl=https://attacker.example`, leave credentials untouched, and within 5 minutes the ingestion job
POSTs `username`+`password` to that host. Same primitive reaches `http://127.0.0.1`, `169.254.169.254`.
A malformed value is an unhandled `UriFormatException` on a background job path.
**Fix** `Uri.TryCreate` + HTTPS + host allowlist in `UpdateExamToolsAsync`; better, restrict the field to
SystemAdmin — it is deployment topology, not a team setting. **Do with T-28.**

### T-28 — Unvalidated SMTP host exfiltrates the SMTP password and every candidate email
`src/VeSessionManager.Core/Admin/TeamSettingsService.cs:128-135` → `Email/SmtpEmailSender.cs:66-73`
· S · Confirmed (transport half: Likely)
Same primitive, worse payload: the attacker also receives a copy of every candidate email. Second half —
`UseStartTls` is admin-controlled and unchecking it selects `SecureSocketOptions.Auto`, which is
*opportunistic*: if the server does not advertise STARTTLS, MailKit proceeds in cleartext and
`AuthenticateAsync` sends the password in the clear. There is no floor.
**Fix** Validate the host (reject loopback/link-local/RFC1918 at minimum); replace `Auto` with
`StartTlsWhenAvailable`, or make StartTls unconditional and delete the toggle. Same for
`SystemSettings.SystemSmtp*`.

### T-29 — Session invitation email skips the HTML encoding every sibling applies
`src/VeSessionManager.Core/VolunteerExaminers/VeSessionInvitationService.cs:172-178` · XS · Confirmed
Five placeholders interpolated raw into an HTML body. `EmailTemplateRenderer` exists to prevent exactly
this and says so; `VeSelfServiceLinkService.cs:113` and `VeEmailChangeService.cs:120-122` both call
`WebUtility.HtmlEncode`. `Session.Title` and `VolunteerExaminer.Name` come from ExamTools' public
registration intake. A crafted title renders as live markup (phishing link, not script — mail clients
strip `<script>`).
**Fix** `WebUtility.HtmlEncode` per placeholder; attribute-safe encoding for `{{ZoomJoinUrl}}` if it ever
lands in an `href`.

### T-30 — Email subject is not CR/LF-stripped before MimeKit
`src/VeSessionManager.Core/Email/EmailTemplateRenderer.cs:61` → `Email/SmtpEmailSender.cs:45` · XS ·
**Needs-verification**
Not encoding the subject is correct (it is plain text); missing control-character stripping is not.
`{{CandidateName}}` originates in ExamTools' public intake. MimeKit re-encodes headers and is generally
not vulnerable — which is why this is low — but the app relies on an undocumented third-party behavior
for attacker-controlled input with no test pinning it. **The fix is worth applying regardless.**

### T-31 — Five wrong passwords also disable the victim's password-reset path
`src/VeSessionManager.Core/Authorization/PasswordResetService.cs:72,129` · XS · Confirmed
The guard uses `IsLockedOutAsync`, intending to block resets for accounts deactivated via
`LockoutEnd = MaxValue` — but it is also true during Identity's ordinary 5-minute failed-login lockout.
An attacker who burns five attempts against a known address silently kills that user's recovery route,
and the user is told "Accepted" either way, so they wait for mail that never comes.
**Fix** Test the deactivation sentinel specifically (`user.LockoutEnd == DateTimeOffset.MaxValue`).

### T-32 — `TryResolveManageableTeamId` silently retargets a credential write
`src/VeSessionManager.Core/Authorization/AdminAccessScope.cs:56-62`; callers incl.
`Pages/Admin/TeamSettings.cshtml.cs:225-232` · S · Confirmed
When a TeamAdmin requests a team they do not manage, it falls back to `effectiveTeamIds[0]` rather than
refusing. No cross-tenant access results — but a multi-team TeamAdmin following a stale link can
silently overwrite **Team X's Square access token** believing they are editing Team Y, and the redirect
reflects the substitution only after the write.
**Fix** On credential-writing handlers, `Forbid()` when the requested id is present and not in scope.

### T-33 — Square webhook is outside every rate-limit partition
`src/VeSessionManager.Web/Program.cs:305-328` · XS · Confirmed
`/webhooks/square/{teamId:int}` matches neither the `/Account` nor `/VeSelfService` prefix, so it gets
`GetNoLimiter`. Each request costs a `Teams.FindAsync` plus HMAC over up to 64 KB before rejection.
Resource exhaustion only — signature verification, replay handling and cross-team blocking are all
correct.
**Fix** A generous per-IP partition for `/webhooks` (e.g. 300/min).

### T-34 — Audit log records no source IP and no authentication events
`src/VeSessionManager.Core/Entities/AuditLog.cs:1-16`; `Pages/Account/Login.cshtml.cs:57-62` · M ·
Confirmed
No IP, no user agent, no session id — and `Login` writes **no audit entry at all**, success or failure.
So a credential-stuffing run or a successful compromised-account login leaves nothing: you can see what
an account did, never that it signed in, from where, or how many times it failed first.
`PasswordResetService:144` and `BootstrapAdminCommand:102` do audit their auth events, making Login's
silence an inconsistency rather than a policy.
**Fix** Nullable `SourceIpAddress` (correct behind the proxy already, via `UseForwardedHeaders`), plus
`SignedIn` / `SignInFailed` / `LockedOut` events.

### T-35 — Test environment reverts to the host-header posture Production pins against
`src/VeSessionManager.Web/appsettings.Test.json` · XS · Confirmed
Sets only `ConnectionStrings` and `DataProtection:KeyRingPath`, inheriting `AllowedHosts: "*"` and
`PublicBaseUrl: https://localhost:5158` from the base — the exact combination
`appsettings.Production.json:5-9` explains at length must be pinned. On the beta box every reset and
self-service link is unusable, and the defence-in-depth layer is absent.
**Fix** Give Test its own `AllowedHosts` and `App:PublicBaseUrl`.

### T-36 — Every deploy deletes the application logs
`.github/workflows/deploy.yml:150-170` · XS · Confirmed
Serilog writes to a relative `logs/` path under the synced tree; the deploy runs `rsync --delete` with
excludes for `*.db*` and `*.bak-*` but **not** `logs`. History is destroyed at exactly the moment most
likely to matter. journalctl retains the console sink, which softens it.
**Fix** `--exclude 'logs'`, or move the sink to `/var/log/vesessionmanager/`, matching the reasoning
already applied to the DB and key ring.

### T-37 — Key-ring backup step recurses into itself
`.github/workflows/deploy.yml:123-125` · XS · Confirmed
Destination is a subdirectory of the source, and rsync builds its file list from the source first — so
each run copies the key ring *plus every previous `.bak-*`*. Size doubles per deploy: 2ⁿ full copies of
the most sensitive material on the box, in a 0700 directory nobody looks at.
**Fix** `--exclude '.bak-*'`, or a sibling directory with a retention count.

## THEME-TESTS

### T-38 — Add a form-shape test: every form field must bind to something
**Effort** M · Highest-value test in this audit
~110 form-posting handlers exist; exactly one is tested (`MyVeDetailsPageTests`). Nothing verifies that
a form's `name=` values bind to the handler's parameters — which is why T-22, T-45 and T-46 were all
live and invisible.
**Fix** A source-scanning test in the shape of the existing `InlineEventHandlerTests`: per page, parse
each `<form>`'s `asp-page-handler` / `?handler=` and every `name=`, then reflect over the page model for
a matching bound property or handler parameter. Also assert antiforgery presence on rendered forms (the
documented explicit-`action=` trap is currently guarded only by code review).

### T-39 — Generalize the empty-`href` check to all pages
**Effort** S
`LinksOnTheVeDirectoryPointSomewhereReal` runs on **one** page, though its own doc calls an empty href
"the signature of that whole bug class". Folding it into the `EveryPageRendersForASystemAdmin` theory is
close to free.

## Correctness / traceability

### T-40 — `SetRetainedAmountOverrideAsync` parses money with ambient culture
`Pages/SessionManager/Detail.cshtml.cs:118` · XS · Confirmed — `"12,50"` parses as 1250 under a
comma-decimal culture. Use `NumberStyles.Number, CultureInfo.InvariantCulture`. Against CLAUDE.md's
explicit money convention.

### T-41 — `ReturnUrl` is bound to nothing, so every deep link lands on the role dashboard
`Pages/Account/Login.cshtml.cs:65` · S · Confirmed — the string appears nowhere in the page, model or
`Program.cs`. Now the common path, since the 2026-08-10 `FallbackPolicy` made every page authenticated.
Bind it and `LocalRedirect` when `Url.IsLocalUrl`.

### T-42 — `Users.cshtml.cs` re-implements `GetUserWithManagerAsync` minus one include
`Pages/Admin/Users.cshtml.cs:288-294` · XS · Confirmed — a second definition without
`.Include(u => u.ManagedByUser).ThenInclude(m => m!.UserTeams)`, plus an extra round-trip. Equivalent
*today* only because transitive TeamLead scoping was removed 2026-08-07; if `GetEffectiveTeamIds` ever
reads `ManagedByUser` again, every `AdminAccessScope` check on this page silently degrades — the precise
trap CLAUDE.md documents.

### T-43 — `CanSendYouthProgram` omits the VEC support check on the session list
`Pages/SessionManager/Detail.cshtml.cs:506` vs `CandidateDetail.cshtml.cs:267` · XS · Confirmed — the
button renders for every non-withdrawn candidate regardless of `Vec.SupportsYouthProgram`, and clicking
returns a raw enum name. `session.Vec` is already Included. Two copies of one rule, drifted.

### T-44 — No `ModelState.IsValid` check in any Admin POST handler
All 12 `Pages/Admin/*` page models · S · Confirmed — non-nullable string params (TeamSettings' four
addresses, EmailTemplates' subject/body, Teams/Vecs name) bind `null` on a tampered or partial POST and
are written straight through, giving `DbUpdateException` → unhandled 500, or a silently emptied required
column.

### T-45 — VeDetail's six POST forms drop the directory's filters
`Pages/SessionManager/VeDetail.cshtml:26,161,182,277,288` · S · Confirmed — `asp-page-handler` with no
explicit `action=` drops the query string, so `TeamId/Search/TagName/IncludeInactive/LicenseStatus/
Worked/WorkedFrom/WorkedTo` all bind null and `SelfRoute()` rebuilds an empty filter set. The
`SelfRoute` doc comment claims to prevent exactly this; it is defeated upstream.
**Fix** `action="@Url.Page(...)"` **plus** `asp-antiforgery="true"` — both halves, per the documented
trap (`VeDirectory.cshtml:150-151` is the working pattern). Or hidden fields.

### T-46 — A nullable payment id can bind as 0 and 403
`Pages/SessionManager/Detail.cshtml:202,414` · XS · Confirmed (latent) — `PrimaryPaymentId` is `int?`;
when null the hidden field posts `""`, which fails to bind to non-nullable `int` → `paymentId == 0` →
guard returns false → 403. Only reachable if the guarding `disabled=` is loosened. Render the form only
when non-null.

### T-47 — Roster sync and VE import key a dictionary on a non-unique column
`VolunteerExaminerSyncService.cs:76-78`; `VolunteerExaminerImportService.cs:63-65` · XS · Confirmed
`.ToDictionary(v => v.CallSign!)` on a column explicitly declared **not unique**, while the neighboring
`byFrn` (on the column that *is* uniquely indexed) is defensive and the placeholder half two lines away
correctly uses `GroupBy(...).First()`. Latent today (only `<UNKNOWN>` ×2 in live data, excluded by
`CallSign.IsUsable`) — but `VolunteerExaminerDirectoryService:146` queries for usable-call-sign
duplicates *on purpose*, and the license sweep can create one. Line 76 sits outside the per-session
try/catch, so it kills the team's roster sync every tick until a human merges; the import copy 500s the
VE Import page.
**Fix** `GroupBy(...).ToDictionary(g => g.Key, g => g.First())` in both.

### T-48 — Encrypted column compared server-side, so the predicate is always true
`Core/Ingestion/IngestionStatusService.cs:79` · S · Confirmed — `t.ExamToolsPassword != ""` on a
converted column emits `<> @p` where `@p = Protect("")`, freshly generated non-deterministic ciphertext
that can never equal a stored value. An admin who clears the password sees the Ingestion Status page and
health banner report the team configured and due, while the job correctly skips it via
`IsExamToolsConfigured`. **The only server-side comparison on an encrypted column in the codebase.**
**Fix** Normalize blank→null on the write path and drop the `!= ""` half (the `!= null` half is fine).

### T-49 — Reconciliation's remote and local windows disagree by up to 24h
`Core/Reconciliation/ReconciliationService.cs:57-67` · S · Confirmed — remote bound is a midnight-aligned
`DateOnly`, local bound carries `now`'s time-of-day. Job cadence is `IntervalFromWorkerStart`, so run
time is arbitrary: a session at day-120 02:00 UTC is returned by the feed and excluded from `local` → a
false `MissingSession` on the standing table and nav badge, which never resolves (next night it has aged
out of both, and `RecordAsync:124` only re-examines findings inside the window).
**Fix** Anchor `local` on the same `DateOnly` boundary.

### T-50 — Payment links minted for cancelled sessions
`Core/Payments/PaymentGenerationService.cs:129-137` · XS · Confirmed — the creation pass filters
`Status == Active` (`:55`); the link-generation pass does not. A session cancelled after its `Payment`
rows exist but before Square was configured (a real ordering — Square is optional and often set up
later) gets live checkout links. `SessionEventSchedulingService:120` explicitly tears down Zoom/Discord
for cancelled sessions; nothing tears these down.

### T-51 — Former-call-sign resolution can mint a duplicate person
`VolunteerExaminerSyncService.cs:257-260` · S · Confirmed — `byFormerCallSign` is built from the whole
history table but resolved against `knownVes`, a strict subset (excludes null and non-usable call signs).
A VE whose current call sign is null — reachable, `VolunteerExaminerImportService:200` creates name-only
rows — falls through to `:262` and a **second** row is created, splitting their session history: exactly
what the block's comment says it prevents. Resolve against `allWithCallSign` or a by-id map.

### T-52 — Roster link de-dup keyed on call sign, not the actual PK
`VolunteerExaminerSyncService.cs:200-215,295-304` · S · Confirmed — PK is
`(SessionId, VolunteerExaminerId)`. (a) A roster naming one person under both current and former call
sign passes the call-sign guard twice → duplicate composite PK → throw. (b) After the license sweep
renames a VE, a roster still reporting the old call sign makes the code drop and re-add the link,
churning `LinksRemoved`/`LinksAdded` every tick. Key both on `VolunteerExaminer.Id`.

### T-53 — VE email uniqueness is enforced in four places and indexed in none
`VolunteerExaminerManagementService.cs:52,173`; `VeEmailChangeService.cs:70,168`;
`VeSelfServiceLinkService.cs:74` · S · Confirmed — no unique index backs the rule, so two concurrent
requests both pass and both commit; `RequestLinkAsync` then resolves with `FirstOrDefaultAsync` and **no
`OrderBy`**, so a sign-in link — a bearer credential reaching personal data — goes to whichever row
SQLite returns. No duplicates in live data today.
**Fix** `HasIndex(v => v.Email).IsUnique().HasFilter("\"Email\" IS NOT NULL")`, matching the `Frn`
pattern; add a deterministic `OrderBy`.

### T-54 — Two persisted enums are unpinned, against an explicit in-file rule
`Entities/ReconciliationFinding.cs:52`; `Entities/SquareApiEnvironment.cs:17` · XS · Confirmed —
`Enums.cs:6-17` states *"EVERY PERSISTED ENUM BELOW HAS PINNED VALUES, AND MUST KEEP THEM"*.
`ReconciliationFindingKind` is a component of a **unique index**, so inserting a member alphabetically
silently renumbers stored rows and re-points the index; `SquareApiEnvironment` renumbering flips a live
team from Sandbox to Production. Pin both and add them to the banner's list.

### T-55 — Unnormalized call sign written to the watch list
`Core/Uls/LicenseWatchService.cs:136` · XS · Confirmed — stores the raw feed value while the VE sweep
normalizes (`:179`, `CallSign.Normalize`). A lower-cased or untrimmed value defeats the unique index
under SQLite's case-sensitive `=`, so the same license can be watched twice and later matching misses.

### T-56 — Three non-atomic multi-entity writes
`Admin/TeamSettingsService.cs:29,40`; `FeeConfigurationService.cs:39,42`; `VecManagementService.cs:29,32`
· S · Confirmed — the first is the one that matters: if the second save fails, the `Team` row commits
with **no `EmailSettings` row and no templates** — precisely the "silently non-functional for email"
state the comment at `:31-36` says this code was moved here to prevent, and it is not self-healing from
the Web process. The other two only risk a lost audit row. Wrap in `BeginTransactionAsync`.

### T-57 — `LicenseWatchJob`'s slot guard covers one of the two jobs it runs
`Worker/LicenseWatchJob.cs:72-73,102-106` · S · Confirmed — the guard keys only on a successful
`"LicenseWatch"` row, though two independent history rows are written. If `LicenseWatch` succeeds and
`VeLicenseWatch` throws, the next tick returns early and **VE license refresh never retries that day** —
one green row beside one red one, no retry. The comment at `:96-99` claims separate rows prevent exactly
this; the code does not have that property.

### T-58 — Zoom-only teams re-PATCH every future session on every poll
`Core/Scheduling/SessionEventSchedulingService.cs:246-249,302` · M · Confirmed — a session settles only
when both Zoom and Discord ids are non-null, so a team using Zoom deliberately without Discord never
settles and the `else` branch fires every tick: ~2,880 PATCH calls/day for 10 sessions, forever, for
data that has not changed. **Note the settle check itself is correct** (`succeeded`-only, per the
documented aggregate-gate rule) — the gap is that there is no per-integration stamp.
**Fix** `ZoomSyncedStartUtc` / `DiscordSyncedStartUtc`.

### T-59 — `ConfirmEmail` applies a VE's email change on an unauthenticated GET
`Pages/VeSelfService/ConfirmEmail.cshtml.cs:26-31` · S · Confirmed (prefetch impact: Likely) —
link-prefetching mail gateways and URL scanners routinely fetch emailed links, silently confirming the
change. Same shape at `Enter.cshtml.cs:26-47`, where a scanner burns the single-use sign-in token and
the VE sees "no longer valid". **`Enter`'s GET consumption is deliberate and documented** — weigh
carefully before changing it. Render a confirmation page with a POST button for `ConfirmEmail` at least.

## Performance

### T-60 — Four uncached `COUNT` queries on every authenticated page render
`Pages/Shared/_AppLayout.cshtml:10,35` → `Core/Navigation/NavBadgeCountService.cs:41,46,48,56` · S ·
High confidence
No caching exists anywhere in the app. `CountSessionsPendingVecSubmissionAsync:70-76` is the expensive
one — a correlated `Candidates.Any(...)` per session with no index on `VecSubmissionStatus`. The sibling
banner on the same layout was explicitly cached for this exact reason
(`IngestionHealthCache.cs:7`: *"renders on every page request"*).
**Fix** `IMemoryCache` keyed by the team-id set with a short TTL, mirroring `IngestionHealthCache`.

### T-61 — Move `CreateScope()` inside the per-team loop
`Worker/SessionIngestionJob.cs:58`; `Jobs/PerTeamDailyJob.cs:36-48`; `Worker/ReconciliationJob.cs:44-61`
· S · High
One scoped DbContext serves the whole tick, so everything team A materialized stays tracked through
teams B and C, and `DetectChanges` walks a growing graph on every deliberate per-item save. Also sets the
blast radius for T-02's `ChangeTracker.Clear()`. **Coordinate with T-02.**

### T-62 — SMTP reconnects per recipient, on the request path
`Core/Email/SmtpEmailSender.cs:66-76`; `VeSessionInvitationService.cs:116-156` · M · High
Connect + TLS + AUTH + send + disconnect per message, looped serially — a 30-VE invitation is 30 full
handshakes inside one POST, plausibly 10–15s to first byte. Add a batch overload that connects once.

### T-63 — CSV VE import is an N+1 on a UI bulk action
`Core/VolunteerExaminers/VolunteerExaminerImportService.cs:191-193,221,234` · S · High
`ParseAsync` already loads and tracks every VE; the loop then re-queries per row with `FirstAsync`
(always round-trips, unlike `FindAsync`) and saves twice. A 176-row import is ~500 round trips.
**Per-item durability is not the point here** — the audit row is saved once at the end, so the operation
is already not per-item atomic. Build a dictionary from the loaded list; one save after the loop.

### T-64 — `AsNoTracking` absent from essentially the entire read path
6 uses against 88 `ToListAsync` · S per site · High
**Read this before bulk-applying:** `Team` carries five encrypted columns, so each materialized `Team`
costs five `Unprotect` calls, currently deduped by identity resolution. Plain `AsNoTracking()` *disables*
identity resolution and would multiply decryption by row count. Use
`AsNoTrackingWithIdentityResolution()` on any query with `Include(... .Team)`, or project.
`SessionAccessScope:170-174` already documents hitting this.

### T-65 — Two job-slot checks cannot use their index; `JobRunHistories` has no retention
`Worker/UlsWatcherJob.cs:53-54`; `Worker/LicenseWatchJob.cs:72-73` · S · High
Both filter on `JobName` + `StartedUtc` but not `TeamId`, and the only index is
`(TeamId, JobName, StartedUtc)` — with `teamId: null` passed, SQLite cannot seed it, so it full-scans a
table nothing ever deletes from (~150k rows/year at 6 rows per team per tick).
**Fix** Add `h.TeamId == null` to both predicates (makes the existing index usable, no migration), and
file a retention pass.

### T-66 — Add index on `Payments(CandidateId)`
· S · High — the only index leading with `CandidateId` is `(CandidateId, Reason)` **filtered** to
`Reason = InitialExam`, and SQLite uses a partial index only when the query's WHERE implies its filter.
So `Include(c => c.Payments)` and `c.Payments.Any(...)` full-scan today.

### T-67 — VE Directory materializes then filters, so it can never page
`Core/VolunteerExaminers/VolunteerExaminerDirectoryService.cs:67-71,108,199-211` · M · High
Three Includes, tracked, whole roster materialized, then guest/license/worked filters in C#. The license
filter genuinely cannot translate; the consequence is no paging path at all.
Related: `GetPersonAsync:216-221` chains **three sibling collections** (cartesian explosion) — prime
`AsSplitQuery`; same shape at `VeSessionInvitationService.cs:49-50`.

### T-68 — Ten page models never propagate a CancellationToken
`Vecs`, `Teams`, `TeamMaintenance`, `JobRunHistory`, `SessionManager/Index`, `FeeConfigurations`,
`EmailTemplates`, `CandidateDetail`, `AuditLog`, `Users` (plus `VeRoster:111` passing
`CancellationToken.None` explicitly) · XS each · Confirmed
**Read-path only.** `CancellationToken.None` in *write* handlers is deliberate throughout (a write must
not be torn in half by a disconnect) — do not "fix" those. The one write that got it backwards is
`RenewalMonitor.cshtml.cs:150-154`, which threads `RequestAborted` across a row insert and its audit
entry.

---

# P2 — Dead code, duplication, tidiness

## Do these first — they unblock other work

### D-01 — Fix two NUL bytes that make files invisible to ripgrep
`Pages/SessionManager/VeInvite.cshtml.cs:49`, `wwwroot/js/app.js:244` · XS · Confirmed
A literal `U+0000` in `"\0untagged"` makes ripgrep classify the file as binary and **silently suppress
all matches**. This already produced a false "delete this" verdict during the audit — one sweep
concluded `VeSessionInvitationService` was a dead DI registration, when `VeInviteModel:26` injects it;
acting on it would have crashed the VE-invite page.
**Fix** `"\0untagged"` in both. Exactly these two files are affected (whole solution audited).

### D-02 — `LicenseWatch` runs at 08:00 ET, not 06:00 — three places say otherwise
`Core/Jobs/JobSchedules.cs:110` (dead constant), `Worker/LicenseWatchJob.cs:13,23-26` (doc comment),
`CLAUDE.md:208` · XS · Confirmed
`LicenseWatchStartHourEt = 6` has zero callers; the live descriptor uses `StartHourEt: 8` and the job
reads the descriptor. The class doc also claims "once a day" and that the hour is a constant not a
settings row — both false since 2026-08-06 (it reads `UlsWatcherStartHourEt`, default 8, and
`UlsWatcherIntervalHours`, default 12).
**Fix** Delete the constant, correct the doc comment and CLAUDE.md.

## Dead code

| ID | Item | File:line | Action | Risk |
|---|---|---|---|---|
| D-03 | `PurgeSpentTokensAsync` never called | `VeSelfServiceLinkService.cs:174` | **Unfinished job, not dead text** — the table grows forever and rows carry `SentToEmail`. Wire into `PiiPurgeJob` or delete. | Low |
| D-04 | `SessionChips.StatusSortKey` | `Web/SessionChips.cs:51` | Delete; reword `Index.cshtml.cs:378` comment | Low |
| D-05 | `VolunteerExaminerImportService.KnownColumns` | `:27` | Delete — parser reads the header dynamically | Low |
| D-06 | `VeRosterSyncResult.VolunteerExaminersUpdated` | `VolunteerExaminerSyncService.cs:343` | Test-only. Its doc claims it preserves `ToString()`'s shape; `ToString()` no longer includes it. Delete property + assertion | Low |
| D-07 | `RenewalMonitorModel.TeamSummaryLabel` | `RenewalMonitor.cshtml.cs:44,200` | Computed and discarded — that page uses a pill row, not a kebab label. Delete both lines | Low |
| D-08 | `ZoomMeetingWireBreakoutRoomEntry.Participants` | `Zoom/ZoomModels.cs:65` | Ships `"participants": []` outbound. **Verify against Zoom before removing** | Med |
| D-09 | 6 cross-host DI registrations | Web `Program.cs:127,128`; Worker `Program.cs:79,116,119` | Clarity only — unresolved scoped registrations cost nothing. Low payoff | Low |
| D-10 | 3 members that should be `private` | `CandidateEmailHistoryFormatter.cs:46`, `VeSelfServiceAuth.cs:19`, `DevAuthSeeder.cs:18` | Reduce visibility | Low |
| D-11 | `SQLitePCLRaw.bundle_e_sqlite3` explicit pin | `Core.csproj:19` | Already transitive — but the pin may force a native bundle on Linux. **Verify before touching** | Med |
| D-12 | `Serilog.AspNetCore` in Worker | `Worker.csproj:12` | Worker uses only `AddSerilog`. Narrow it; check the Console sink still resolves | Med |
| D-13 | Dead filter on a non-nullable projection | `ReconciliationService.cs:72` | `.Where(s => s.ExamToolsSessionId != null)` over a `required string` — always true. Delete | Low |
| D-14 | Singleton read not pinned | `Jobs/JobScheduleService.cs:89` | No predicate on a singleton table, unlike every other reader. Add `Id == SingletonId` | Low |
| D-15 | Skipped VEs never stamped | `VolunteerExaminerLicenseWatchService.cs:53-62` | Rows failing `IsUsable` never get `LicenseLastCheckedUtc`, and null sorts first — they head the window every run forever. Headroom today (2 of 176); once placeholders exceed `MaxLookupsPerRun * 2` the sweep does **zero** real work while reporting `Due = 0` | Low |
| D-16 | Untimed clock in a seeder | `Email/EmailDefaultsSeeder.cs:54` | `DateTime.UtcNow` instead of the injected `TimeProvider` | Low |
| D-17 | Import audit not atomic with its rows | `VolunteerExaminerImportService.cs:164-174,221,234` | A throw partway leaves rows committed with no audit entry, and the entry written links to entity id `0` | Low |

**Latent config bug, not dead code:** `Web/Program.cs:126` registers `ExamToolsUlsLookupClient` but Web
never binds `UlsLookupOptions` — the key exists only in `Worker/appsettings.json:8-10`, not in
`appsettings.Shared.json`. Works only because the code default equals the configured value. Change the
Worker's value and RenewalMonitor's "Add license" silently queries a different host than the nightly
sweep. **Fix when config is next touched.**

**Verified clean, do not re-audit:** all 44 Razor pages reachable (`Public/YouthConfirm` via its emailed
route); no orphan page handlers; all 162 enum members across 40 Core enums referenced; no orphan DB
columns; no commented-out code anywhere; all other NuGet packages used; all wwwroot assets referenced
(the `.ql-*` selectors are generated by Quill at runtime); all 9 hosted services registered and
non-trivial; **no over-fragmentation found** — the small Web helpers each document a real bug they were
extracted to fix.

## Duplication

Ranked by payoff. **The named CLAUDE.md helpers are being used correctly** — `AddAuditLog` (44 sites,
zero inline `new AuditLog {}`), `TerminalStatuses`/`IsTerminal` (9), `ToEmailCredentials`/
`ToSquareCredentials` (15), `CandidatePiiFields.Clear`, `CallSign.IsUsable`, `UlsSchedule.EasternTimeZone`.
The re-derivation failure mode CLAUDE.md warns about has **not** recurred. This is different duplication.

| ID | Cluster | Copies | Proposed home |
|---|---|---|---|
| DUP-01 | **Nine candidate/payment POST handlers, verbatim** — `Detail.cshtml.cs:207-317` ↔ `CandidateDetail.cshtml.cs:45-145`, identical down to every message string; `SetStatus` a third copy at `Index.cshtml.cs:502` | 2–3 (~110 lines) | `RunCandidateActionAsync(candidateId, action, successMsg, errorMsg)` + one message table. **Highest payoff**, and exactly the drift this repo keeps rediscovering (see T-13) |
| DUP-02 | **Session-completion rule spelled out 7× query-side** across 4 files, plus C# copies at `Session.cs:175` and a re-implementation on the projection DTO at `Index.cshtml.cs:675`; `HasEnded` re-implemented at `:678`. `Index.cshtml.cs:311` carries a comment *ordering* a 4th copy to stay identical | 9 | `static Expression<Func<Session,bool>> Session.IsCompletedExpression` (or `IQueryable<Session>.WhereCompleted()`), plus static rule helpers taking primitives so DTOs share them. CLAUDE.md flags this as a twice-live bug class |
| DUP-03 | `TeamSummaryLabel` computed identically in 11 page models | 11 | `TeamPicker.Label(...)` beside `GetAvailableTeamsAsync` |
| DUP-04 | **Team-picker Razor markup**, 13 copies. Three files already carry comments saying "Same team-picker component as the session list" — the intent is documented, only the partial is missing. `RenewalMonitor:20-30` is a **pill row instead**, its own inconsistency (1 of 13 looks different) | 13 + 1 variant | `Pages/Shared/_TeamPicker.cshtml`. Context: 44 pages share only 4 non-layout partials |
| DUP-05 | `GetAvailableTeamsAsync` re-implemented inline 5× — the helper's own doc says it was extracted to stop this | 5 | Mirror it on `AdminAccessScope`. **Note:** these do *not* lose the credential-decryption perf fix (all five project in SQL) — pure duplication |
| DUP-06 | The required-user throw, 21 copies across 14 files, against 49 `GetUserWithManagerAsync` call sites | 21 | `CurrentUserLoader.GetRequiredUserAsync`. Trivial, high leverage |
| DUP-07 | Role strings as literals, 24 occurrences, 3 values — 19 in `[Authorize]` **plus 5 in `.cshtml`** via `ParentCrumb`, which must *mirror* the target page's attribute or silently link to a 403 | 24 | `RoleGroups` consts beside `RoleLandingPages.cs` |
| DUP-08 | `AuthorizeAsync()` page-model helpers, 2 shapes | 5 | Two extension methods beside `CurrentUserLoader` |
| DUP-09 | Money formatting, ~16 hand-written sites, 2 spellings. CLAUDE.md documents the `"C"`-format trap but no helper was created | 16 | `Money.Format(decimal)` in Core |
| DUP-10 | Payment chip + candidate capability rules — 7 identical boolean expressions | 2 | `SessionChips.Payment(...)` + a `CandidateCapabilities.For(Candidate)` record |
| DUP-11 | Anchored-daily-job guard (`UlsWatcherJob:38-89` ↔ `LicenseWatchJob:44-108`, verbatim comment); `ReconciliationJob:33-65` re-implements `PerTeamDailyJob:24-52` | 2 + 1 | An `AnchoredDailyJob` base; make `PerTeamDailyJob.RunForTeamAsync` generic. **Coordinate with T-57** |
| DUP-12 | VE token mint/hash/send (already cosmetically drifted); system-sender envelope 3× | 2–3 | `OneTimeToken.Mint()/Hash()`; `SystemSettings.ToSystemEmailMessage(...)` beside the existing `ToSystemEmailCredentials()` |
| DUP-13 | Modal shell markup, 20 copies across 8 files; `frnModal` and `refundModal` are true content duplicates | 20 | A `<modal>` tag helper (the repo already ships tag helpers); those two pairs become partials |
| DUP-14 | `ConvertTimeFromUtc(SpecifyKind(x, Utc), EasternTimeZone)` 5× — all correctly reuse the shared zone, only the incantation repeats. Plus 46 `TempData` magic strings | 5 / 46 | `UlsSchedule.ToEastern(DateTime)`; TempData key constants |

**Not worth touching:** `page-head` markup (26×, cosmetic), CSV download tails (3×), FRN/email `.Trim()`
normalization (2× each). Name formatting is already centralized on a single `Candidate.Name` field.

## Size / shape

| ID | Item | Size | Action |
|---|---|---|---|
| S-01 | `RenewalMonitor.OnPostAddAsync` | 71 lines | **Highest-value shape fix.** The only page model in the solution doing real domain work — ULS lookup, validation, dedupe, entity creation, audit, double save — and it reaches into `LicenseWatchService.Apply`, which appears to be `public static` solely for this. Move to `LicenseWatchService.AddWatchedLicenseAsync`; `Apply` goes private. (Verified: only 2 page models touch `dbContext` at all; the other is an export audit entry. The codebase is otherwise disciplined here.) |
| S-02 | `SessionIngestionService` | 774 lines | `ImportHistoricalRangeAsync` + 3 helpers (~140 lines) is a separate responsibility driven by `HistoricalImportService:156`. **Caveat:** it reuses `TryCreateSessionAsync`/`SyncCandidates`, so this is a partial extraction, not a clean lift |
| S-03 | `SessionIngestionService.RunAsync` | 194 lines, ~40 branches | ~half is load-bearing comment. Five phases → `FetchRemoteSessionsAsync`, `MergeSessionUpdates`, `DetectCancellations`, `SyncCandidatesForOpenSessions`. Comments travel with their phase |
| S-04 | `AppDbContext.OnModelCreating` | 340 lines, 27 blocks | → `IEntityTypeConfiguration<T>` + `ApplyConfigurationsFromAssembly`. Mechanical, low risk |
| S-05 | `VolunteerExaminerDirectoryService.GetDirectoryAsync` | 153 lines | Four phases → `BuildMembershipQuery`, `LoadSessionStats`, `LoadDuplicateCallSigns`, `ToRows`. **Coordinate with T-67** |
| S-06 | `VolunteerExaminerSyncService.RunAsync` | 143 lines | Lookup-building phase (4 dictionaries) → a `RosterLookups` record. **Coordinate with T-47, T-51, T-52** |
| S-07 | `PaymentGenerationService.RunAsync` | 135 lines | Split eligibility filtering from link generation. **Coordinate with T-50** |
| S-08 | `Admin/Users.cshtml.cs OnGetAsync` | 111 lines | Extract filter/scope resolution |
| S-09 | `Detail.cshtml.cs` | 570 lines, 15 handlers | Each handler is 8–11 lines and correctly delegates — the ASP.NET idiom, not a defect. The 9 sharing a verbatim preamble fold into DUP-01 |
| S-10 | `SessionManager/Index.cshtml.cs` | 754 lines | Well-factored internally. The one seam is filter-state + cookie persistence (`:230-263`, `:549-632`) → a `SessionListFilter` type. **Lower priority than its line count suggests** |

## Low-severity security / hygiene

| ID | Item | File:line |
|---|---|---|
| L-01 | Filter cookie missing `HttpOnly`/`Secure` (values are allowlist-validated on read, so tampering is contained) | `SessionManager/Index.cshtml.cs:609-621` |
| L-02 | HSTS at defaults (30d, no `includeSubDomains`/`preload`); no `__Host-` cookie prefixes (incompatible with the self-service cookie's deliberate `Path`) | `Program.cs:443` |
| L-03 | CI trusts whatever host key is presented (`ssh-keyscan` per run, fresh runner). Mitigated by WireGuard | `deploy.yml:85` |
| L-04 | ExamTools username logged — the single deviation from otherwise perfect ids-only logging. Arguably justified given ExamTools attributes every action to that account | `ExamToolsClient.cs:154` |
| L-05 | No 2FA; lockout at framework defaults. Partly offset by the 20/min per-IP limiter, which is the better control here | `Program.cs:167-181` |
| L-06 | Audit log has no retention and no tamper-evidence — append-only by convention (verified: no update or delete path in `src/`), not by DB enforcement. Growth tracked as #86 | `AppDbContext.cs:313-315` |
| L-07 | VE personal data has no retention policy — an unstated asymmetry with the candidate retention promise on the Privacy page. Access control is correct and export is audited | `Entities/VolunteerExaminer.cs:68-74` |
| L-08 | `RenewalMonitor` is `[Authorize]` with no role, so a "read-only" TeamLead can write. Team scoping is correct; the class doc says this is deliberate. **Likely intended** — worth an explicit note in `docs/admin-auth.md` so the invariant isn't read as universal | `RenewalMonitor.cshtml.cs:31` |
| L-09 | A TeamAdmin can create a user they can then never configure (`CanManageUser` requires a shared team; a new user has none). Fails closed — but the button is offered and cannot be completed | `Users.cshtml.cs:149-165` vs `AdminAccessScope.cs:66-81` |
| L-10 | `[Required]` on a non-nullable `int` (`ResetPassword.InputModel.UserId`) is client-side-only, same trap as the documented `bool` case. Not exploitable (0 is never a real key, and the token check fails regardless) but reads as enforcement | `ResetPassword.cshtml.cs:28` |
| L-11 | `VeDirectory` Export writes an audit row on a **GET** — a cross-site `<img>` can make an admin emit a `VeDirectoryExported` entry. No PII leaves (CSV isn't readable cross-origin) but it pollutes the trail that exists to attest who exported PII | `VeDirectory.cshtml.cs:130-168` |
| L-12 | `ContinueWith(t => t.Result ...)` throws `AggregateException`, not the intended exception, and runs on `TaskScheduler.Current`; also a redundant user load | `VeDetail.cshtml.cs:233-235`, `Admin/Reconciliation.cshtml.cs:88-90` |
| L-13 | Upload size cap on the preview path only (bounded by `MaxRows = 500` and the framework form limit) | `VeImport.cshtml.cs:78-95` |
| L-14 | `OnGetAsync` returns `Task` and bare-`return`s on no user → blank page instead of 403 | `FeeConfigurations.cshtml.cs:32-38` |
| L-15 | Six pages have validation attributes but no `_ValidationScriptsPartial` (server messages still render — UX only) | `ChangePassword`, `MyVeDetails`, `VeDetail`, `VeInvite`, `VeSelfService/Details`, `VeSelfService/SignIn` |
| L-16 | Dead `href="#"` on a public candidate-facing page (flagged TODO in the copy above it) | `Public/YouthConfirm.cshtml:18` |
| L-17 | Hardcoded string URL instead of the tag helper | `_IngestionHealthBanner.cshtml:34` |
| L-18 | Three columns carry an `AddColumn` SQL default the model doesn't declare, so a model-built schema (`EnsureCreated`, i.e. the SQLite tests) differs from the migrated one. Identical to the drift `AppDbContext.cs:344-348` already calls out and fixes with `HasDefaultValue` | `Candidates.FccHoldReason`, `Candidates.FccPaymentStatus`, `AspNetUsers.MustChangePassword` |
| L-19 | Stale doc comments: `VeDetail.cshtml.cs:22-26` says email is not editable here (it is, and it decides who receives self-service links); `EmailDefaultsSeeder:127-129` still describes the felony email as automatic (untrue since #221); `IUlsLookupClient:109` is named `LookupByFrnAsync` but two of three callers pass a call sign | various |
| L-20 | `DiscordEventClient` accepts a `CancellationToken` on every method and passes it to no Discord.Net call | `Discord/DiscordEventClient.cs:34,52,66,76,83,94` |
| L-21 | `ExamToolsClient.GetOrCreateTeamSession` can dispose an `HttpClient` another thread is mid-request on. Narrow today (jobs are sequential) | `ExamToolsClient.cs:170-180` |
| L-22 | `DailySlotSchedule` throws for a slot hour inside the spring-forward gap. Guarded in the Worker by `JobTick`; **`JobScheduleService` has no such guard, so Admin → Job Schedule 500s** | `Uls/DailySlotSchedule.cs:51,72` |
| L-23 | `AuthorizeSessionAsync` returns null for "no user", "not found" and "not permitted" alike → a deleted session answers 403 instead of 404. `Detail:159-161` distinguishes them | `SessionManager/Index.cshtml.cs:425,489-499` |
| L-24 | No optimistic concurrency token anywhere (no `IsRowVersion`/`[Timestamp]`). The one race with money attached is closed by a unique index and handled explicitly; the rest are last-writer-wins on disjoint fields. Recorded because "why is there no rowversion" otherwise has no answer in the repo | solution-wide |
| L-25 | No `MaxLength` anywhere in the model — every string column is unbounded TEXT. SQLite enforces nothing, so no live defect; the two places a cap matters are enforced in code only | solution-wide |
