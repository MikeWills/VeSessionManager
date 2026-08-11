# Full-Codebase Audit — 2026-08-11

Nine parallel specialist reviews of the whole solution: three security, two optimization, four
traceability (one per layer). Every agent was told to verify claims against source and quote lines,
because the 2026-08-03 audit produced five findings that were wrong on re-check — including one that
would have deleted a live authorization check.

**Companion file: [`audit-2026-08-11-tasks.md`](audit-2026-08-11-tasks.md)** — the same findings as
discrete, pickable work items with IDs, file paths, fixes and verification steps. That file is the one
a sub-agent should read. This file is the one a human should read.

---

## The short version

The codebase is in good health, and that is not a courtesy sentence — it is the finding. Zero build
warnings, 1,156 passing tests, zero raw SQL, zero `Html.Raw`, zero committed secrets, no vulnerable
packages, no PII in any of 175 log statements, no orphan schema columns, no unreachable pages, and the
shared helpers CLAUDE.md prescribes are genuinely being used rather than re-derived. The schema, the
model and the migration chain were verified to agree exactly, by diffing DDL built three separate ways.

**Nothing rated Critical. Nineteen findings rated High.** Those cluster into five recurring shapes
rather than nineteen unrelated bugs, which is the most useful thing this audit produced — fix the
shape and you fix the instances, including the ones nobody has found yet.

| Area | High | Medium | Low | Verified clean |
|---|---|---|---|---|
| Security — auth & tenancy | 1 | 3 | 5 | Extensive (see below) |
| Security — injection & web surface | 0 | 2 | 6 | Extensive |
| Security — secrets, PII, deps | 2 | 6 | 7 | Extensive |
| Optimization — dead code & duplication | — | 14 clusters | ~25 items | — |
| Optimization — performance | 4 | 8 | 6 | — |
| Traceability L1 — markup → handlers | 1 | 3 | 3 | 71/71 handlers wired |
| Traceability L2 — handlers → services | 3 | 10 | 14 | 63/63 arg orders correct |
| Traceability L3 — services → database | 5 | 12 | 6 | Schema agreement proven |
| Traceability L4 — jobs & external APIs | 3 | 3 | 5 | 10/10 jobs guarded |

---

## The five recurring shapes

### 1. `ChangeTracker.Clear()` used as a per-row error handler

Found independently by two agents, at three sites, in code written months apart.

The pattern: a loop processes rows, one throws, the catch calls `dbContext.ChangeTracker.Clear()` to
recover. But `Clear()` detaches **everything**, including the entities the loop already mutated and
the ones it has not reached yet. The loop keeps assigning properties to now-detached objects, the
final `SaveChangesAsync` writes nothing, and the counters still increment.

- `VolunteerExaminerLicenseWatchService.cs:97` — one FRN collision on VE #7 of 250 means VEs #8–250
  are mutated detached. The job reports "checked 243", writes zero rows, Job History renders green,
  and because the `LicenseLastCheckedUtc` stamp never persisted, the next run does it all again.
- `JobRunHistoryLogger.cs:126/134/155` + `SessionIngestionJob.cs:88` — a failed pipeline step clears
  the tracker, detaching the `Team` the job then stamps `LastIngestionRunUtc` on. That stamp is what
  throttles ingestion to hourly. It silently stops persisting, so that team re-ingests every 300s
  forever, and nothing logs it.
- `VolunteerExaminerSyncService.cs:178` is the mirror image — it catches per session and *doesn't*
  clear, so a poisoned tracker is re-attempted by every subsequent session's save.

The correct move is a **scoped** detach of the failing entity, not a global one. Two services took
opposite halves of the same lesson and both got it wrong.

### 2. `VolunteerExaminer` is a global person; the code around it assumes team scoping

The 2026-08-07 change that made a VE a person rather than a per-team row was right, and is documented.
What did not follow is the access model. Five sites across three independent agents:

- **The one that matters most:** a TeamAdmin can type any call sign into VeDirectory's "Add VE" and
  pull that person onto their own team — no check that they may reach the person, only that the team
  is theirs. They then get that VE's home address, phone, email and notes, can overwrite them, and can
  export them to CSV. `VeImport` does it 500 at a time.
- `VeMerge` scopes the record you start from and not the record you merge in — so a cross-team merge
  can retire another team's VE record irreversibly. (Found by two agents.)
- `VeSessionInvitationService` doesn't scope recipients, so a tampered POST mails attacker-authored
  text from the team's SMTP to any VE on the deployment.
- `SetVolunteerExaminerAsync` lets an admin claim any VE row as their own login's identity.
- The CSV import *preview* is a cross-tenant existence-and-name oracle, 500 probes per request, no
  audit entry.

There is a real design question underneath: should cross-team reach be scoped away, or should it
require SystemAdmin? Both are defensible. What is not defensible is the current state, where the
survivor is scoped and the duplicate is not.

### 3. Failure that renders as success

Five instances, and the repo has a documented history of exactly this shape (`sent 0, failed 1`).

- `_PublicLayout.cshtml` has no TempData block, so **all 13** status and error messages on the VE
  self-service page — the app's only unauthenticated PII-editing surface — render as nothing. A VE
  whose email change was rejected sees a byte-identical page.
- `ManualRefreshResult` has no failure channel, and `JobRunHistoryLogger` catches without rethrowing,
  so a total pipeline failure returns `(0,0,0)` and the admin gets a green *"Refreshed HRCC — 0 new
  candidates"*.
- `DataProtectionKeyRingGuard` checks five of the six encrypted columns. On a wrong key ring it logs
  "verified" while the system SMTP password is a `CfDJ8…` blob — and password-reset mail then fails
  silently, because `PasswordResetService` deliberately swallows send failures to avoid an enumeration
  oracle.
- The VE license sweep counter above.
- `MarkCompletedAsync`'s three-value result collapsed into two branches on the session list, so
  "session not found" reports as "already marked submitted" — and the correct three-branch fix already
  exists on the detail page, with the comment explaining it. The list copy was never updated.

### 4. Unbounded historical scans

`Session.Status == Active` means "not cancelled", never "not yet happened", and CLAUDE.md documents
two live bugs from it. The full sweep was done: **13 occurrences, all correct** — both historical
instances are genuinely fixed.

But the bug class survived in two other forms:

- `VolunteerExaminerSyncService:97` reads as fixed because the *HTTP calls* were bounded — the fix
  landed in memory, after materialization. The query still loads every session the team has ever run,
  with rosters and VEs, every tick, to discard them on the next line.
- `SessionIngestionService:254` loads the team's entire session→candidate→payment graph, tracked, with
  two nested collection Includes. Every other consumer of this data got a date window added after the
  historical import; this one never did.
- `UlsWatcherService` is the only watcher of three with no per-run cap, so every unresolved candidate
  is one HTTP call against a third party, twice daily, forever.

### 5. The test suite renders pages but never posts to them

~110 form-posting handlers exist. **One** is tested. The page smoke tests are genuinely good — they
discover every route from the app's own `EndpointDataSource`, so a new page is covered the day it
exists — but they GET. Nothing verifies that a form's `name=` attributes bind to the handler's
parameters, which is precisely why all three Layer-1 binding defects were live and invisible.

The cheapest high-value addition is not more tests but two *source-scanning* tests, in the same shape
as the existing `InlineEventHandlerTests`: one that parses every form's handler + field names and
reflects over the page model for a match, and one that generalizes the existing empty-`href` check
(currently run on one page) to all 46.

---

## Things worth knowing that aren't bugs

Several agents disproved claims they nearly filed. These are recorded so nobody re-derives them:

- **WAL is already enabled** — EF Core turns it on for databases it creates, and the live file header
  confirms it. So readers never block the writer, and the "database is locked" symptom is
  **writer-vs-writer** between Web and Worker. That reframes the fix as shortening write transactions,
  not enabling WAL. It also means `BACKUP.md:35` ("rollback-journal mode, not WAL") is wrong and the
  deploy's `.db`-only snapshot silently drops every uncheckpointed transaction.
- **A NUL byte in `VeInvite.cshtml.cs:49` makes the file invisible to ripgrep**, and it already caused
  one agent to recommend deleting a DI registration that a live page injects. Two files affected.
  Fixing them unblocks every future grep-based review — worth doing first, as a tooling fix.
- **`decimal` is stored as TEXT** by the SQLite provider. Exact, no rounding — but any server-side
  comparison or ordering would be lexicographic. Every money operation currently happens in memory, so
  round-trip fidelity holds. Worth knowing before anyone writes `.Where(p => p.Amount > 15m)`.
- **`Microsoft.Data.Sqlite` has no async I/O.** `ToListAsync`/`SaveChangesAsync` run synchronously, so
  every performance win here comes from doing less work, never from more concurrency.
- **NuGet audit is active and `TreatWarningsAsErrors` is on solution-wide.** A newly disclosed advisory
  in any transitive package will fail the build. That is the right default; know it before a release.
- **`GroupBy(...).Select(g => g.OrderByDescending(...).First())` does translate** on EF Core 10 +
  SQLite — verified against real in-memory SQLite rather than the InMemory provider.

---

## What was verified clean

Recorded so it does not get re-audited, in the same spirit as the 2026-08-03 file's most useful section.

**Auth.** The three-barrier VE self-service separation genuinely holds, and the load-bearing part is
that `VeSelfServiceAuth.BuildPrincipal` adds no role claim, so every `[Authorize(Roles=…)]` fails
closed for a VE principal. Token hygiene is correct throughout: 32 CSPRNG bytes, SHA-256 at rest,
single-use, short-lived, non-enumerable responses. Password flows are non-enumerable end to end and
build links from `App:PublicBaseUrl`, never the request host. A TeamAdmin cannot escalate role, create
a team, change anyone's password, or touch a TeamAdmin/SystemAdmin target. Every id-taking POST
handler on the session/candidate/payment surface re-checks ownership. The documented
`GetEffectiveTeamIds(...)?.Contains(id) ?? false` SystemAdmin trap was checked in both directions and
found nowhere.

**Injection.** Zero `Html.Raw`/`HtmlString`/`MarkupString` in the entire solution. Zero inline event
handlers (enforced by a test). Zero raw SQL, no `Process.Start`, no `BinaryFormatter`, no XXE, no path
traversal (uploads never touch disk). CSV export neutralizes formula injection. Sort keys are
allowlisted before reaching a hardcoded switch. No open redirect — `Login` accepts no `returnUrl` at
all. The Square webhook does everything right: raw body, constant-time HMAC, 401 on failure, replay
harmless, cross-team application blocked.

**Secrets & PII.** No committed secrets anywhere, verified against provider-prefix patterns as well as
by inspection. `.gitignore` correctly covers the DB, its `.bak-*` snapshots and the key ring — checked
with `git check-ignore`, none tracked. No hand-rolled crypto. The dev seeder cannot run in Production.
The one-off Worker switches correctly run *after* the key-ring guard. Candidates store no SSN, no DOB,
no address, no phone — and the purge is reflection-tested for completeness, with no shadow copies in
audit, job-history or reconciliation tables.

**Structure.** All 71 UI-referenced handlers exist. All 63 multi-same-type-parameter call sites pass
their argument-order check, including `MergeAsync(survivor, duplicate)` and every meeting point of the
new `User.Id` / `VolunteerExaminer.Id` split. All 10 jobs are registered exactly once, wrapped in
`JobTick.GuardedAsync`, and idempotent. The persisted-idempotency-key pattern (Square) and
query-before-create (Discord) are correctly implemented. The `!IsConfigured || succeeded` aggregate-gate
antipattern appears nowhere. Every seeded email template has a sender and vice versa. No DI
registration is missing and no singleton captures a scoped service.

---

## Where the effort is best spent

1. **Two NUL bytes** (10 minutes). Unblocks grep for every future review, including the follow-up work
   from this audit.
2. **The `ChangeTracker.Clear()` shape** (half a day). Three sites; the ingestion-throttle one is
   actively degrading production behavior right now and nothing surfaces it.
3. **Decide the VolunteerExaminer access model** (a conversation, then a day). This is the only finding
   here that needs a *decision* before it needs code. Five bugs collapse into one answer.
4. **`_PublicLayout` TempData block** (10 minutes) — one edit restores every error message on the VE
   self-service surface.
5. **The two source-scanning tests** (half a day). They would have caught all three Layer-1 defects and
   will catch the next one, which matters more than fixing these three.
6. **The four uncached `COUNT`s in `_AppLayout`** (an hour). They run on every authenticated page
   render; the sibling banner on the same layout was explicitly cached for exactly this reason.
7. **Ops: narrow the `rsync *` sudoers grant, fix the WAL-unaware backup, build an off-box backup.**
   These are the highest-consequence items in the whole audit and none of them are code.

Duplication is real and quantified — roughly 600 removable lines concentrated in the Web layer — but it
is maintenance cost, not risk. It belongs after the above.

---

## A note on process

Per CLAUDE.md, GitHub issues are the single list of outstanding work and new work belongs there rather
than in a markdown file. The companion task file exists because it was asked for and because it is the
right shape for sub-agents to pick from. **These items should become issues** before they are worked,
or the repo acquires a fourth parallel "what's left" list — the exact problem the 2026-08-10
consolidation solved. The task file is structured so each item maps to one issue cleanly.
