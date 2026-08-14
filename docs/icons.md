# Icons

The UI uses **Bootstrap Icons 1.11.3** (MIT), vendored at
`wwwroot/lib/bootstrap-icons/` — CSS, the `woff2`/`woff` fonts, and the license.

```html
<i class="bi bi-caret-down-fill" aria-hidden="true"></i>
```

For icons driven from CSS rather than markup — anything toggled by a state attribute, like the sort
arrows — use the font directly:

```css
.sort-arrow::after { font-family: bootstrap-icons; content: "\f127"; }
```

Codepoints are in `bootstrap-icons.css`; grep it for the icon name.

## Why a library at all

Before this, affordances were bare Unicode characters typed into the markup — `▾` for dropdowns, `⋮`
for row menus, `⚑` for the reschedule flag, `↻` for refresh, `✓` for tested.

**Those work only if the user's device happens to have a font containing them.** IBM Plex Mono, which
this app loads, contains some and not others, so the browser silently falls back to whatever else is
installed — and what is installed differs per device.

That is not hypothetical. The withdrawn-roster disclosure used `content: "\25B8"` (BLACK
RIGHT-POINTING SMALL TRIANGLE), which rendered fine on the development machine and appeared as a
**tofu box showing its own codepoint** on an iPhone (reported 2026-08-06). The sort arrows three
rules away used `\25B2`/`\25BC` and looked fine — which is exactly what made the omission easy to
miss: the same technique appeared proven.

An icon font ships the glyphs with the app, so there is no fallback lottery.

## Why Bootstrap Icons, and why self-hosted

- **Standalone.** It needs no Bootstrap, which is what made it viable here: Bootstrap itself was
  vendored once, used by essentially nothing, and **deleted in v0.3.0** (8.4 MB). Every screen runs
  on `app.css`. Bootstrap Icons stayed because it is a font, not a framework.
- **MIT, no free/pro split**, so no icon is unexpectedly unavailable.
- **Self-hosted is mandatory, not a preference.** The CSP allows `font-src 'self'` plus Google Fonts
  only — a CDN reference would be blocked outright.

Loaded by both `_AppLayout` and `_PublicLayout`, before `app.css` so the design system can override
it. `app.css` carries one shared rule (`.vesm .bi`) nudging the baseline, since Bootstrap Icons sit
slightly high against IBM Plex; individual icons need no styling and inherit colour and size from
context.

## Conventions

- **Always `aria-hidden="true"`.** Every icon here is decorative — the surrounding text or the
  control's own `aria-label` carries the meaning. A screen reader announcing "caret down" after
  "Settings" is noise.
- **An icon-only control needs its own `aria-label`** (see the row kebabs — the Teams list's says
  which team it acts on, since a page full of identical "Actions" buttons tells a screen-reader user
  nothing).
- **Inline SVG is still fine** where it already exists — the theme toggle. It predates the font and
  has no fallback risk, so there was no reason to churn it. The Teams row's gear and maintenance
  SVGs were also on this list until 2026-08-06, when the row gained a third action and became a
  labelled kebab menu like every other admin table; two glyphs had been defensible, three ambiguous
  ones were not (an envelope for "email templates" reads as "send something").

> **Do not put an icon inside a C# string literal.** `@(c.Tested ? "<i class=…>" : "·")` breaks the
> Razor expression, because the markup's quotes terminate the string. Use a conditional block
> instead: `@if (c.Tested) { <i …></i> } else { <text>·</text> }`. A bulk find-and-replace across
> `.cshtml` will walk straight into this, and into Razor comments whose prose legitimately contains
> arrows.
