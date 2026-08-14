# VE personal data: what is kept, what ages out, and why

Written 2026-08-14, resolving the L-07 half of issue #313. The finding was an **unstated asymmetry**:
the public Privacy page promises candidates a retention window, while a VE's home address, phone and
email were kept forever — including for VEs who retired years ago and for duplicate rows that a merge
had already superseded. Defensible, arguably. Just never decided, and never said.

## The position

**A VE's accreditation trail is kept indefinitely. Their contact details are not.**

| Kept forever | Cleared after the inactivity window |
|---|---|
| Name | Email |
| Call sign, FRN | Phone |
| License class and expiry | Address lines, city, state, postal code |
| VEC accreditations | Discord username |
| Sessions worked | Notes (admin free text) |

The split is not arbitrary. The left column is the record that **this person was qualified to
administer the exams they administered** — a VEC may need it long after someone stops volunteering,
and call sign and FRN are public FCC record data anyway, the same ruling already applied to
candidates (2026-08-03).

The right column is what was **given in confidence**. `VolunteerExaminer`'s own remarks make the
point that matters: the address here is the VE's *home* address, handed to their team privately,
while the address on the public FCC/QRZ record is typically a PO box precisely because they chose not
to publish where they live. Those two are not interchangeable, and only one of them is ours to keep.

Notes is cleared with the rest. It is admin-facing free text that no rule constrains, so it is the
one field that may contain anything at all about a person — which makes keeping it after their
contact details have aged out indefensible.

## "Inactive" is two conditions, and both are load-bearing

A VE is eligible only when **both** hold:

1. They have **no active team membership**, and
2. They have **worked no session** inside the window.

Either alone is wrong, in opposite directions:

- On (2) alone, a current roster member who happened to have a quiet couple of years would lose the
  email address their team invites them with. That is not a purge, it is a bug.
- On (1) alone, a VE freshly added to a roster and not yet activated would be purged before they ever
  worked a session.

A VE who has never worked a session falls back to `CreatedUtc`, so an imported row that went nowhere
still ages out rather than living forever on a technicality — while a just-created one is safe,
because its `CreatedUtc` is today. Both cases have tests, and the mutation that swaps the fallback
for `DateTime.MinValue` fails exactly the second one.

Merged-away duplicates (`MergedIntoVolunteerExaminerId` set) are eligible on the same terms rather
than immediately. The merge target carries the person forward, so the loser row's details are already
redundant — but purging it early would be a second rule to reason about for no practical gain.

## It is off until an admin turns it on

`SystemSettings.VeContactRetentionYears` is null by default and the pass is skipped entirely while it
is. Same explicit-opt-in rule as the candidate window, and a stronger case for it: **nobody expects a
volunteer roster to start forgetting people because a job shipped.** Set it in Admin → System
Settings. Five years is the suggestion — long enough that a lapsed VE who returns still has their
details, short enough that "we hold your home address" stops being true indefinitely for someone who
left a decade ago.

Years, not days, because the two windows answer different questions. A candidate's is tied to an FCC
process that finishes in weeks; a VE's is tied to "have they stopped volunteering", which is only
legible over years.

## Mechanics

- Runs as a second pass inside the existing `PiiPurgeService`, so it inherits the job, the schedule
  and the per-row save that every scan-based job here uses.
- `VolunteerExaminerPiiFields.Clear` is the single definition of "cleared", mirroring
  `CandidatePiiFields` — two definitions drift, and the drift is silent.
- Row selection asks **what contact data is actually present**, not whether `PiiPurgedUtc` is null.
  Filtering on the stamp would skip rows purged before a field was added to the definition, which is
  the exact gap that needed `RepairIncompletelyPurgedCandidatesAsync` on the candidate side.
- Audited as `VolunteerExaminerPiiPurged`, with **no contact details in the entry**. Writing them
  into the audit log on the way out would defeat the purpose; a test asserts they are absent.
- A returning VE has `PiiPurgedUtc` cleared when their details are re-entered, so the record stops
  claiming to be purged while holding an address.

## What this does not cover

- **VE self-service erasure on request.** Considered and not built: only one VE of 176 has a login,
  and self-service is entered by a link mailed to the address on file, so almost nobody could reach
  it. Worth revisiting if logins become common.
- **The candidate side**, which is Phase 10 and unchanged — see `docs/pii-purge.md`.
