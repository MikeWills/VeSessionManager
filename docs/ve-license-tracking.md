# VE license tracking (issue #107)

The other half of the license work. [`docs/renewal-monitor.md`](renewal-monitor.md) covers the
hand-curated watch list; this covers the **VE roster's own** licenses, which is a different question
with a different answer.

> Built alongside issue #142 — see [`docs/ve-management.md`](ve-management.md) for the person model
> this depends on, and for why the FRN backfill below matters beyond licenses.

## The question this exists to answer

The Renewal Monitor asks *"is this person's license lapsing?"* about people someone deliberately
added. This asks *"can this person legally serve at Saturday's session?"* about a roster that
populates itself from ExamTools.

**The valuable part is session-relative, not calendar-relative.** A license that is perfectly current
today and expired on the date you have someone booked is the thing that ruins a session, and it is
precisely what the Renewal Monitor structurally cannot say — it has no concept of a session.

**It only became answerable when #142 landed alongside it.** A VE needs a current license of General
or higher **and** accreditation with that session's VEC. #107 brought the license half from ULS;
#142's `VeVecAccreditation` brought the other. Either issue alone could only ever answer half the
question, which is why they shipped together.

## Where the data lives

Columns on `VolunteerExaminer` (`LicenseLastCheckedUtc`, `LicenseNotFoundAtFcc`, `LicenseStatus`,
`OperatorClass`, `LicenseGrantDateUtc`, `LicenseExpiresUtc`, `LicenseCancellationDateUtc`), added in
#142's own migration rather than a second pass over a table that was already being rewritten.

The alternative — auto-creating `WatchedLicense` rows per VE and joining — was rejected: the Renewal
Monitor's whole premise is that a human curated each entry, and filling it with thirty VEs nobody
added would break that. What genuinely had to be shared is the *rules*, not the storage.

## The shared rules

`ILicenseSnapshot` is implemented by both `WatchedLicense` and `VolunteerExaminer`, and
`DeriveSnapshotStatus` holds the 90-day renewal window, the two-year grace period and the "valid
through the expiry date" arithmetic in one place. Two copies of those thresholds would have drifted
the first time either was tuned.

`WatchedLicense` maps onto the interface with **explicit implementations rather than renamed
columns**: its names came first and appear throughout the Renewal Monitor, its service and a shipped
migration, so renaming would have cost a migration for no behavioural gain.

`DeriveStatus` (the Renewal Monitor's full rules) deliberately stays its own method rather than
delegating. The renewal request-through-issuance checks sit *between* cancellation and the date
tests, so delegating would require the shared rule to understand a lifecycle that means nothing for
a VE.

### `NoCallSign` — issue #107's open question 3

A VE can legitimately have no call sign: ExamTools' roster may not report one, or may report the
literal `<UNKNOWN>`. That is a third answer, distinct from "not checked yet" and from "not found at
FCC", and it is checked **first** — before "have we looked?", because `NotYetChecked` implies the
sweep is merely behind and will get to it.

It keys off `CallSign.IsUsable`, the helper written for the placeholder bug in #142 (see
[`docs/historical-import.md`](historical-import.md)), so the two features agree on what a call sign
is. Unreachable for a `WatchedLicense`, whose call sign is required — harmless, and cheaper than a
second enum.

## The refresh

`VolunteerExaminerLicenseWatchService`, run by `LicenseWatchJob` inside its existing **anchored 06:00
ET slot**. Same nightly FCC data through the same mirror as the watch list, so a second schedule
would be two names for one cadence.

**It writes its own `JobRunHistory` row.** The slot guard keys on a successful `"LicenseWatch"` run,
so folding the two together would mean one failing sweep suppresses the other for the rest of the
day — and the ops dashboard could no longer say which half broke.

**Deliberately not part of `VolunteerExaminerSyncService`.** That service reconciles roster
membership from ExamTools and carries a hard-won bound on which sessions it touches (see
[`docs/historical-import.md`](historical-import.md)); bolting FCC lookups onto it would entangle two
unrelated cadences and risk undoing that bound. Roster membership and license state are separate
concerns that happen to share an entity.

Two filters bound the work:

- **Active membership only.** Someone retired from every team they served is never going to be
  assigned to a session, so their license is nobody's question — and without this the sweep grows
  forever as teams turn over.
- **Usable call signs only.** `<UNKNOWN>` would otherwise burn a lookup every run and come back
  not-found each time, which reads as a real FCC answer rather than "there is nothing to ask about".
  Those rows are *counted as skipped* rather than silently dropped, so a roster full of them shows up
  on the dashboard.

A failed lookup deliberately does not stamp `LicenseLastCheckedUtc`, so the row stays stale and the
next run retries — the same idiom every scan-based job here uses.

### The unplanned payoff: FRN backfill

ExamTools' VE roster never reports an FRN, and FRN is the only identifier that survives a vanity call
sign change. This sweep looks up by call sign and gets the FRN in the response, so **it is what makes
#142's identity model robust rather than call-sign-dependent**. A changed call sign is followed and
the previous one written to `VeCallSignHistory`, which is what stops a stale roster minting a second
person for the same human.

That connection was not in either issue's plan; it only became visible once both were in one branch.

## Where it surfaces

| Screen | What it shows | Who sees it |
|---|---|---|
| VE Directory | Status chip, expiry, day count inside the 90-day window | TeamAdmin / SystemAdmin |
| VE detail | Class, expiry, FRN, last refreshed | TeamAdmin / SystemAdmin |
| **Session Detail VE chips** | **Whether they can serve *this* session** | **Every role** |

The last row is the point, and its wider audience is deliberate. Unlike the directory's contact
details — home address and phone, which are *not* public record data — eligibility is derived from
public FCC data plus the team's own roster admin, and the Session Manager running Saturday's session
is exactly who needs to know a VE cannot serve it.

## Three states, and why not two

`VeEligibility` reports **problem**, **unverified**, or **clear**. Collapsing the middle one would let
a VE nobody has checked render as cleared, which is worse than saying nothing.

- *Problem* — expired by the session date, cancelled, not found at FCC, or below the General minimum.
- *Unverified* — no usable call sign, never swept, no expiry recorded, or no accreditation on file
  for this session's VEC. Accreditation is presence-only (simplified 2026-08-09): a missing one is
  never a *problem*, because the app cannot tell "not accredited" from "nobody recorded it", and
  treating the second as the first would refuse people over missing data entry.
- *Clear* — checked, and every test passed.

The two markers differ in **glyph as well as colour**, so they survive a colour-blind reader and a
greyscale screenshot. Bootstrap Icons rather than Unicode symbols, for the reason
[`docs/icons.md`](icons.md) records: a bare symbol renders only if the device happens to have a font
containing it, which is how a marker became a tofu box on an iPhone once already.

**The tooltip always states when the license data was last refreshed and that accreditation is
hand-entered.** The verdict rests on a cached snapshot up to a day old plus data no VEC has verified;
presenting it as a live check is the one way this feature could do harm. It also never says
"cleared" — only "no problem found for this session's date".

## Deliberately not built

- **No emails or alerts.** Screen-only, matching the Renewal Monitor's agreed scope. The natural next
  step, if one is wanted, is the phase 6 session-invitation flow flagging a VE who would be
  ineligible for the session being invited to.
- **No blocking.** Nothing stops a session going ahead with an ineligible VE on the roster. The app
  reports; a human decides — and given half the inputs are hand-entered, an app that refused to
  proceed would be wrong often enough to be ignored.
