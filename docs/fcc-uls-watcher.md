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

**Deliberately out of scope (per the spec's own Open Item):** an existing licensee upgrading class
(e.g. Technician → General) already has an active license before the session, so "FRN appears in the
license file" isn't enough on its own to detect the *new* grant. Needs real sample data (from both
ULS and the ExamTools/HamStudy API) before that logic can be designed — not guessed at here.

## Multi-team note

Candidate matching queries every non-terminal candidate across every `Vec`/session in one pass —
it's inherently team-agnostic, since FCC data has no concept of "which VE team." If this app is
ever extended to serve multiple independent teams (each with their own Discord/Square/Zoom
account), this piece needs no changes to keep sharing one FCC download/scan across all of them;
only the per-team integrations (Zoom/Discord/Square/Email, currently single global
appsettings/user-secrets-bound singletons) would need rework to store credentials per-`Vec`.

## Jobs

- `FccDailyWatcherJob` — 24-hour `PeriodicTimer` (`Jobs:FccDailyWatcherIntervalHours`), calls
  `FccUlsWatcherService.RunDailyAsync` every tick.
- `FccWeeklyCatchupJob` — same `PeriodicTimer` idiom (`Jobs:FccWeeklyCatchupIntervalHours`), but only
  actually invokes `RunWeeklyCatchupAsync` when the current day matches
  `Jobs:FccWeeklyCatchupDayOfWeek` (default `Monday`); every other day's tick is a no-op.
