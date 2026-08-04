# PII Purge Job (Phase 10)

What `PiiPurgeService` (`VeSessionManager.Core/PiiPurge/`) does and depends on. Final phase in
`docs/spec.md` — no new external API or schema (`Candidate.PiiPurgedUtc` and
`SystemSettings.PiiRetentionWindowDays` already existed from Phase 0/Phase 9c respectively;
confirmed via a throwaway `dotnet ef migrations add` that produced an empty migration, same habit
as Phase 8).

## Global, not per-team

Every prior phase's scan-based service either operates per-`Team` (Ingestion, Zoom/Discord, Square,
Email) or has no team concept at all (FCC watcher). This one is global for a different reason:
`SystemSettings.PiiRetentionWindowDays` is a single deployment-wide value (Phase 9c), not a `Team`
credential/setting — one retention policy applies to every team's candidates. `PiiPurgeJob` follows
`FccDailyWatcherJob`'s wrapper shape (no per-team loop, `teamId: null` passed to
`JobRunHistoryLogger.RunAsync`), not `PaymentReminderJob`'s per-team loop.

## Two triggers, one shared purge action

`PiiPurgeService.RunAsync` reads `SystemSettings.PiiRetentionWindowDays` fresh every run (via
`SystemSettingsService.GetAsync`, not cached) — **no default is assumed**: a `null` value (the
seeded default) means the job no-ops with one aggregate `INFO` log line, same "skip quietly" idiom
as an unconfigured optional integration, even though this isn't an external-API client.

1. **Trigger A — passed candidates.** `LicenseGrantDateUtc` is set and at least
   `RetentionWindowDays` days old.
2. **Trigger B — failed candidates.** `ApplicationStatus = Failed` and the candidate's
   `Session.ScheduledStartUtc` is at least `RetentionWindowDays` days old — anchored to the exam
   date instead of a license grant, since there's no FCC process left to track once a Session
   Manager has recorded a failing result.

Both triggers use `PiiPurgedUtc == null` as the query filter and idempotency guard, same idiom as
every other phase's `...Utc`/flag tracking field — a candidate already purged is never reprocessed.

**`NotTested` is deliberately excluded from both triggers.** No-show/withdrawal PII is nulled
*immediately* by `CandidateActionService`'s Phase 9 delete action, at the moment a Session Manager
takes it — not on this scheduled window. `Unmatched`/`Received` candidates never match either
trigger regardless of session age: `LicenseGrantDateUtc` is only ever set on `Granted`, and Trigger
B's own `ApplicationStatus == Failed` filter excludes them — no separate non-terminal-status check
needed, same "excluded as a side effect of the date-null filter" reasoning `docs/payment-reminders.md`
already documents for its own Unmatched exclusion.

Purge action (either trigger, same field set `CandidateActionService`'s delete action already
nulls): **null** `Candidate.Name`/`FirstName`/`Email`/`HasFelonyDisclosure` and
`Payment.PaymentLinkUrl`/`SquarePaymentReferenceId` on every associated payment, set
`Candidate.PiiPurgedUtc` to now. **Preserved:** `Frn`, `CallSign`, `LicenseGrantDateUtc`,
`ApplicationStatus`, `SessionId`, and every `Payment.Amount`/`Status`/`Reason` — needed for
historical session/VE/financial stats. `Frn` was purged until 2026-08-03, when it was reclassified:
an FRN is public FCC data, not PII, and retaining it keeps a purged record traceable if a question
about the candidate's application ever comes up (same reasoning as `CallSign` and the ULS keys).
The Privacy page's retention wording was updated in the same change.

`FirstName` was **missing from the purge entirely** until 2026-08-03 (audit finding T02). It was
added in Phase 4 for the `{{CandidateFirstName}}` email placeholder and never added to
`CandidatePiiFields.Clear`, so every purged candidate kept their given name indefinitely — rendered
on Candidate Detail, and flatly contrary to the Privacy page. Two things came out of that fix:

- **A reflection guard test** (`CandidatePiiFieldsTests`) now enumerates `Candidate`'s properties and
  fails if anything outside an explicit, commented retained-field allow-list survives `Clear`. Adding
  a field to the entity now forces a deliberate decision instead of a silent omission. This is the
  actual fix; the one-line null was the symptom.
- **A self-healing repair pass** (`PiiPurgeService.RepairIncompletelyPurgedCandidatesAsync`).
  Already-purged rows carry `PiiPurgedUtc`, so both triggers skip them forever — the one-line fix
  alone would have helped only future purges. The repair re-runs the whole shared `Clear` on any row
  where `PiiPurgedUtc != null && FirstName != null`, **preserving the original `PiiPurgedUtc`** (that
  date records when retention actually expired, not when the repair ran) and reporting a count in
  `PiiPurgeResult.AlreadyPurgedCandidatesRepaired` — non-zero once, zero forever after. Scan-based
  and idempotent rather than a one-off migration script, the same idiom as the `ExtId` and
  license-class backfills, so it needs no deployment step.

  **Know the limit of that repair:** the *action* is a wholesale `Clear` (idempotent, so it costs
  nothing), but the *detection* is the narrow `FirstName != null`, which is the signature of this
  one historical gap. A field added to `Clear` in future will **not** be repaired on already-purged
  rows, because by then `FirstName` is null everywhere and no row matches. Adding a field to `Clear`
  means widening this predicate too — otherwise the same class of stale row comes back silently.
  The guard test catches the missing `Clear` line; nothing but this note catches the missing repair. Unlike the Phase 9 delete action, this purge never touches
`ApplicationStatus`/`ResultMarkedBy*` — it's a privacy-retention action, not a candidate-status
change.

Each purge writes an `AuditLog` row with `UserId = null` (a background job, not a person — same
idiom as `SessionIngestionService`'s reschedule-flagged entry) and `Details` naming which trigger
fired, so a later audit review can tell a license-grant-anchored purge from an exam-date-anchored
one.

## Boundary math: exclusive upper bound, not `.Date` on the query

"Today − anchorDate ≥ RetentionWindowDays" is expressed as
`anchorDate < today.Date.AddDays(-RetentionWindowDays + 1)` — an exclusive upper bound computed once
in C#, then compared directly against the raw `DateTime` column. This avoids two problems a more
literal translation would hit:

- Calling `.Date` on the entity property inside the LINQ `Where` would need EF Core to translate a
  `DateTime.Date` member access against a real column — the exclusive-range approach only ever
  compares two plain `DateTime` values, same pattern `CandidateNotificationService`'s own
  `tomorrowStartUtc`/`tomorrowEndUtc` range already uses for its "is the session tomorrow" check.
- `Session.ScheduledStartUtc` carries a real time-of-day (the exam's start time), while
  `LicenseGrantDateUtc` effectively doesn't — comparing against a midnight-only threshold would
  wrongly exclude an exam that started later in the day on the cutoff date itself. The exclusive
  upper bound (`< cutoffDate.AddDays(1)`) includes any time-of-day on the boundary day, so both
  triggers get identical calendar-day semantics regardless of which field they anchor to.

Verified with all three boundary cases the spec calls for (exactly at threshold, one day before,
one day after) for both triggers in `PiiPurgeServiceTests`.

## Config

- `SystemSettings.PiiRetentionWindowDays` (nullable, admin-configurable via Phase 9c's System
  Settings screen, seeded `NULL` — spec.md's own "no default is assumed") is the only tunable value
  the purge logic itself reads.
- `Jobs:PiiPurgeIntervalHours` (appsettings, default `24`) controls only how often `PiiPurgeJob`
  ticks, same as every other daily job's own `...IntervalHours` — not the retention window itself.
