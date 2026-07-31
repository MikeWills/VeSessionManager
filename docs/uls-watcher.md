# ULS Watcher — licence grant tracking via ExamTools' ULS API

Replaced the FCC bulk-file watcher on 2026-07-31. For the removed subsystem — and, more importantly,
the incidents that produced the matching rules carried over here — see
[`fcc-uls-watcher.md`](fcc-uls-watcher.md), retained as history.

## Why this replaced the FCC file parsing

The old watcher downloaded FCC's daily/weekly ULS transaction archives and parsed four `.dat` record
types (`HD`, `EN`, `AM`, `HS`) by field position. It worked, but:

- **It was structurally ~26-30h behind.** FCC issues licences at 02:00 ET; the day's file publishes
  the *following* morning. A candidate granted today could not appear until tomorrow. ExamTools —
  which a Session Manager has open on the next screen — showed the call sign immediately, so the app
  routinely disagreed with the reference the user actually trusts.
- **The complexity was substantial and fragile**: day-name file scheduling, a weekly catch-up job
  that existed only because a missed day-file was unrecoverable, field positions that disagreed with
  FCC's own published layout, ~199 MB downloads, and a whole publication-timing model to reason about.

`GET https://exam.tools/api/uls/lookup2/{frnOrCallsign}` returns current ULS state for one FRN,
unauthenticated, in one call — including the two fields that made upgrade detection hard.

**The accuracy trade-off was accepted deliberately** (Mike, 2026-07-31): this tracking is
informational, not operational. *"We aren't out here tracking the FCC and putting in tickets if there
are issues. That's the VECs job."* The source of truth remains FCC's own Application Search and ULS,
consulted manually — which is why Applicant Status links out to them.

The failure-domain objection (adding a dependency on ExamTools) was raised and correctly dismissed:
ExamTools is already the hard dependency for ingestion, and **if ExamTools is down there is no
testing happening**, so there are no grants to miss.

## The endpoint

```
GET https://exam.tools/api/uls/lookup2/{frnOrCallsign}     # unauthenticated
```

```jsonc
{
  "type": "existing",              // or "notfound" — a clean sentinel, not an error
  "u_id": 5339614,                 // ULS Unique System Identifier -> Candidate.FccUlsLicenseKey
  "callsign": "KC1ZYU",
  "license_status": "Active",      // only "Active" counts as a grant
  "license_class": "Technician",   // CURRENT class — advances on an upgrade
  "prev_license_class": "General", // absent on some records, "" on others — treat identically
  "grant_date": "...",             // original issuance; does NOT advance on upgrade
  "effective_date": "...",         // DOES advance on upgrade (= HD Last Action Date)
  "pendingApplications": [{ "uls_filenumber", "receipt_date", "history": [{ "log_date", "code" }] }]
}
```

**Use `lookup2`, never `lookup`.** Both exist. `/lookup/` resolves an FRN against a staler index — on
2026-07-31 it reported a candidate `license_status: "Pending"` with no call sign at all, while
`/lookup2/` returned that same FRN's grant issued the same morning. This app only ever holds FRNs, so
`/lookup/` is unusable here.

## Matching rules — carried over unchanged

Only the data source moved. Each rule below was bought with a real incident; none may be relaxed.
`UlsWatcherServiceTests` pins all of them.

| Rule | Why |
|---|---|
| Only `license_status: "Active"` counts | An FRN can carry a Canceled/Expired record touched by unrelated admin activity |
| **New licence**: `grant_date` on/after the session | Without it, an upgrade candidate's *pre-existing* licence marks them Granted instantly — this wrongly granted three real candidates on 2026-07-30 |
| **Upgrade**: `license_class` == `NewLicenseClass` **and** `effective_date` on/after the session | Both halves load-bearing: class alone re-confirms someone who already held it walking in; date alone matches any unrelated action |
| Pending application only counts if `receipt_date` is on/after the session | A dismissed old application can share an FRN; a real post-exam application cannot predate the exam |

**`grant_date` is useless for upgrades** — FCC pins it to the original issuance. A real 2026 upgrade
still reported `grant_date: 2024-08-21` alongside `effective_date: 2026-07-30`. That is why
`LicenseGrantDateUtc` stores `effective_date` for a confirmed upgrade, not `grant_date`.

Legacy classes (Novice, Advanced) map to `LicenseClass.None`, which conservatively means "never
confirms an upgrade" — correct, since `NewLicenseClass` can only be Technician/General/Extra.

### Verified live, in both directions, on the day it shipped

Two candidates tested for General on 2026-07-31:

| | 10:00 ET | 11:30 ET |
|---|---|---|
| `license_class` | Technician | **General** (`prev`: Technician) |
| `effective_date` | 2026-05-22 / 2025-09-30 | **2026-07-31** |
| Watcher verdict | correctly **not** granted | correctly **granted** |

The upgrades landed between the two runs. The rule withheld the grant while only the date could have
matched, and confirmed it once the class actually moved.

## Job

`UlsWatcherJob` ticks hourly and runs at the ULS slots — **08:00 and 20:00 ET by default**
(`SystemSettings.UlsWatcherIntervalHours` / `UlsWatcherStartHourEt`). Wall-clock ET is kept because
FCC issues at 02:00 ET, so a morning slot lands after that day's grants exist. The
"has this slot already run?" catch-up check via `JobRunHistory` is carried over from
`FccDailyWatcherJob`, so a Worker starting at 08:47 still catches the 08:00 slot.

**There is no weekly catch-up job any more.** It existed solely because an FCC day-name file was a
one-shot window that could be missed permanently. A lookup returns current state on every call, so a
missed tick costs one slot's latency and self-heals. For the same reason the three
`--run-fcc-daily`/`--run-fcc-weekly`/`--run-fcc-all-dailies` switches collapse into one: **`--run-uls`**.

Cost is one HTTPS request per *non-terminal* candidate per run — 7 on the day it shipped. Terminal
candidates are excluded by the query, so this does not grow with history.

## Failure handling

`LookupByFrnAsync` returns **`null`** when the lookup could not be performed (network/HTTP) and
**`UlsLookupResult.NotFound`** when the endpoint answered `type: "notfound"`. These must stay
distinct: not-found is a legitimate "no change" (FCC has no record yet); null means "learned
nothing", and the candidate is left untouched so the next run retries. Neither aborts the scan — one
failing FRN must not stop the others. Each candidate is saved individually, so a crash mid-scan never
loses progress.

## Application data is informational only

*"I trust the ET license grants, I don't trust the applications."* Received status, hold reason and
fee status come from `pendingApplications` and drive **display only** — never money or retention
decisions, which key off `ApplicationStatus` and `LicenseGrantDateUtc`.

Worth knowing before treating a blank Fee column as a bug: these signals are genuinely rare. Of 68
real candidates before the switchover, 66 had `FccPaymentStatus = Unknown` and **all 68** had
`FccHoldReason = None`. The `FVP*` codes look like an exception path (`FVPOFF` is literally
"*Offlined* for Payment Verification"), not a per-application record.

## FCC links on Applicant Status

**Licence link only, rendered whenever a licence key exists.** Built by `FccUlsLinks.License` as
`https://wireless2.fcc.gov/UlsApp/UlsSearch/license.jsp?licKey={u_id}`.

Verified end to end 2026-07-31 against a real record: FRN `0038616330` → `lookup2` `u_id: 5339575` →
the FCC URL Mike opened, `…/license.jsp?licKey=5339575` (KD3DPX). So `u_id` *is* `licKey` — the
shape is confirmed, not inferred, and the page resolves.

`UlsWatcherService.ApplyLicenseKey` persists the key **on every run that returns one, not only on
grant**. An upgrade candidate already holds a licence while their upgrade is pending, so the link
works for the whole waiting period. A first-time applicant has no licence until the grant, so the key
stays null and the link simply isn't rendered — which is the whole reason it's conditional.

### No application deep link — closed, not deferred

Investigated to a conclusion and abandoned for three independent reasons, any one sufficient:

1. **The results page is session-scoped.** Searching by FRN lands on
   `results.jsp?applSearchKey=applSearchKey20266311340484` — a server-generated token encoding *the
   search that was just run*, with a timestamp in it. There is no stable URL to construct even with
   unrestricted access.
2. **`ApplicationSearch/*` is blocked for this deployment's operator** — Akamai 403, reproduced from
   multiple VPN exits. A link would land on an error page. (`UlsSearch/*` on the same host is *not*
   blocked, which is why the licence link is fine — Akamai rules differ per path.)
3. **We don't hold the key it would need.** An application has its own USI, distinct from the
   licence's (Anthony Losada: application `16131111`, licence `5339614`). The ULS lookup API does not
   expose it — `pendingApplications[]` carries only `uls_filenumber`, `application_purpose`, `source`,
   `receipt_date`, `history`, `comments`. The deleted FCC `AD.dat`/`EN.dat` parser *did* carry it, and
   resurrecting a file-parsing subsystem for a convenience link would be a bad trade.

`Candidate.UlsApplicationFileNumber` is still captured — it is real data, cheap to store, and usable
by anyone who can reach FCC's search — it is simply not rendered as a link.

**Better direction if application visibility is wanted:** `pendingApplications[].history[]` already
returns human-readable entries (`code_text: "Redlight Review Completed"`) with dates, and the watcher
currently discards everything but the hold flag. Surfacing that timeline inline would give more than
the FCC page would, with no dependency on a site that won't load. Logged in TODO.md.

## Risks worth revisiting

Undocumented and unauthenticated, so it can change shape, add auth, or rate-limit without notice —
and it is ExamTools' mirror rather than FCC direct, so it inherits their refresh behaviour (the
`/lookup/` vs `/lookup2/` disagreement is direct evidence their indexing has moving parts). **Asking
ExamTools whether this endpoint is supported, and at what polling rate, is still an open action** —
see TODO.md. If the answer is "internal, please don't", the fallback is FCC's files, recoverable from
git history.
