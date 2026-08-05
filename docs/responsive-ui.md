# Responsive / mobile-first UI

Until 2026-08-05 the admin UI was desktop-only **by construction**: `wwwroot/css/app.css` was 307
lines of hand-rolled design system containing **zero media queries**. The viewport meta tag was
present in both layouts, so a phone rendered the page at device width and everything simply
overflowed — a 56px fixed-height chassis holding brand + five nav items + three dropdowns, and up to
sixteen table columns, all on a 390px screen.

This pass makes the whole site work on a phone. It is a CSS/JS change plus one markup class per
table; no page model, service, or query was touched.

## The breakpoint, and why the base layer is the mobile one

There is one breakpoint, **768px**, written as `max-width: 767.98px` / `min-width: 768px` so the two
can never both match at a fractional viewport width.

`app.css` is now genuinely mobile-first: everything above the "Responsive layer" banner near the
bottom of the file **is the phone layout**, and it is what a device gets with no media query
evaluated at all. Two layers sit on top:

1. **Card tables** (`max-width: 767.98px`) — a treatment that only exists below the breakpoint.
2. **Desktop layer** (`min-width: 768px`) — restores the original single-row chassis and the roomier
   desktop spacing and type scale.

The desktop layer is a deliberate *restoration*, not a redesign: the design that was already live
and in daily use is unchanged from 768px up. If a desktop rule looks like it is only there to undo a
mobile rule, that is exactly what it is.

There is no `--bp-md` custom property, despite the comments using that name. Custom properties
cannot be used in a media query condition, so a token would be a decoration the queries themselves
could not reference.

## The chassis nav

Below the breakpoint the header bar carries only the brand and a `☰` toggle. The nav links **and**
the `.who` cluster (user menu, Help, theme toggle) are one collapsed panel that
`header.chassis.nav-open` reveals; `app.js` toggles the class, keeps `aria-expanded` in step, and
closes the panel on Escape or a tap outside the header.

Two things inside the panel are easy to get wrong:

- `.nav-group` / `.user-menu` / `.help-menu` must be **`flex-direction: column`** on mobile. Once
  their `.menu` becomes `position: static` it is a real sibling of its trigger in that flex
  container, and the default row direction lays the menu out *beside* the button instead of under
  it. This shipped broken in the first draft and was caught in the harness.
- The chassis dropdowns become inline accordions rather than floating menus. A `position: absolute`
  dropdown inside a stacked panel overlays the links beneath it.

## Tables: two treatments, chosen per page

| Treatment | Applied to | How |
|---|---|---|
| `table.cards` | Session Manager screens — Sessions, Session Detail, Applicant Status, Candidate Detail, Unmatched Payments, VE Roster | Each row restacks into a labelled card below the breakpoint |
| `.table-scroll` wrapper | Admin/reference screens — Audit Log, Job History, Users, VECs, Fees, Teams, Team Maintenance | Table keeps its columns and scrolls sideways inside the wrapper |

The split is by *who opens the page on a phone*: a Session Manager running a session needs the SM
screens to read well one-handed; the admin tables are configuration screens. **Promoting an admin
table to cards later is a one-word markup change** — add `class="cards"` — which is why the labels
are generated rather than hand-written. That was an explicit request during this work.

### Labels come from the `<th>` at runtime

`app.js`'s `labelCardTable` stamps each `<td>` with `data-label` taken from its own column's `<th>`,
and the CSS renders it via `content: attr(data-label)`. Hand-writing `data-label` on every cell would
have meant hundreds of attributes to add and to keep in step with the headers forever after.

Two cases are marked rather than labelled:

- `.is-unlabelled` — the column has no header text (the View / `⋮` action columns). The cell drops
  the empty label gutter and uses `display: inline-flex`, so consecutive action cells sit together
  on one final row instead of each taking a row.
- `.is-blank` — the cell rendered nothing. A desktop table needs the empty cell to keep its grid
  aligned; a card row reading `FRN —` with no value is noise. Emptiness is tested on text content
  **and** the absence of `a, button, input, select, svg, img`, or a cell holding only an icon button
  would be judged empty and hidden.

### The grid trap inside a card cell

A card cell is `display: grid` with a label column and a value column. Grid auto-placement sends the
*second* child of a cell back to **column 1, directly under the label** — and a two-line cell like
the Sessions list's title + sub-line is exactly that. `table.cards td > *` therefore pins every
element child to `grid-column: 2`. The same rule carries `justify-self: start`, without which every
status chip stretches into a full-width bar, since a grid item fills its column by default.

Text-only cells still auto-place correctly into column 2 and need no help.

### Sorting

`thead` is hidden in card mode, so **client-side sorting is unavailable on a phone** for the tables
that use it. The sort still applies if one was remembered from a desktop visit (it is stored in
`localStorage` and reorders rows, which cards inherit). This is a known, accepted limitation rather
than an oversight — surfacing a sort control per card was not worth the complexity for screens whose
result sets are already filtered and paged.

## iOS zoom — why controls are 16px on mobile

iOS Safari auto-zooms the viewport when a control with `font-size` below 16px receives focus, and it
never zooms back out. Every focusable control is therefore 16px on mobile and drops to the design's
12–14px at the breakpoint.

This is why the repeated `style="… font-size:12px …"` attributes on the Sessions and VE Roster
date-range inputs, and the Unmatched Payments candidate `<select>`, were replaced with a
`.menu-input` class: **an inline `font-size` cannot be overridden by a media query**, so those
controls were unreachable while the style stayed inline. The rest of the site's ~139 inline `style=""`
attributes are `max-width`, which is mobile-safe, and were left alone (they are also load-bearing for
the CSP `style-src` allowance — see `docs/security-hardening-2026-08-03.md`).

Touch targets were raised to roughly 44px on mobile: nav links, kebabs, menu items, buttons and
filter pills all gained padding, reverting to the tighter desktop values above the breakpoint.

## Verifying responsive changes (the framing gotcha)

**The app cannot be loaded in an iframe.** The 2026-08-03 hardening pass sends `X-Frame-Options:
DENY` and CSP `frame-ancestors 'none'`, so the obvious approach — framing `localhost:5158` at 390px
to evaluate media queries — fails silently with a broken-image placeholder. Do not weaken those
headers to test layout.

Chrome also enforces a minimum window width of roughly 500px, so `resize_window` cannot produce a
true phone viewport either.

What works, and what was used here: a **self-contained harness** — an HTML file with the real
`app.css` and `app.js` inlined and representative markup copied from `_AppLayout.cshtml` and the page
being checked, served over a throwaway local HTTP server and loaded into iframes sized 390px and
1180px. Media queries evaluate against the iframe's own viewport, so this gives a true mobile render
of the actually-shipped CSS and JS, with no login required. The harness can also assert rather than
just look — the admin-table check read back `documentElement.scrollWidth === clientWidth` (page never
scrolls sideways) and `wrapper.scrollWidth > wrapper.clientWidth` (the table does).

One escaping trap: if the harness is injected via `srcdoc` or a JS string, its own `</script>` ends
the host page's script block early. Write the harness to its own file and use `src=` instead.

### What is *not* verified

Every Session Manager and Admin page is `[Authorize]`d, and Claude does not enter the dev password
(see CLAUDE.md). The harness proves the CSS and JS mechanisms against real markup; it does not prove
each authenticated page's own content renders well with real data. The remaining check is a
logged-in pass over Session Detail, Applicant Status, Candidate Detail and Team Settings at phone
width.
