# VE import and export (issue #142)

Bulk-adding VEs from a CSV, and getting the directory back out again. See
[`docs/ve-management.md`](ve-management.md) for the person model these operate on.

## Export

One row per person per team, matching exactly what the current filters show — team, search, tag and
show-retired all ride along on the link. Export-what-you-see, so a filtered list and its file cannot
disagree about who is on it.

**Audit-logged, unlike the Job History export.** The page is already TeamAdmin/SystemAdmin only, but a
screen someone reads and a file they can mail onward are different kinds of exposure, and only one of
them leaves the building — this carries real home addresses and phone numbers in bulk. The entry
records who exported and how many rows, never the contents.

The CSV helper lives in `Web/CsvExport` rather than being copied out of Job History's private method.
Its formula-injection guard matters more here than it did there: **Excel and Sheets evaluate a cell
starting `=`, `+`, `-`, `@`, tab or carriage return**, and quoting does not stop it — Excel strips the
quotes and evaluates what is inside. Job History's risky text was exception messages; this is names
and notes typed by people, in a file that gets mailed around a team.

## Import

Two steps, always: upload previews every row, and nothing is written until that is confirmed.

**The confirm step re-posts the file's TEXT, not the parsed rows.** Posting a structure back would
mean applying whatever the browser returned — which the server would have to re-validate anyway —
whereas posting the text runs the identical parse twice. What was reviewed is what happens, from one
function, with no second code path to drift.

### Rules that stop an import damaging existing data

**A blank cell means "no opinion", never "delete".** A spreadsheet that omits a column must not
silently empty a phone number, which is the kind of loss nobody notices until they need it.

**Import is the other duplicate-generating path**, so it matches on the same identity rules as the
sync:

- FRN first, when the file supplies one — so a file listing someone by a call sign they no longer hold
  still finds them.
- Then a usable call sign. Never a placeholder: `<UNKNOWN>` is rejected outright rather than creating
  a person nobody can identify and no license check can resolve.
- Someone already serving **another team gains a membership**, not a second record. That is the whole
  point of the person model.
- One call sign appearing **twice in a file** is an error on the second row, not a silent overwrite of
  the first.

**An FRN in the file is used to *find* a person and is never written.** It is the identity key, it is
unique, and the nightly FCC sweep owns it — accepting a typo would either collide with a real record
or quietly attach the wrong identity to this one.

### Round trip

Lossless. The export's formula-injection apostrophe is stripped on the way back in, so a name does not
accumulate one per export/import cycle. A file exported from the directory can be edited and
re-imported as-is.

### Columns

`CallSign` and `Name` are what matter; `Email`, `Phone`, `AddressLine1`, `AddressLine2`, `City`,
`State`, `PostalCode`, `Discord` and `Frn` are read if present. Any other column is ignored rather
than rejected — a team's own spreadsheet will have extras. A row needs at least a name or a usable
call sign.

The parser is a hand-rolled RFC 4180 field splitter (quoted fields, doubled quotes, commas inside
quotes) rather than a package: the format it reads is the one this app writes, and a dependency for
twenty lines needs asking about first (CLAUDE.md's NuGet rule).

## Not built

Import cannot set tags, accreditations or team membership beyond the team being imported into. Those
are per-team decisions with their own validation, and a CSV column is a poor place to make them.
