# VEC identity: display name vs. ExamTools code

**Added 2026-08-01.** Fixes a silent, permanent ingestion failure for any VEC whose ExamTools code
is not its name — found live when GLAARG sessions were being skipped every poll.

## The bug

`SessionIngestionService` resolved a session's VEC by matching ExamTools' per-session `vec` field
against `Vec.Name`, case-insensitively:

```csharp
var vecCode = remote.Vec.ToLowerInvariant();
var vec = await dbContext.Vecs.FirstOrDefaultAsync(v => v.Name.ToLower() == vecCode, ct);
```

That worked only because the one VEC in use was ARRL, which ExamTools reports as `"arrl"` — the code
and the name coincide. **GLAARG reports `"lagroup"`.** So a correctly-created VEC row named "GLAARG"
would never match, and every GLAARG session would be skipped forever.

The failure mode is the dangerous part: no exception, no failed row, no red banner — just
`SessionsSkippedNoConfig++` and one `[WRN]` line per poll. The sessions are simply absent, which
looks identical to "ExamTools hasn't published them yet." This surfaced only because the warning was
read directly out of the Worker log during an unrelated status check.

## The fix

New nullable `Vec.ExamToolsCode`. Null means "the code is the same as the name," so every existing
row keeps working untouched and the common case needs no data entry. `Vec.MatchCode`
(`ExamToolsCode ?? Name`) is the effective value ingestion matches on.

Ingestion spells the coalesce out in the query rather than using `MatchCode`, so EF Core can
translate it to SQL:

```csharp
v => (v.ExamToolsCode ?? v.Name).ToLower() == vecCode
```

Admin → VECs gained an "ExamTools code" column and a field on both the create and edit modals,
with helper text naming the GLAARG case. Blank stores null. A code typed to exactly match the name
also stores null — otherwise a later rename would silently strand the code on the old spelling.

## Uniqueness is checked against the coalesce, not the column

Two VECs resolving to the same match code would make ingestion ambiguous, so `VecManagementService`
rejects that with a new `VecActionResult.DuplicateExamToolsCode`. The check runs against
`ExamToolsCode ?? Name` on both sides: a new VEC coded `lagroup` is rejected if some other VEC is
merely *named* `lagroup`, not just if another one is coded that way.

There is a matching unique index, `IX_Vecs_ExamToolsCode`. It relies on SQLite treating NULLs as
distinct — otherwise the second ordinary VEC (code null) would fail to insert.

## Two traps worth knowing

1. **`v.Id != excludingVecId` with a nullable int silently matches nothing.** The duplicate check
   takes `int excludingVecId` and the create path passes `0` (never a real key). Written as `int?`,
   the create path's `Id <> NULL` is SQL NULL, so the query returns no rows and waves *every*
   duplicate through — and the InMemory provider the tests use evaluates it as plain LINQ, where
   `Id != null` is true, so the tests would have passed anyway.

2. **A repeated placeholder breaks structured logging.** The reworded skip warning originally used
   `{VecCode}` twice, which fails `CA2017` as a build error — Serilog binds placeholders
   positionally, not by name.

Both are why `VecExamToolsCodeSqliteTests` exists: the rest of the suite runs on EF InMemory, which
cannot tell whether a query translates to SQL or whether the unique index tolerates repeated NULLs.
Those two facts are provider behaviour, so they are pinned against real SQLite.

## Seeding the codes

**Resolved 2026-08-10 (issue #83) — all fourteen are now seeded automatically. The section below is
kept because it explains why the codes are treated as untouchable data rather than a guess.**

### Where the full list came from

The `From VEC` filter on <https://hamstudy.org/sessions> lists every VEC, each linking to
`/sessions/{code}/inperson` — and that slug is the same code space ExamTools puts on a session's
`vec` field. There are exactly fourteen entries, which is the number of FCC-accredited VECs, so the
list is complete rather than a subset of whoever is onboarded. Three of the codes (`arrl`,
`lagroup`, `sandarc`) had already been confirmed against live ExamTools data, and all three agree
with the filter — that agreement is what makes the other eleven trustworthy rather than guessed.

It corrected two things believed here previously: `sandarc` displays as **"SANDARC"**, not
"SANDARC-VEC", and ARRL displays as **"ARRL-VEC"**, so the reasoning that ARRL's code equals its
name holds only because this deployment happens to have named the row "ARRL". **Nine of the fourteen
have a code that differs from the display name** — GLAARG was never the exception it looked like.

The one caveat: this is a session-search facet, not a documented registry endpoint. Better than
guessing, which is what the rule below exists to prevent, but still inference from a UI.

### `KnownVecs` and `VecDefaultsSeeder`

`KnownVecs.All` (Core/Admin) is the fourteen rows as data. `VecDefaultsSeeder` runs from Worker
startup next to `EmailDefaultsSeeder` — in **every** environment, since a team onboarding under an
unseeded VEC is exactly the silent-skip case this whole document is about.

**It only ever fills gaps; no existing row is modified.** A VEC is considered already present when
its `ExamToolsCode ?? Name` matches a known code case-insensitively, so a deployment that named its
row "ARRL" or coded GLAARG by hand keeps its own name, notes and youth-program flag. Verified
against a copy of the dev database: five hand-made rows in, five untouched, nine added, no second
ARRL.

Two cases it deliberately declines to fix, because both mean a human has to look:

- **The name is taken by a row resolving to a different code.** `IX_Vecs_Name` is unique, so
  inserting would throw and take Worker startup down with it. Skips with a warning instead — this is
  almost certainly the same real VEC with a wrong code, i.e. the original bug.
- **An existing row's match code isn't one of the fourteen.** The code space is closed, so that row
  can never match a session; it is a typo or has been silently doing nothing. One warning per row.
  Worth reading, because the seeder may have just added the correctly-coded row beside it, leaving
  any `FeeConfiguration` attached to the dead one.

**A newly accredited VEC is a code change, not a feature.** The FCC accrediting a fifteenth VEC is
rare enough that the right response is adding a row to `KnownVecs` once it exists and its code has
been read from real data — not building admin tooling (a code picker, an import) to anticipate it.
Admin → VECs still allows a hand-made row for the gap between accreditation and the next deploy.

`DevDataSeeder`'s guard had to change with this: it checked `Vecs.AnyAsync()`, which is now always
true by the time it runs, so it would have seeded nothing on a fresh dev database. It checks for a
`FeeConfiguration` instead and looks the ARRL row up rather than creating it — the same
specific-rows-not-table-wide rule that `DevAuthSeeder` learned in CLAUDE.md's Known Constraints.

### The original per-team discovery route

ExamTools has no endpoint that lists VECs globally. The codes are also readable from
`GET https://alpha.exam.tools/api/teams/team`, which returns **the calling VE's own team
memberships** — each `teamDoc.vecs` is a string array of the codes that team may run under (the
sibling `delegateVecCreds` lists the subset it holds delegated credentials for). An authenticated VE
session is required; unauthenticated requests get the SPA shell, and a bare fetch redirects to
`/portal/veLogin`.

Read live 2026-08-01 across the five teams on Mike's account:

| ExamTools code | VEC | Teams seen on | Confidence |
|---|---|---|---|
| `arrl` | ARRL VEC | HRCC, San Diego ARRL VE Team, WX0MIK, MARC | **Confirmed** — live ingestion since Phase 1. Code equals name, so `ExamToolsCode` stays null |
| `lagroup` | GLAARG | HRCC, San Diego Area Licensing Exams | **Confirmed** — the code in the Worker log that prompted this fix |
| `sandarc` | SANDARC | HRCC | Code confirmed here; **name later corrected from "SANDARC-VEC"** by the HamStudy list above |

Why this route could never be the seed list on its own: **it is scoped to one VE's teams, not to
every VEC that exists**, and it carries the code and nothing else — no display name. It stays useful
as the way to confirm a code against ExamTools' own data, which is a stronger source than the
session-search facet.

**Do not guess a code.** A wrong one fails exactly the way the original bug did: silently, with
sessions quietly missing. Add a VEC by hand only once its code has been read from real ExamTools
data — re-run the fetch above as the VE for the team in question. The seeded fourteen should already
cover it.
