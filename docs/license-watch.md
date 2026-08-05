# Licence Watch

A team's watch list of amateur licences: expiration dates, and the renewal lifecycle from
application through issuance. Anyone can be watched — club members, family, someone who never tested
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

### The one unverified assumption

`application_purpose` is matched against `RO` (renewal only) and `RM` (renewal/modification). Those
are FCC's documented purpose codes and the field is documented as present on this endpoint, but **no
record carrying a pending renewal has been observed live** — W1AW, the record the shape was verified
against, had an empty `pendingApplications`.

It is coded defensively: a case-insensitive, trimmed match against a set, so an unexpected spelling
degrades to "not a renewal" rather than throwing or producing a false positive. If renewal detection
ever appears not to fire, this is the first thing to check — one live lookup of a call sign with a
renewal in flight would settle it.

## Status is derived, never stored

`WatchedLicenseStatus` is computed at render time from the cached fields, for the same reason
Session "Completed" is derived: a stored copy would need rewriting every time the clock crossed a
threshold, and would be wrong in between.

Order of the checks is load-bearing:

| Precedence | Status | Why it sits there |
|---|---|---|
| 1 | `NotYetChecked` / `NotFound` | With no ULS data every date test below is meaningless, not merely false |
| 2 | `Cancelled` | A cancelled record **keeps its expiration date** — testing dates first reports a revoked licence as comfortably Active |
| 3 | `RenewalPending` / `Renewed` | Once a renewal is filed, "expires in 12 days" is no longer the actionable fact |
| 4 | `ExpiringSoon` / `ExpiredInGrace` / `ExpiredLapsed` | The date thresholds |

Thresholds, confirmed with the VE team on 2026-08-05:

- **90 days** — FCC's renewal window.
- **2 years** — the grace period during which a licence is still renewable without re-testing, though
  it may not be operated.

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
