# Chart.js 4.4.6

Vendored, not referenced from a CDN, because the app's CSP is `script-src 'self'` — a CDN reference
is blocked outright and fails silently as a chart that never renders. Same reason
`bootstrap-icons` lives here (see `docs/icons.md`).

Source: https://cdn.jsdelivr.net/npm/chart.js@4.4.6/dist/chart.umd.min.js
Licence: MIT.

Added 2026-08-15 for the stats page (#63), with permission — CLAUDE.md requires asking before taking
a new library. Canvas-based, so it needs no inline styles either, which the same CSP would also block.

To upgrade: replace the file, keep the version in this README accurate, and re-check the page renders
— there is no build step and nothing else pins the version.
