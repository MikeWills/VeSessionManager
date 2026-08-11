# Audit — 2026-08-03

Source: a six-agent deep audit (security, optimization/dead-code, and four traceability layers:
UI→handlers, handlers→services, services→EF/SQLite, Worker jobs→services→clients→DB), against the
codebase at commit `2898817` plus an uncommitted footer/version diff.

**Overall verdict: no Critical security findings.** IDOR, CSRF, injection and secrets handling were
all verified clean, and the schema matched the migrations exactly. The 39 findings were real but
bounded.

## The task list moved to GitHub issues (2026-08-10)

**14 of 39 were fixed before the migration** — the P0/P1 tier: authorization (T01), the PII purge gap
(T02), public-internet hardening (T05, T06, T13, T14, T17), Worker resilience (T10, T11), the payment
race (T07, T08), the FRN-is-not-PII decision (T03, T19), and the Square/ExamTools config drift (T04,
done 2026-08-06 before live payment testing).

The remaining 25 are now issues, one per task, keeping their original T-numbers in the title:
→ **[label: `audit-2026-08-03`](https://github.com/MikeWills/VeSessionManager/labels/audit-2026-08-03)**

| | P1 | P2 | P3 | P4 |
|---|---|---|---|---|
| | [T09](https://github.com/MikeWills/VeSessionManager/issues/156) jQuery not loaded | [T15](https://github.com/MikeWills/VeSessionManager/issues/158) fallback authz policy | [T22](https://github.com/MikeWills/VeSessionManager/issues/163) Users dropdown / filter loss | [T26](https://github.com/MikeWills/VeSessionManager/issues/167) `Session.IsCompleted` |
| | [T12](https://github.com/MikeWills/VeSessionManager/issues/157) **key ring beside the DB** | [T16](https://github.com/MikeWills/VeSessionManager/issues/159) auth cookie expiry | [T23](https://github.com/MikeWills/VeSessionManager/issues/164) sort wiped by filter | [T27](https://github.com/MikeWills/VeSessionManager/issues/168) `TeamEmailDispatcher` |
| | | [T18](https://github.com/MikeWills/VeSessionManager/issues/160) silent decrypt fallback | [T24](https://github.com/MikeWills/VeSessionManager/issues/165) `.pill-count` unstyled | [T28](https://github.com/MikeWills/VeSessionManager/issues/169) `CandidatePresentation` |
| | | [T20](https://github.com/MikeWills/VeSessionManager/issues/161) serialize pipeline runs | [T25](https://github.com/MikeWills/VeSessionManager/issues/166) call sign normalize | [T29](https://github.com/MikeWills/VeSessionManager/issues/170) Sessions list `Include` |
| | | [T21](https://github.com/MikeWills/VeSessionManager/issues/162) schema hygiene migration | | [T30](https://github.com/MikeWills/VeSessionManager/issues/171) `AsNoTracking` pass |
| | | | | [T31](https://github.com/MikeWills/VeSessionManager/issues/172) credential helpers |
| | | | | [T32](https://github.com/MikeWills/VeSessionManager/issues/173) money/chip formatters |
| | | | | [T33](https://github.com/MikeWills/VeSessionManager/issues/174) inline `AuditLog` sites |
| | | | | [T34](https://github.com/MikeWills/VeSessionManager/issues/175) split giant methods |
| | | | | [T35](https://github.com/MikeWills/VeSessionManager/issues/176) small perf batch |
| | | | | [T36](https://github.com/MikeWills/VeSessionManager/issues/177) dead-code removal |
| | | | | [T37](https://github.com/MikeWills/VeSessionManager/issues/178) low-severity security |
| | | | | [T38](https://github.com/MikeWills/VeSessionManager/issues/179) Worker polish |
| | | | | [T39](https://github.com/MikeWills/VeSessionManager/issues/180) consistency cosmetics |

**[T12](https://github.com/MikeWills/VeSessionManager/issues/157) leads what's left:** the Data
Protection key ring sits beside the SQLite database, so one leaked backup carries both the ciphertext
and the key that decrypts it.

Each issue carries the original finding's problem / files / fix / acceptance criteria, plus any
correction where the codebase moved on since 2026-08-03. **The line numbers are from commit
`2898817`** and several files have been restructured since — treat them as a starting point, not an
address.

---

## Verified clean — don't re-audit these

This is the part of the audit that isn't a task list, and it exists nowhere else. Its value is
negative work: knowing what *not* to spend a review pass on.

**Security.** Zero raw SQL. Zero `Html.Raw`. Email placeholders HTML-encoded (the subject
deliberately isn't). MimeKit blocks header injection. IDOR ownership re-checks present on every
id-taking POST handler. CSRF correct, including all four explicit-action forms. No secrets in config.
Password-reset flow non-disclosing, throttled, single-use. TLS validation intact. No SSRF. No open
redirects. The Worker exposes nothing. No mass-assignment.

**Traceability.** All ~60 forms, links, modals and JS hooks resolve with matching names and types
(except the items now filed as issues). All service signatures and argument orders verified by
parameter name. All result-enum members handled. DI complete in both hosts, with no scoped-into-
singleton captures. Entity model matches migrations exactly (`has-pending-model-changes` clean). All
EF-translatability, `DateTime.Kind` and null-semantics traps handled at every call site as of the
audit. Encrypted columns never queried. `JobRunHistoryLogger`'s `teamId` correct at every call site.
Eastern-time scheduling correct. Optional-integration gates and the aggregate-settled rule correct
everywhere. Zoom/Discord/Square idempotency correct (T07 was the exception, and is fixed).

**Deliberate patterns — confirmed intentional, don't "fix" them.** Per-item `SaveChangesAsync` in
scan jobs (12 sites). In-memory `HasEnded` filters (a coarse query bound is already present).
Materialize-before-`OrderBy` (an EF InMemory constraint). `_TestModeBanner`'s uncached read. The
per-item email send window — there is no outbox, by design.

## ⚠️ Treat these findings as leads, not facts

Working through them on 2026-08-10 turned up **five that were wrong**:

| Finding | Reality |
|---|---|
| T36: `CanCreateTeam` is "test-only; pages gate via `[Authorize(Roles)]`" | **It gates a POST handler** (`Teams.cshtml.cs`). Deleting it would have removed an authorization check. |
| T36: `Vec.MatchCode` has "zero production reads" | `VecDefaultsSeeder` and `KnownVecs` depend on it as of 2026-08-10. |
| T36: `FormatSentUtc`, `LatestDueSlotUtc` unused | Both have production callers. |
| T32: money "formatted 18 ways in 3 spellings" | All 14 sites already use one spelling; the inconsistency was fixed in the interim. |
| T15: "Square webhook endpoint unaffected" | A fallback policy **does** reach minimal-API endpoints. Without an explicit exemption every Square delivery would have been refused, invisibly. |

T36 also missed `_Layout.cshtml.css`, an orphaned scoped-CSS companion that fails the build the
moment the view it belongs to is deleted.

None of this makes the audit worthless — it found real problems, and the P0/P1 tier was all genuine.
But **re-verify each claim against current `main` before acting on it**, particularly anything that
proposes deleting code. The file is nine months of assumptions frozen at one commit.
