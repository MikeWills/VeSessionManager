# Renewal Monitor

A team's watch list of amateur licences: expiration dates, and the renewal lifecycle from
application through issuance. Lives under **Applicants** in the nav — a renewal is, technically,
an application, and it keeps every "waiting on FCC" screen together.

> **Two names, deliberately.** The feature is *Renewal Monitor* in the UI; the Core types are
> `WatchedLicense` / `LicenseWatchService` / `LicenseWatchJob`, because that is mechanically what
> they do. The split is kept rather than renamed: the table ships in the `WatchedLicenses`
> migration, and renaming it would cost a migration on a deployed schema for no functional gain. Anyone can be watched — club members, family, someone who never tested
with this team. Rows are **not** tied to a `Candidate` or a `VolunteerExaminer`.

Tracking the VE roster's own licences is a **separate, not-yet-built feature**. It was deliberately
split off rather than folded in here (2026-08-05): a VE watch list wants different questions
answered ("can this person serve on Saturday?"), and `VolunteerExaminer` rows already exist and are
synced from ExamTools, so the two have almost nothing in common beyond the ULS call.

## Scope, as agreed

- **Screen only.** No emails, no digests. The feature reports; a human decides.
- **Team-scoped, every role.** `[Authorize]` with no role list, filtered through
  `SessionAccessScope.ResolveViewableTeamIds`. Everything shown is public FCC record data, so there
  is no reason to gate it the way the VE Roster is gated.
- **Entry by call sign or FRN**, with call sign strongly preferred — it is what a human recognises,
  and it is what the list is keyed on.

## The data source, and what it actually returns

Same endpoint the candidate watcher uses: `GET https://exam.tools/api/uls/lookup2/{frnOrCallsign}`.

Two things were verified live on 2026-08-05 against `W1AW`, and both matter:

1. **`expired_date` is returned.** It was simply never mapped — `UlsLookupResult` had `grant_date`
   and `effective_date` only, because nothing before this needed a term end.
2. **Call-sign lookup works.** The client's parameter is named `frn`, but the endpoint resolves
   either. That is what makes call-sign-first entry possible without a second lookup step.

The response also carries `first_name`/`last_name`/etc. and a full postal address. The name is
mapped; **the address deliberately is not**. Nothing here needs it, and not holding it avoids the
question entirely. Call sign, FRN and licensee name are public FCC record data — the same privacy
class as `Candidate.CallSign`, which the PII purge deliberately keeps.

> **FCC's own API is not an option.** `data.fcc.gov/api/license-view/...` 301s to `www.fcc.gov` and
> returns Akamai 403, the same edge block already documented for `wireless2.fcc.gov`. Confirmed
> 2026-08-05. ExamTools' mirror is the only workable source, which is also the right answer for
> consistency — two sources could disagree about the same licence.

## Renewal detection is a two-step state machine

This is the part that is easy to get wrong, so it is worth stating plainly:

**ULS reports that a renewal application is pending. It never reports that one was issued.**

A renewal leaves the call sign, the operator class and the grant date exactly as they were. The only
thing that moves is the expiration date. So the service:

1. Sees a renewal in `pendingApplications[]` (matched on `application_purpose`), records
   `RenewalPendingSinceUtc` **and** stores the expiration as it stood right then, in
   `ExpiredDateWhenRenewalFiledUtc`.
2. On a later poll, declares the renewal issued only once the current expiration is actually **past
   that stored anchor**.

The anchor is what makes step 2 an assertion rather than a guess. Without it the only available test
is "is the expiry in the future?", which is true for a licence renewed years early and would report
a renewal that never happened.

**Issuance is detected from the expiry advancing, not from having seen the application first.**
That distinction was originally missing, and it broke the very first real renewal (2026-08-06).

The state machine required a prior "pending" sighting before it would recognise a grant. A licence
renewed *between two polls* therefore arrived with its new expiry already in place, was recorded as
newly **pending**, and anchored against that already-updated value — which it could then never beat.
The row sat on "Renewal pending" until FCC dropped the application, then fell through to plain
Active, never once reporting the renewal it had just watched land. It reached the right end state by
the wrong route, misreporting the whole way.

So the first question asked on every refresh is now simply: **did the expiration date move forward
since the last look?** If so, that is a renewal, whatever the application list says and whether or
not this app ever saw it pending. The anchor test below is kept as a second line of defence, for an
advance spread across a poll that returned no expiry at all.

Three details in `ApplyRenewalState` that each exist for a reason:

- **Confirmation is tested before the still-pending branch.** FCC does not necessarily drop the
  application the instant the new term is granted; the two can overlap. Checking "did the expiry
  advance?" first means an overlap reports Renewed instead of sticking on Renewal pending until FCC
  tidies up. Pinned by `RenewalIssued_EvenWhileTheApplicationIsStillListedAsPending`.
- **`RenewalPendingSinceUtc` is never refreshed while the application stays pending.** It records
  when *we* first saw it; letting it creep forward on every poll would make "pending since" always
  today and hide the wait entirely.
- **An application that vanishes without the expiry moving** is treated as abandoned (dismissed,
  withdrawn, re-filed), and the row returns to reporting its real expiry rather than a stale
  "pending".

### The overlap outlives the poll that confirmed the renewal (fixed 2026-08-07)

The bullet above about FCC leaving a granted application in `pendingApplications` was right about the
overlap and wrong about its duration. It handled the overlap *within* the poll that spotted the
grant — but the application is still listed on the **next** day's poll too, and by then the expiry
has stopped moving, so the "no advance, and there's a renewal pending" path read it as a brand-new
request.

Observed on KA0MVW, one day after the fix above: Aug 6 correctly reported **Renewed**, Aug 7 showed
**Renewal pending / "Filed, seen Aug 7"** against an expiry of 2036. And it was wedged there
permanently — the anchor it recorded was the already-renewed expiry, a value nothing could ever beat,
so the row could only escape when FCC eventually dropped the application. A licence walking backwards
from issued to pending is exactly the thing the state machine exists to prevent.

Two guards, deliberately at different layers:

- **`LicenseWatchService.IsAlreadyIssued`** filters an already-granted application out of
  `pendingApplications` before any branch looks at it, so no path can mistake a receipt for a
  request. Matched on the ULS file number first — which is why `RenewalFileNumber` is now **kept**
  through a confirmation rather than cleared with the other renewal fields. The fallback, for a
  response that omits the number, is the receipt date: FCC cannot have received a genuinely new
  renewal before it issued the last one, and real ones are ten years apart. A row already wedged by
  the bug stands itself down on the next run, and that stand-down is deliberately **not** counted as
  an abandonment.
- **`DeriveStatus` puts `Renewed` above `RenewalPending`.** A renewal confirmed within the last month
  cannot have been followed by a real new one, so whatever the stored fields say, the screen never
  walks an issued licence back to pending. `RenewalMonitor`'s renewal column keys off the derived
  status for the same reason — the chip saying Renewed while the column says "Filed" was half the
  confusion.

Pinned by `ApplicationStillListedAfterIssuance_DoesNotReArmPending`,
`RowAlreadyWedgedByALingeringApplication_StandsDownWithoutCountingAsAbandoned`,
`RenewalFiledLongAfterAPreviousOne_IsStillDetected` (the filtering must not deafen the row to the next
real renewal) and `RecentlyIssuedRenewal_OutranksAPendingFlag`.

### The assumption that was wrong (resolved 2026-08-06)

`application_purpose` was originally matched against FCC's two-letter codes, `RO` and `RM`. That was
documented at the time as **the one unverified thing in the feature**, because no record carrying a
pending renewal had ever been observed — W1AW, the record the shape was verified against, had an
empty `pendingApplications`.

It was wrong. A real renewal, observed live on 2026-08-06:

```jsonc
"pendingApplications": [{
  "application_purpose": "Renewal/Modification",   // NOT "RM"
  "uls_filenumber": "0012140898",
  "receipt_date": "2026-08-04T08:00:00.000Z"
}]
```

**ExamTools returns FCC's human-readable description, not the raw code.** So `IsRenewal` was always
false and the entire request-through-issuance lifecycle never fired. The failure was quiet in the
worst way: the licence still picked up its new expiration date on the next refresh, so the row simply
slid from "Expiring soon" to "Active" with a 2036 date, never once reporting a renewal.

Matching now accepts **either form** — the codes, in case another endpoint or a future shape change
returns them, and any description containing "renewal". A substring test is the right shape because
FCC combines purposes ("Renewal/Modification"), so an exact list would have to enumerate every
combination and would break on the next one.

> The lesson is not "check assumptions" in the abstract. It is that this one was **knowable** — it
> needed one lookup of a call sign with a renewal in flight, which nobody had. When a feature rests
> on a value that has never been seen, the honest options are to find a real sample or to make the
> match tolerant enough that being wrong degrades rather than disables.

## Refresh cadence follows FCC's clock, not the licence's

`RefreshInterval` was originally 20 hours, justified as "a licence term is ten years and a renewal
takes days to weeks, so nothing changes hour to hour". True of the licence, wrong about the feed.

**FCC posts its daily changes at 02:00 ET.** The useful question is not how fast a licence changes,
but how long after that nightly run this app notices. The job originally ticked every four hours
**from Worker start**, so its check times drifted with every restart — boot at 21:27 and it checks at
21:27/01:27/05:27; restart at 06:10 and it becomes 06:10/10:10/14:10. Nobody could say when the next
check was without knowing when the service last came up, and a renewal granted at 02:00 ET on
2026-08-06 was still invisible that morning as a direct result.

**It is now anchored to 06:00 ET, once a day** — after FCC's run, before anyone opens the page.

Anchoring is not "fire a timer at 06:00 and hope the Worker is up". The job still ticks hourly, and
each tick asks whether the most recent due slot has already run by looking at `JobRunHistory`. A
Worker that boots at 08:47 finds today's 06:00 slot missed and runs it immediately; later ticks that
day find it done and skip. Restarts and outages self-heal, and the schedule never drifts.

One anchored run is also **fewer** calls than the four-a-day it replaced — the data only changes once
a night, so polling more often bought nothing.

Two consequences worth knowing:

- `RefreshInterval` (6h) no longer decides cadence. Its only remaining job is to stop a second run on
  the same day — a restart, a manual trigger — redoing every lookup.
- `MaxLookupsPerRun` rose from 100 to 250, because "the remainder is picked up next run" used to mean
  four hours and now means tomorrow.

The hour is a constant rather than a `SystemSettings` row, unlike `UlsWatcherJob`'s. That job is tuned
per deployment because it drives candidate grant detection during live sessions; this one has a
single job to do and no reason to differ between environments.

### The slot arithmetic lives in Core, and now has tests

`DailySlotSchedule` is shared by both watchers. It was previously an internal helper inside
`UlsWatcherJob` with **no tests at all** — not through neglect, but because the test project
references Core and not the Worker, so it was simply unreachable. Moving it is what made it testable.

That matters because it crosses DST twice a year: 06:00 ET is 10:00 UTC in summer and 11:00 UTC in
winter. Computing the slot in UTC, or assuming a fixed offset, shifts every run by an hour twice a
year — and the failure is invisible, because the job still runs, just not when anyone thinks.

## Refresh job

`LicenseWatchJob` ticks every 4 hours; `LicenseWatchService` decides what is actually due
(`RefreshInterval`, 20 hours) and caps each run at `MaxLookupsPerRun` (100), least-recently-checked
first so nothing starves.

Deliberately the plain "tick every N hours from Worker start" idiom, **not** `UlsWatcherJob`'s
wall-clock-ET slot machinery. That job anchors to 08:00/20:00 ET because FCC issues at 02:00 ET and a
morning poll wants that day's grants to exist. Nothing here is time-of-day sensitive: a term is ten
years and a renewal takes days to weeks. CLAUDE.md's precondition for reusing this idiom holds — the
endpoint returns current state on every call rather than a one-shot window, so a missed tick
self-heals.

Standard scan-based idempotent shape otherwise: `LastCheckedUtc` is both the staleness filter and the
progress marker, rows save individually, and the tick body is wrapped in `JobTick.GuardedAsync`
(without which a transient "database is locked" from the shared SQLite file stops the whole Worker).

**A failed lookup deliberately does not stamp `LastCheckedUtc`.** Leaving the row stale is what makes
the next run retry it — stamping it would park the row for a full refresh interval on the strength of
an error. `FailedLookup_LeavesTheRowStaleSoItRetries` pins this.

## Adding a licence resolves it immediately

The add flow performs the ULS lookup **synchronously, in the web request**, and refuses to save if
the call sign does not resolve. A mistyped entry that was merely stored would sit in the list forever
showing "not checked yet", long after the person who typed it had gone. It also means a row entered
as an FRN is stored under its call sign, and that a new row renders complete rather than blank until
the Worker's next tick.

The Web project therefore registers `IUlsLookupClient` and `LicenseWatchService` too, and the handler
reuses `LicenseWatchService.Apply` rather than mapping the response itself — otherwise the two paths
would drift.

Three failure cases are reported differently on purpose: endpoint unreachable ("try again shortly"),
no such record ("check the call sign"), and already on the list. Telling someone their perfectly good
call sign does not exist, because ExamTools happened to be down, would be worse than useless.

## Follow-ups

- **VE licence tracking** — the other half of the original request, deliberately deferred.
- Confirm the renewal purpose codes against a live pending renewal.
- Nothing notifies. If a digest is ever wanted, the existing `PaymentExpirationNotice` pattern (which
  mails the Session Manager rather than the subject) is the precedent to copy — and note that a
  watched licence has **no contact details at all**, by design.
