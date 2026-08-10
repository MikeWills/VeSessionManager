# VE management (issue #142)

Managing the people a VE team works with — who they are, how to reach them, and what they are to each
team — rather than counting how many sessions they worked. That count already existed
(`VolunteerExaminerReportService`, the **VE Session Counts** page — called VE Roster until
2026-08-07, renamed because it never was one) and is deliberately untouched; the issue says
outright that session counts are not the focus.

## The change everything else hangs off

`VolunteerExaminer` **was a per-team row**: it carried a `TeamId` and was unique on
`(TeamId, CallSign)`. One human serving two teams existed twice, with nothing linking the halves.

Every single thing #142 asks for — contact details, tags, VEC accreditations, self-service — is a
fact about the *person*, not about one team's copy of them. So the row became the person, and
`VeTeamMembership` carries the per-team part. Same shape as the earlier `User.TeamId` → `UserTeams`
change (issues #17/#19).

| Table | Holds |
|---|---|
| `VolunteerExaminer` | The person: name, call sign, FRN, contact details, cached FCC license state |
| `VeTeamMembership` | One person on one team — active/retired, and where tags hang |
| `VeTag` / `VeTagAssignment` | A team's own vocabulary, applied per membership |
| `VeVecAccreditation` | Person ⇄ VEC, hand-entered, presence only |
| `VeCallSignHistory` | Call signs they used to hold |

Tags hang off the *membership*, not the person, because someone can be a full member of their home
team and a guest elsewhere, and one set of tags on the person cannot say that.

## Identity: the id, then the FRN, never the call sign

**A call sign changes. The person does not.** Keying identity on the call sign would mean a VE
becomes a *new person* the day their vanity call comes through, orphaning their session history at
exactly the moment someone would want it.

So:

- **`Id`** is the identity. Every relationship points at it, so a rename is invisible to session
  history, memberships and accreditations.
- **`Frn`** is the stable external key — FCC's registration number survives a call sign change.
  Unique where present.
- **`CallSign`** is a mutable *attribute*. When FCC reports a different one, the value is replaced and
  the old one written to `VeCallSignHistory`.

ExamTools' roster never reports an FRN, so it arrives only from the ULS sweep
([`docs/ve-license-tracking.md`](ve-license-tracking.md)) — which is why that issue and this one
ended up depending on each other.

### `CallSign` is deliberately NOT unique

Tempting, since only one person holds a call at a time. Rejected for two practical reasons:

1. **The migration can only match on call sign**, and a call sign released and reissued to a
   different person would fuse two humans irreversibly. It therefore merges only when call sign *and*
   name agree — and a unique index would reject the rows it deliberately leaves alone, turning a
   data-quality question into a migration that cannot run.
2. Those survivors are surfaced as **possible duplicates** for a human. A constraint cannot express
   "probably the same person, ask someone".

Uniqueness is enforced where it is knowable: on `Frn`.

## ExamTools owns membership and nothing else

It supplies a call sign and a name on a session roster, and has no contact information at all.
`Name` is seeded from it the first time a VE is seen and is **app-owned from then on**.

That is a change, and an important one. The sync used to re-apply ExamTools' name on every poll,
justified in a comment saying nothing in the app could edit it — true at the time. #142 created
exactly that edit path, so the same code would have undone an admin's or a VE's own correction within
the hour. The same trap the manual VE roster buttons fell into (see
[`docs/session-manager-ui.md`](session-manager-ui.md)).

The sync also **never touches `IsActive`**. Retiring a VE is a human decision; if the sync
"corrected" it, someone retired would quietly reactivate the next time they turned up on a roster.

### A placeholder is not an identity

ExamTools reports the literal `<UNKNOWN>` when it has no call sign. Treated as an ordinary value it
looks like one call sign shared by many people — and it fused HRCC's unidentified VE with MARC's into
a single person carrying 88 sessions of both their histories. Found by running the migration against
real data; every test used realistic call signs and sailed past it.

`Core/CallSign.IsUsable` is now the single answer to "is this a call sign at all". The rule is
structural rather than a list of known placeholders — letters, digits, an optional slash, and at least
one letter and one digit — so it catches the next placeholder without anyone predicting it. Used by
the migration, the sync, the license sweep, the importer and the duplicate flag.

## Merging duplicates

Duplicates exist by design: the migration is conservative. The FRN backfill then turns suspicion into
**proof** — FRN is unique per person, so two records resolving to one is conclusive, unlike a shared
call sign.

`VolunteerExaminerMergeService` does it in **one transaction**, because a half-merge is the only
outcome with no recovery. Six things point at the retired record, three with uniqueness constraints
that collide:

- **Session links** repoint; where both records worked the *same* session the two collapse to one.
  Not data loss — one person cannot be on a roster twice, so a count of 1 is the correct answer.
- **Team memberships** fold on `(VeId, TeamId)`: active beats retired, tags union.
- **Accreditations** fold on `(VeId, VecId)`: presence-only, so the duplicate is simply dropped.
- **Call sign history** moves, and the retired record's own call sign becomes history if it differs.
- **Contact details fill blanks, never overwrite** — nothing a human typed on the survivor is
  replaced by whichever record happened to lose.
- **FRN transfers rather than copies.** Leaving it on both violates the unique index; the SQLite test
  caught that, which an InMemory one could not.

Three mechanisms carry the integrity guarantee:

1. **The transaction.**
2. **A conservation check inside it** — distinct sessions before must equal distinct sessions after,
   asserted against the database rather than the in-memory graph, rolled back if it fails. That turns
   "we believe nothing was lost" into something the code refuses to commit if untrue.
3. **The loser is retired, never deleted** — `MergedIntoVolunteerExaminerId` plus a **global query
   filter**, so it vanishes from every query at once rather than leaving an invariant each future
   query must remember.

The audit entry records **which session ids moved**. Without that, an un-merge could not tell whose
history was whose, and calling the merge reversible would be an overclaim.

Two different FRNs is a **hard block**: FCC saying these are two people is stronger evidence against
the merge than a matching name is for it.

### `ConflictingFrn`

When the sweep finds a collision it cannot store the FRN — the unique index refuses it. That proof
was originally only a log line, so the merge screen could see nothing but a shared call sign and
called a *proven* duplicate "needs checking". `ConflictingFrn` stores what the index refused. Not
indexed, not unique: it is a note about a collision, not an identifier.

## The screens

All under the existing **VEs** nav dropdown, per the issue's own request.

| Page | Who |
|---|---|
| VE Directory — **one row per person**, teams listed on it; search, tag filter, last-worked, license, duplicate marker | TeamAdmin / SystemAdmin |
| VE detail — contact details, teams and tags, accreditations, FCC license | TeamAdmin / SystemAdmin |
| VE Tags — the team's vocabulary | TeamAdmin / SystemAdmin |
| Possible duplicates / merge | TeamAdmin / SystemAdmin |

**The admin gate is a data-protection boundary, not tidiness.** These rows carry home addresses and
phone numbers, which — unlike call sign, FRN and license class — are **not public FCC record data**. A
VE's public record usually carries a PO box precisely because they chose not to publish where they
live. Session Managers and Team Leads get no access. The nav gate matches the page attribute, so no
role is shown a link that 403s.

Two consequences of that, both deliberate:

- The **contact-details audit entry records that it changed, not what to.** The audit log is readable
  by roles not entitled to see an address, and a diff would route around the restriction.
- The **CSV export is audit-logged** where Job History's is not — a screen someone reads and a file
  they can mail onward are different kinds of exposure.

### Editing and colors (2026-08-09)

A tag was originally **create-and-delete only**. Changing its display order or fixing a typo meant
deleting it and adding it back — and deleting cascades the assignments away, so correcting a
*display detail* silently untagged everyone who had it. `SortOrder`'s own doc comment promised a team
could "put its most-used tags first", which the app had no way to actually do after the fact.

`UpdateTagAsync` edits in place. The row keeps its id, so assignments survive **by construction**
rather than by care.

Tags also carry an optional `#RRGGBB` **color**, set with a native color picker. It shows three
places: the tag's chip in the directory, a dot beside its checkbox on the VE page, and — the point of
the request — a **stripe down the team panel** on a VE's detail page, so a team is identifiable at a
glance.

**The winning color is the highest-priority tag that has one**, where "highest" means shown first,
i.e. the **lowest** `SortOrder`. Those two phrasings read like opposites, so the rule lives in one
place (`VeTagColor.ForTags`) with a test of its own. An *uncolored* higher tag doesn't win by being
first — it has no color to contribute — because someone who colors only their "Team lead" tag expects
that color to show.

Two implementation notes worth keeping:

- **A tag color is the first user-supplied value in this app written into a CSS context.** Razor
  HTML-encodes the attribute, which stops it escaping into *markup* — but not into the *stylesheet*.
  A stored `red; background-image: url(https://evil/x)` is valid HTML and would be honored as CSS,
  and the CSP allows inline styles (`style-src 'unsafe-inline'`), so nothing downstream blocks it. The
  value is pinned to exactly `#RRGGBB` and checked **on write and again on render**
  (`VeTagColor.ForStyle`) — the second check covers a row that arrived some other way.
- **The color renders as a tint, not a fill** — 18% behind the label, full strength on the dot.
  Filling the chip would need luminance math to choose the text color, and that gets it wrong exactly
  at the mid-tones people pick most.
- **`<input type="color">` has no empty state.** It always posts a value, and `#000000` is a color a
  team might genuinely want, so it can't stand in for "none". A paired "Use" checkbox expresses that
  instead.

### The tag filter groups by name

Same reasoning as the directory rows, applied one control later. On "all teams" the filter listed
every tag row, so two teams each defining "Member" produced **two identical, unlabelled radio
buttons** — and picking either one silently excluded the other team's people. The rows had always
collapsed same-named tags into one chip, so the filter disagreed with the column it filtered.

The filter is now keyed on the tag **name** (`?tagName=`, previously `?tagId=`), one entry per
distinct name, carrying the same color the chips use. Matching is case-insensitive on both sides
because SQLite's `=` on TEXT is not, and the row-level dedupe was already `OrdinalIgnoreCase`.

### Filtering to guests

"Guest" means no tag at all, and it is **derived rather than stored** — a stored guest tag would need
adding and removing in step with every other tag change, and would be wrong in between. So it cannot
be one of the names in the filter list and takes a sentinel value
(`VolunteerExaminerDirectoryService.GuestTagFilter`, a leading-space string no team can define
because tag names are trimmed and required non-empty — same trick as the invite picker's untagged
option).

**It is applied after the grouping, not in the query**, and that placement is the whole design.
`IsGuest` is a property of the finished row — no tags on *any* team in scope — while the query is
still per-membership. Filtering memberships would match the untagged half of someone who *is* tagged
elsewhere, and their row would then appear in a guests-only list visibly carrying tags. Both cases
are pinned by tests, including the one that makes it look inconsistent and isn't: scoped to the team
where they hold no tag, that person **is** a guest, because the row's tags narrow to that team too.

### Filters survive a visit to a VE

Clicking into a VE and coming back used to land on an unfiltered first page. The filters now ride
along on the link in and back out again, as **explicit route values rather than one `returnUrl`
string** — nothing to parse and no open-redirect surface.

The cost is that every one of `VeDetail`'s POST handlers has to carry them on its redirect, which is
what `SelfRoute()` exists to stop anyone forgetting: drop them in one handler and the back link
breaks only after a save, which is the hardest kind of breakage to notice.

**Sort was already remembered** and needed no change — the table sorter persists its column and
direction per page in `localStorage` (see `app.js`), verified against the real script rather than
assumed from the comment. One case does discard it by design: a remembered column that no longer
exists, which the directory can't hit because its Teams column always renders.

### Finding the page

Three links, all added after the VE detail page turned out to be a dead end — it said a team had no
tags without mentioning that a page exists to create them, which is the exact question it prompts:

- The empty state links to that team's tag page.
- A team with tags gets a "Manage *team* tags" link under the checkboxes.
- The directory's team dropdown links to the selected team's tags — omitted on "all teams", where
  there is no single vocabulary to edit.

### Tags grant no access

Several starting names ("admin", "session manager", "team lead") match real roles in this app,
because those are the words the team uses. A VE tagged "admin" gets nothing.
`VeTagsGrantNoAccessTests` scans the authorization sources for any reference to `VeTag` and asserts
the entity has grown no permission-shaped property — this is exactly the kind of promise that erodes
the first time reading a tag would be convenient, while three screens carry on saying otherwise.

**No tag means guest**, derived at render time. A stored "guest" tag would need adding and removing in
step with every other tag change and would be wrong in between.

### One row per person

The directory started as one row per person *per team*, mirroring the session-count report. That was
wrong for this page: the report is a leaderboard where the per-team split is the point, while this is
a directory of people — and repeating a name once per team made a 176-VE roster read as if it held far
more, while burying the fact that those rows were one person. Which is the very thing the person model
exists to express.

Everything per-team collapses across the teams **in scope**: tags union (deduped by name, since two
teams can each define "Team member"), last-worked takes the most recent, and the row is active if any
membership is. Filter to one team and each narrows to that team's answer — which is what makes the
collapse safe rather than lossy. Per-team detail lives on the VE's own page.

### Last worked

`MAX(session date)` over the session links, scoped to the row's own team. It avoids the
`Session.Status` trap for the third time in this codebase: `Status` only ever means "not cancelled",
so filtering on it reports a VE booked for next month as having already worked that session — a
*future* "last worked" date.

## Adding a VE by hand

Added 2026-08-10, so a team can tag and monitor someone they are considering — a prospect who has
never worked one of their sessions.

**This passes the "does ExamTools already do it?" test**, which every in-app admin action here has to
(three were built and then removed for failing it). ExamTools only knows a VE once they are rostered
onto a session. A prospect being watched has never been on one, so ingestion will never produce them
and **nothing else in the app can create this row**. That is the whole justification, and it is worth
re-checking if the feature ever grows.

It runs through the **CSV importer's own add path** rather than a second implementation.
`ApplyRowAsync` — match, then create-or-fill, then ensure the membership — is now shared by both. Two
duplicate-generating paths into the same table each owning a copy of those rules is precisely how the
per-team refresh pipeline drifted before `TeamPipeline` existed.

So a hand-add behaves exactly as the equivalent CSV row would:

- **Already on this team** — nothing changes; blank fields get filled.
- **Serving another team** — they gain a membership here, never a second identity. Splitting one
  person's history in two is the failure the person model exists to prevent.
- **A placeholder call sign is refused.** `<UNKNOWN>` fused two real people once already.
- **A typed value never overwrites a stored one.** Blank means "no opinion", same as the import.
- **FRN is not accepted from the form at all**, for the same reason the importer ignores it: it is the
  unique identity key, and the ULS sweep fills it from FCC. A typo would either collide with a real
  person's record or attach the wrong identity to this one.

The reconciliation that makes it safe is that the ExamTools sync matches on a usable call sign across
the whole table, so a hand-added prospect who later works a real session is **matched, not
duplicated**. That is asserted by its own test, because if it ever stopped being true the feature
would quietly become a duplicate factory — and the duplicate would surface weeks later, at the
session, rather than when the mistake was made.

The panel is collapsed by default. Everyone else arrives through ingestion, and this must not read as
the normal way VEs get onto the list.

## Deliberately not built

- **Editing the roster in-app.** Removed 2026-08-07; ExamTools reconciles it every poll. See
  [`docs/session-manager-ui.md`](session-manager-ui.md).
- **QRZ prefill**, which the issue also asks for. Blocked on credentials: nobody has confirmed whether
  their API returns email and address at all, and designing around an unseen field is how the `RO`/`RM`
  assumption in the Renewal Monitor went wrong. Note it could only ever return the *public* address —
  usually a PO box — so it must never overwrite a hand-entered one.
- **FRN-first matching in the sync.** ExamTools' feed has no FRN, so this would need a ULS lookup on
  the create path, coupling the roster sync to the ULS client. A VE who changes call sign between
  sweeps still creates a duplicate — caught within a day by the collision detector and surfaced for
  merge. Prevention deferred; detection works.
