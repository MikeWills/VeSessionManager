# Click-to-sort table columns

Requested 2026-07-31: *"add a sort option on all tabular data. I'd love to click on the column and
sort ascending, click again to descend, and a third time to remove the sort. If I click on another
column, it removes the previous sort and sorts that column."* — plus a follow-up: *"remember the sort
between pages, so when I go back to that page the sort sticks."*

## Two mechanisms, one appearance

Every table in the app now sorts, but not all of them by the same route.

**Client-side (`app.js`, the default).** Nearly every table here renders its full result set in one
page — the VE roster, the candidate roster, Applicant Status' two tables, Unmatched Payments, and
every Admin list. Reordering the rows already in the DOM is the whole job, so a single shared vanilla-JS
sorter handles them. Opt a table in with `data-sortable="<key unique on this page>"`.

**Server-side (`IndexModel`, the Sessions list only).** The Sessions list is the one table that
**pages server-side** (10–100 rows per page out of a much larger set). Sorting only the rows currently
on screen there would look exactly like a sort of the whole result set and silently wouldn't be one —
a Session Manager sorting by "Candidates" descending would get the largest session *on page 3*, not the
largest session. So that page carries real `sort`/`dir` query parameters, applied to the `IQueryable`
before `Skip`/`Take`. Its headers render as ordinary links whose `href` is the next state in the click
cycle. It deliberately does **not** carry `data-sortable`, so the JS sorter leaves it alone.

Both produce the same DOM shape — `th.sortable` plus a `.sort-arrow` span, with `aria-sort` carrying
the state — so `app.css` styles them identically and the split is invisible to a user.

**The rule for any new table: if it pages server-side, sort server-side. Otherwise `data-sortable`.**

## The cycle

Ascending → descending → back to the order the server rendered. Clicking a different column replaces
the sort rather than adding a second key. Client-side, "the order the server rendered" is recovered
from a `sortOriginalIndex` expando stamped on each row at init; server-side it's the existing
date-ordering logic, which is what `ApplySort` falls back to when no column is selected.

On the Sessions list, sorting resets to page 1 — the row that was on page 3 is somewhere else
entirely once the ordering changes.

## Sort values vs. displayed text

A cell sorts on its `data-sort-value` attribute when present, otherwise its trimmed visible text.
Numbers (including `$12.00` and `1,234`) are detected and compared numerically; everything else uses
`localeCompare` with `numeric: true`. Blank-ish values — `""`, `—`, `-`, `–` — always sort last, in
both directions, so rows that actually have data stay together at the top.

`data-sort-value` is required whenever the display form doesn't sort correctly as text:

- **Dates, always.** `"MMM d, yyyy"` sorts alphabetically — *Apr* before *Mar*, and 2019 before 2026.
  Every date column emits a round-trip (`"o"`) timestamp instead. Several page-model row records grew
  a `...SortValue` member purely to carry the raw `DateTime` alongside the formatted `...Line`
  (`PendingRow`, `RecentlyIssuedRow`, `UnmatchedPaymentRow`, `PaymentRow`, `OtherAttemptRow`).
- **Cells rendering more than one thing.** The candidate cell on the session Detail page stacks a name
  over a call-sign/FRN sub-line; the payment status cells append refund/mismatch/expiry tags. The
  concatenation of all that text is not what the column means.
- **Cells whose text is a glyph.** The Tested column's ✓/· sorts on `1`/`0`.
- **Cells with a trailing link.** The FRN and Call sign columns on Applicant Status append a `↗` ULS
  link that shouldn't ride along in the key.

Columns with nothing to order by (a per-row `<select>`, an "Open link ↗" anchor) are opted out with
`data-sort="none"`. Headerless action/kebab columns are skipped automatically.

Server-side, the Status and VEC submission columns sort on a SQL `CASE` that reproduces the chip label
`ToRow` renders, not on the underlying columns — each of those cells collapses several fields (`Status`,
`RescheduleFlaggedForReview`, `TestingCompletedUtc`) into one label, and ordering by anything other than
what the user can read in the cell would look broken. Every sorted query also gets a `ThenBy(s => s.Id)`
tiebreak: without one, equal keys can come back in a different order per request, so paging through them
silently repeats and skips rows.

## Remembering the sort

**Client-side:** `localStorage`, keyed by `vesm-sort:<pathname>#<data-sortable value>`. Keyed by
pathname rather than the full URL on purpose — a sort chosen on one candidate's detail page should
apply to the next candidate's too.

The stored key is the **header's own label**, not its column index. Several of these tables show a Team
column only when the user can see more than one team, which shifts every index to its right; a stored
index would silently restore onto the wrong column. If the remembered column is genuinely absent on
this render (the user narrowed to a single team), the stored sort is dropped rather than guessed at.

All `localStorage` access is wrapped in `try`/`catch` — it throws in some privacy modes and when the
quota is full. Sorting itself doesn't depend on it, so a failure just means the sort isn't remembered.

**Server-side:** the Sessions list rides along in the existing `vsm_session_filters` cookie, which
already remembers Status/TeamId/PageSize/DateRange for a bare navigation back to the page. Two fields
were appended (`|sort|dir`) and the parser's `Split('|', 4)` widened to `6`. A cookie written before
this change simply has fewer parts, so it degrades to the defaults; every field is re-validated on the
way out, and `sort` must name one of `SortableColumns`, so a hand-edited cookie or query string can't
reach an arbitrary ordering expression.

## Accessibility

Sortable headers are focusable (`tabindex="0"`, `role="button"`) and respond to Enter/Space as well as
click. `aria-sort` carries the current state and is what the ▲/▼ arrow is keyed off in CSS, so the
visual indicator can't drift from what a screen reader announces.
