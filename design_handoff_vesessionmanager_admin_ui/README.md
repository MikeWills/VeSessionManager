# Handoff: VeSessionManager Admin UI

## Overview
Admin backend for volunteer amateur-radio exam session management: session managers view/manage exam sessions, track candidate applications through a status pipeline, and submit results to a VEC (Volunteer Examiner Coordinator). Includes a light/dark theme toggle.

## About the Design Files
The files in this bundle (`tokens.html`, `components.html`, `session-list.html`, `session-detail.html`) are **design references built in static HTML/CSS** — they show intended look, layout, and interaction, not production code to copy directly. The task is to **recreate these designs in the target codebase's existing environment** (React, Vue, etc.) using its established component patterns and state management — or, if no environment exists yet, choose the most appropriate framework and implement there.

## Fidelity
**High-fidelity.** Colors, typography, spacing, and component states are final. Recreate pixel-precisely using the codebase's component libraries where possible.

## Design Tokens

### Colors — Light (default, `:root`)
| Token | Hex | Use |
|---|---|---|
| `--panel` | `#14181A` | Chassis nav bar, primary button bg |
| `--panel-2` | `#1F262A` | Chassis hover/active, role badge |
| `--paper` | `#F5F6F4` | Page background |
| `--surface` | `#FFFFFF` | Card/table background |
| `--ink` | `#1B2023` | Primary text |
| `--ink-soft` | `#5B6460` | Secondary/muted text |
| `--amber` | `#E8A33D` | Pending/warning accent |
| `--amber-dim` | `#F4D9A8` | Amber chip background |
| `--green` | `#3F8F5F` | Success/granted/paid accent |
| `--green-dim` | `#CFE6D8` | Green chip background |
| `--brick` | `#C24C3F` | Error/failed/expired accent |
| `--brick-dim` | `#F1D4D0` | Brick chip background |
| `--line` | `#D8DBD6` | Borders/dividers |
| `--line-strong` | `#B8BEB9` | Stronger borders (inputs, filter pills) |
| `--focus` | `#2E6F8E` | Focus ring, link color |

Additional hardcoded values used at full opacity: chip text colors `#245C3B` (green), `#7A5313` (amber), `#7E2E24` (brick); `#E9EBE7` (chip-neutral bg, ghost/kebab hover bg); `#FBFBF9` (table row hover); nav link `#B9C0BC`; flag-banner border `#E4C486`; menu disabled text `#B8BEB9`.

### Colors — Dark (`[data-theme="dark"]` override)
| Token | Hex |
|---|---|
| `--panel` | `#14181A` (unchanged — chassis stays dark in both themes) |
| `--panel-2` | `#262E31` |
| `--paper` | `#101416` |
| `--surface` | `#1B2124` |
| `--ink` | `#EDEFEC` |
| `--ink-soft` | `#94A19B` |
| `--amber` | `#F0B15A` |
| `--amber-dim` | `#4A3A1E` |
| `--green` | `#54B37B` |
| `--green-dim` | `#1E3A2A` |
| `--brick` | `#E17262` |
| `--brick-dim` | `#4A241E` |
| `--line` | `#2B3236` |
| `--line-strong` | `#3D454A` |
| `--focus` | `#5AA6C9` |

Dark-mode text/bg overrides: chip text `#8FE0AE` (green), `#F5CE8C` (amber), `#F3A79A` (brick); chip-neutral bg `#262E31`; table row hover `#20272A`; ghost/kebab hover `#262E31`; flag-banner text `#F5CE8C` / border `#5A461F`; menu disabled `#4F585C`.

The chassis header (`.chassis`, `.brand`, nav links, `.who`) is **always dark** regardless of theme — only page content (paper/surface/ink) flips.

### Typography
- **Sans**: IBM Plex Sans, weights 400/500/600/700 (Google Fonts)
- **Mono**: IBM Plex Mono, weights 400/500/600 — used for data (call signs, FRNs, fees), eyebrows, nav-ish labels, chips
- Display: 28–32px/600, `letter-spacing:-.01em`
- Heading (h1 on pages): 22px/600
- Body: 13–14px/400
- Eyebrow: 11px mono, `letter-spacing:.14em`, uppercase, `--ink-soft`

### Spacing / shape
- Border radius: 5px (buttons/inputs), 6–10px (cards/menus), 99px (pills/chips/badges)
- Page padding: 28–56px horizontal, generous vertical
- Chassis header height: 56px

## Screens

### 1. Foundations (`tokens.html`)
Documentation page only — not a user-facing screen. Shows the color palette, type scale, and the "application status meter" component (a lit 3-segment meter representing Unmatched → Received → Granted, with Failed/NotTested as terminal single-segment states). Useful as the token reference for implementation but not something to build as an app screen.

### 2. Components (`components.html`)
Documentation page showing the shared UI vocabulary: buttons (primary/secondary/danger/ghost), status chips (green/amber/brick/neutral, dot + label), kebab dropdown menu, and form field (label + input). Reference for building a shared component library.

### 3. Session List (`session-list.html`)
**Purpose**: Session manager's home view — browse/filter exam sessions for their club.
**Layout**: Dark chassis header (56px) with brand mark, 3-item nav (Sessions/VE Roster/VEC Submission — Sessions active), and a "who" block (role badge + club name) on the right, containing the theme toggle icon button. Below: max-width 1160px centered main. Page head row: eyebrow + "Sessions" h1 on the left, pill filter group (Upcoming/Needs review/Past/All — Upcoming active) on the right. Then a full-width table.
**Table columns**: Session (title + mono subline), VEC, Candidates (count), Status (chip), VEC submission (chip/link), row actions (kebab).
**States**: filter pill active state (dark bg/white text), table row hover, view-link hover underline, flag icon (amber) for flagged sessions.

### 4. Session Detail (`session-detail.html`)
**Purpose**: Manage one exam session — candidate roster, status pipeline, VEC submission, VE roster.
**Layout**: Same chassis header. Breadcrumb (`← Sessions / exam-2026-0822-a`). Conditional flag banner (amber, shown when a reschedule needs review) with a "Clear flag" button. Session panel: title + meta grid (VEC, Zoom link, Discord link, fee, testing status, VEC submission chip) on the left, primary/secondary action buttons stacked on the right. Candidate table below (name/call sign/FRN, status meter, chip, refund tag, kebab row menu — menu has a disabled state for certain actions). Footer "roster" card: VE roster as removable pill chips.
**States**: kebab menu open/closed (per-row), menu item disabled/danger states, meter segment lit states per candidate status.

## Interactions & Behavior
- **Theme toggle**: icon-only button (moon shown in light mode / sun shown in dark mode) toggles `data-theme="dark"` on `<html>`, persisted to `localStorage` under key `vesm-theme`, applied on load. Located inline in the header `.who` block on app pages; fixed top-right on documentation pages.
- **Filter pills** (session list): single-select, click sets `.active` style; would drive a query/filter in a real app.
- **Kebab menu** (both list and detail rows): click toggles an absolutely-positioned dropdown menu anchored to the trigger; click-outside should close it (not wired in the static mock — implement in the framework's pattern, e.g. a popover/menu primitive).
- **Flag banner "Clear flag"**: dismisses the reschedule-review flag for that session.
- **VE roster chips**: each has an "×" affordance to remove that VE from the roster.
- No animations beyond standard hover transitions; no loading/error states designed — add per the target app's existing patterns.

## State Management
- Theme: boolean/string persisted to localStorage (see above) — reuse existing app theming solution if one exists.
- Session list: active filter selection; sessions data (title, date, VEC, candidate count, status, submission state, flagged bool).
- Session detail: session record (date/time, VEC, links, fee, testing status, submission status, flag state); candidate roster (name, call sign, FRN, application-status stage, chip state, refund flag); VE roster list; per-row menu open state.
- Candidate application status pipeline: `Unmatched → Received → Granted`, with `Failed` and `NotTested` as terminal off-path states (single lit segment, not a further pipeline step) — model this as an enum, not a numeric progress value.

## Assets
No external image assets — purely typographic/color design. Fonts loaded from Google Fonts (IBM Plex Sans, IBM Plex Mono).

## Files
- `tokens.html` — color/type/meter foundations reference
- `components.html` — shared component reference
- `session-list.html` — sessions list screen
- `session-detail.html` — session detail screen
