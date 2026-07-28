# FCC ULS Application/License Watcher (Phase 5)

What `FccUlsClient`/`FccUlsRecordParser`/`FccUlsWatcherService`
(`VeSessionManager.Core/FccUls/`) rely on. No account setup, no credentials — this is a public FCC
dataset — but the pipe-delimited field layout needed real downloaded data to pin down accurately,
which this doc records so nobody has to redo that verification.

## Hosts and file names

- Download host: `https://data.fcc.gov/download/pub/uls/` — confirmed working with a plain
  `HttpClient`, no special headers needed.
- **`www.fcc.gov` (the documentation/PDF pages) is a different host and behaves differently** —
  plain HTTP requests there (via `curl` and via `WebFetch`) reliably hung or reset mid-download,
  seemingly bot/automation detection unrelated to the actual data API. `data.fcc.gov` (the host
  this app actually calls) had no such issue in direct testing. If a future troubleshooting session
  sees FCC connectivity failures, check which host the request is actually going to before
  assuming the API itself is down.
- Daily files: `daily/a_am_<day>.zip` (applications), `daily/l_am_<day>.zip` (licenses), where
  `<day>` is a lowercase 3-letter day abbreviation (`sun`/`mon`/`tue`/`wed`/`thu`/`fri`/`sat`) —
  confirmed all seven exist in the daily directory listing, even though the spec's source material
  says files are only generated Tue–Sat (weekend files may just be stale/empty rather than absent).
  `FccUlsClient` requests whatever day `TimeProvider` says it currently is; a 404 is treated as "not
  published yet," not an error.
- Weekly/complete files: `complete/a_amat.zip`, `complete/l_amat.zip` — same internal structure,
  just a full nationwide snapshot instead of one day's transactions. Used by `FccWeeklyCatchupJob`
  as a catch-up pass, per the spec.
- Each zip contains multiple `.dat` files, one per ULS record type (`HD.dat`, `EN.dat`, `AM.dat`,
  plus several irrelevant ones — `AD`, `AT`, `HS`, `SC`, `VC`, `CO`, `LA`). This app only reads
  `HD.dat` and `EN.dat`; `AM.dat` (and the rest) are never opened, since everything needed —
  including Call Sign — is already present on `HD`.

## Field layout — verified against real data, not just the FCC's own PDF

The FCC's field-layout PDF
([fcc.gov/file/13762/download](https://www.fcc.gov/file/13762/download)) lists a "Position" number
for every field per record type. `FccUlsRecordParser` was written against that document, but its
positions were cross-checked by downloading a real `l_am_<day>.zip`/`a_am_<day>.zip` and indexing
actual pipe-delimited rows — **the two didn't fully agree.**

- **HD matched the PDF exactly.** Real rows confirmed position 2 = Unique System Identifier,
  5 = Call Sign, 6 = License Status, 7 = Radio Service Code (always `HA` in these files), 8 = Grant
  Date, 9 = Expired Date, 10 = Cancellation Date, 43 = Effective Date, 44 = Last Action Date — every
  one landed exactly where the PDF said.
- **EN did not.** The PDF lists FRN at position 24. A real EN row's FRN-shaped value (a 10-digit
  number) was actually at **position 23** — one earlier than documented. The PDF's own "Data
  Element" column had an extra, seemingly-blank phantom row inserted between Zip Code (19) and PO
  Box, most likely a PDF-to-text extraction artifact rather than a real field. `FccUlsRecordParser`
  uses the *verified* position (23), not the document's stated one.

`FccUlsRecordParser` only reads what this phase needs:

| Record | Position | Field | Used for |
|---|---|---|---|
| HD | 2 | Unique System Identifier | join key between HD and EN within one file |
| HD | 5 | Call Sign | `Candidate.CallSign` on grant |
| HD | 6 | License Status | `A` = Active (see filtering below); blank on pending applications |
| HD | 8 | Grant Date (`MM/dd/yyyy`) | `Candidate.LicenseGrantDateUtc` |
| HD | 44 | Last Action Date (`MM/dd/yyyy`) | `Candidate.ApplicationDateEnteredUtc` — see below |
| EN | 2 | Unique System Identifier | join key |
| EN | 23 | FCC Registration Number (FRN) | match key against `Candidate.Frn` |

**Why Last Action Date, not a field literally named "status date":** real downloaded
application-file rows (`a_am_<day>.zip`) always have a blank License Status (not yet decided) and a
populated Last Action Date equal to the day the file itself represents — i.e. the date ULS recorded
this application's entry. That's the closest real equivalent to what the spec called "the HD status
date," so that's what `ApplicationDateEnteredUtc` is set from.

**Why License Status must be `A` to count as a grant:** a real license-file row can have Status `C`
(Canceled) while still showing a *recent* Last Action Date — observed directly in downloaded data:
an old license, canceled years ago, still appeared in a same-day transaction file because of some
unrelated administrative touch. Treating *any* FRN appearance in the license file as "granted" would
misfire on that case, so `FccUlsWatcherService` only accepts `LicenseStatus == "A"`. A brand-new
candidate has no prior license at all, so this filter never affects the common case — it only
guards the edge case.

## State machine (implemented in `FccUlsWatcherService`)

Matches the spec's Phase 5 state machine exactly:

- `Unmatched` + FRN found in the application file → `Received`, `ApplicationDateEnteredUtc` set.
- `Unmatched` or `Received` + FRN found in the license file with Status `A` → `Granted`,
  `CallSign`/`LicenseGrantDateUtc` set. License match always wins and short-circuits application
  status — a straight `Unmatched → Granted` jump (no `Received` in between) is expected, not a bug,
  when a daily application file was missed but the license file wasn't.
- Terminal statuses (`Granted`/`Failed`/`NotTested`) and candidates with a null `Frn` are excluded
  by the service's queries, not by an explicit branch — they're simply never selected.
- Application processing runs before license processing and commits (`SaveChangesAsync`) in
  between, so a candidate matched to `Received` earlier in the *same* run is already persisted and
  eligible for the `Granted` check that follows in that same run.

## Upgrade exam (existing licensee) handling — resolved 2026-07-28 with real data

The spec's own Open Item flagged this as deliberately out of scope until real sample data existed:
an existing licensee upgrading class (e.g. Technician → General) already has an active license
before the session, so "FRN appears in the license file" isn't enough on its own to tell a real new
grant apart from a pre-existing one. Real data finally surfaced this live, running the FCC daily
watcher against real HRCC candidates:

- **William Denney** registered for two separate sessions (2026-06-30, 2026-07-11), but both were
  matched against the same license record — `GrantDateUtc = 2026-06-23`, predating *both*
  registrations. He was already licensed before either session.
- **Jason Pelowitz**: his first session's match (grant date 2026-07-14, two days after his
  2026-07-12 test) looks like a genuine new grant. His second session (2026-07-25) matched the
  *same* 2026-07-14 grant date — again, already licensed before that session even started.

This also confirmed empirically (not guessed) that **FCC's Grant Date does not change when an
existing licensee upgrades their class** — Denney's stayed `2026-06-23` across two later sessions.
`ProcessLicensesAsync` still can't avoid re-detecting a pre-existing license this way (and doesn't
try to — the AM.dat record type, which carries operator class, still isn't fetched), but that
Grant Date is still accurate historical fact worth storing as-is.

What actually needed fixing was downstream: `PiiPurgeService.PurgeGrantedCandidatesAsync`'s
retention Trigger A anchored purely on `LicenseGrantDateUtc` — for an upgrade/repeat candidate, that
historical date is already old the moment ingestion runs, so their PII would purge almost
immediately after a real, current session. Fixed by anchoring Trigger A on the *later* of
`LicenseGrantDateUtc`/`Session.ScheduledStartUtc` instead (see `Candidate.LicenseGrantPredatesSession()`
and `docs/pii-purge.md`) — zero behavior change for a genuine new grant (always after the session),
and no more premature purge for the upgrade case. The distinction is also now surfaced on the
applicant detail page (`docs/applicant-detail.md`) so a VE can see it at a glance.

## Stale/dismissed application gotcha (found 2026-07-22, fixed same day)

Found via a live lookup of a real FRN: the application file's HD row has **no field that
distinguishes "genuinely still pending" from "dismissed/withdrawn/returned months ago"** — both
look identical (blank HD License Status, same as the "not yet decided" case documented above). The
weekly-complete `a_amat.zip` snapshot in particular appears to retain old, already-resolved
application entries indefinitely rather than dropping them once resolved — a dismissed application
from five months prior was still present, at its original (stale) Last Action Date, in a
current-day download.

This matters because a candidate's FRN is a permanent personal identifier — if they (or someone
sharing that identity record) has any *prior*, unrelated application on file that was never
granted, its HD row can sit in the application file indefinitely with the same "pending" signature
a real new post-session application would have. Unguarded, `FccUlsWatcherService` would match on
whichever row happens to be there, mark the candidate `Received` with a stale
`ApplicationDateEnteredUtc` from the old application, and then — because the application-matching
query only looks at `Unmatched` candidates — never revisit it once the real new application
actually appears in a later file.

Fixed in `FccUlsWatcherService.ProcessApplicationsAsync` two ways:

1. An application match only counts if its Last Action Date is **on or after the candidate's own
   `Session.ScheduledStartUtc`** (`.Date` comparison, same "compare dates not full DateTimes" idiom
   used elsewhere in this app) — a real new-license application can't have a Last Action Date
   before the exam that produced it.
2. When a file contains more than one row for the same FRN, the **most recent** Last Action Date is
   picked (`OrderByDescending(r => r.LastActionDateUtc).First()`), not an arbitrary first-in-file
   pick — so a stale row and a genuine new row for the same FRN in the same file resolve correctly.

The license-matching path (`ProcessLicensesAsync`) doesn't need the same fix — it already only
counts `LicenseStatus == "A"` rows, and a genuinely stale/dismissed application never reaches Active
license status, so there's nothing stale for it to accidentally match on.

## Multi-team note

Candidate matching queries every non-terminal candidate across every `Vec`/session in one pass —
it's inherently team-agnostic, since FCC data has no concept of "which VE team." If this app is
ever extended to serve multiple independent teams (each with their own Discord/Square/Zoom
account), this piece needs no changes to keep sharing one FCC download/scan across all of them;
only the per-team integrations (Zoom/Discord/Square/Email, currently single global
appsettings/user-secrets-bound singletons) would need rework to store credentials per-`Vec`.

## Jobs

- `FccDailyWatcherJob` — ticks hourly and calls `FccUlsWatcherService.RunDailyAsync` only when the
  current US Eastern hour matches `SystemSettings.FccDailyWatcherStartHourEt` (default 8) plus every
  `FccDailyWatcherIntervalHours` (default 12) after that — 8am and 8pm ET by default. See "Same-day
  retry" below for why this isn't the Worker-start-relative 24h `PeriodicTimer` every other job uses.
- `FccWeeklyCatchupJob` — same Worker-start-relative 24-hour `PeriodicTimer` idiom as every other job
  (`Jobs:FccWeeklyCatchupIntervalHours`), but only actually invokes `RunWeeklyCatchupAsync` when the
  current day matches `Jobs:FccWeeklyCatchupDayOfWeek` (default `Monday`); every other day's tick is
  a no-op. See "Weekly complete snapshot lags real filings" below — this job's own name overstates
  how current its data actually is.

## Same-day retry (found 2026-07-23, fixed same day)

Live-verified via a real FRN: every other job in this app uses a 24-hour `PeriodicTimer` starting
from whenever the Worker process happens to start, on the reasoning that an extra tick is free
(idempotent) so wall-clock precision doesn't matter. That reasoning breaks down for
`FccDailyWatcherJob` specifically, because unlike those other jobs, **a missed tick here isn't just
wasted — it can go unrecovered for a full week**: each day-name file (`a_am_wed.zip`, etc.) is a
fixed URL that only ever holds that one calendar day's transactions, and isn't revisited by this job
until the same day-of-week comes back around 7 days later. If the Worker-start-relative tick for a
given day happened to land before FCC's ~5am ET publish window (or during a maintenance-window gap),
that day's real filings were invisible to the daily job until the following week.

Fixed by pinning this one job to wall-clock time in US Eastern (`FccUlsSchedule.EasternTimeZone`,
resolved via the cross-platform IANA id `"America/New_York"`) instead of Worker-start-relative
ticking: it now ticks hourly and only actually runs at `FccDailyWatcherStartHourEt` (default 8am ET)
and every `FccDailyWatcherIntervalHours` after that (default 12h → also 8pm ET), giving a same-day
retry instead of a week's wait if the morning check is too early for that day's publish. Both values
are admin-editable on `/Admin/SystemSettings`, same as this job's other settings.

This same fix required changing `FccUlsWatcherService.RunDailyAsync`'s day-of-week computation from
raw UTC to Eastern time too: the new 8pm ET retry lands at/after UTC midnight for most of the year
(EDT is UTC-4, EST is UTC-5), so a UTC-based `DayOfWeek` would silently compute *tomorrow's* day name
right when the retry was supposed to be checking *today's* file. Covered by
`RunDailyAsync_NearUtcMidnight_UsesEasternDayOfWeek_NotUtcDayOfWeek` in
`FccUlsWatcherServiceTests.cs`.

## Weekly complete snapshot lags real filings (found 2026-07-23)

`FccWeeklyCatchupJob` exists to cover any day the daily job missed, by re-scanning FCC's full
`complete/a_amat.zip`/`complete/l_amat.zip` snapshot. Live-verified this snapshot is **not** a fast
backstop: a real application filed and confirmed present in that day's daily file
(`a_am_wed.zip`) was **still absent from `complete/a_amat.zip` a full day later**, re-downloaded
fresh (not cached) to confirm. How long the complete snapshot actually takes to absorb a new filing
is unknown — at least 24+ hours, possibly longer — so a daily-job miss isn't reliably caught by the
following Monday's weekly catch-up either; the same-day retry above is the real fix for that,
not this job. Don't treat `FccWeeklyCatchupJob`'s existence as proof missed days are always
eventually recovered on a predictable schedule.
