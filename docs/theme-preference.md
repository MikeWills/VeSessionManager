# Theme preference — the OS setting as the default, dark mode remembered on the account

Two changes to how the chassis theme toggle behaves, both asked for on 2026-08-13:

1. **A first visit follows the browser/OS setting** instead of always starting light.
2. **The choice is stored on the account**, so dark mode follows you from the desktop to the phone
   rather than being remembered separately by each browser.

## What was there before

One line of JavaScript, at the bottom of `wwwroot/js/app.js`:

```js
applyTheme(localStorage.getItem(THEME_KEY) || "light");
```

That is device-scoped (localStorage is per browser, per origin), OS-blind (`prefers-color-scheme`
was never consulted), and — because `app.js` loads at the end of `<body>` — resolved *after* the
page had already painted. The flash was tolerable while dark mode was opt-in and rare. It is not
once the OS setting decides it, because that turns a flash a handful of people saw into one most
dark-mode users see on every navigation.

## Resolution order

Most authoritative first. This lives in `wwwroot/js/theme.js` except for step 1, which the server
renders.

| # | Source | Where |
|---|---|---|
| 1 | `User.ThemePreference` | `data-theme` on `<html>`, rendered by `_AppLayout` |
| 2 | `localStorage["vesm-theme"]` | this browser's last choice |
| 3 | `prefers-color-scheme` | the OS/browser setting |
| 4 | light | a browser too old to answer (3) |

Step 1 has to win over step 2, and that is the whole point of storing it on the account: signing in
on a second device must show *your* choice, not that device's stale one. Step 2 still exists because
it is the only home a signed-out page has (login, the privacy page, VE self-service), and because it
lets the *next* navigation paint correctly before the server's answer is known.

### `ThemePreference.System` must render no attribute at all

The single most breakable thing here. `System` means "no explicit choice yet" — it is the default,
and **every account that predates this feature is in that state**. `_AppLayout` must emit no
`data-theme` for it, because `theme.js` treats a server-rendered attribute as authoritative and
stops there. Rendering `data-theme="light"` for `System` would look completely correct (light-mode
users see no difference at all) while silently pinning everyone else's account to light and undoing
change (1) entirely.

Two traps on the way to getting that right, both caught by tests rather than by reading:

- **Razor renders `data-theme="@nullString"` as `data-theme=""`, not as no attribute.** The empty
  string happens to be falsy in `theme.js`, so this worked — but it worked by luck, one `!== null`
  away from the failure above. The layout now builds the whole attribute or nothing
  (`Html.Raw(" data-theme=\"dark\"")`), which is safe by construction: the only two values that
  reach it are those literals.
- The toggle only ever writes `Light` or `Dark`, and the handler **rejects anything else**,
  including `system`. There is deliberately no way back to `System` from the UI: a three-state
  control whose current state is invisible ("is this dark because I picked dark, or because it's
  9pm?") costs more than it buys. Clearing the column by hand is the escape hatch.

## Why a separate `theme.js`, loaded render-blocking in `<head>`

The conventional fix for the flash is a two-line inline `<script>` in the layout. **That is not
available here**: the CSP is `script-src 'self'` with no nonce, so an inline script renders fine,
reads correctly in the markup, and never runs — the same trap already recorded for inline event
handlers in `Program.cs` and `app.js`. So it has to be a real file, which means a real (cached,
same-origin) request; keep it small.

It is separate from `app.js` rather than moving `app.js` into `<head>` because `app.js` is ~500
lines of behaviour that has no reason to block rendering. `theme.js` must not be `defer` or `async`
either — both postpone execution until after the document is parsed, which is exactly the flash
being prevented. `ThemePreferenceTests.TheThemeScriptRunsBeforeTheBodyPaints` pins all of that.

## Saving: the app's first and only `fetch()`

`POST /Account/Theme` (`Pages/Account/Theme.cshtml.cs`). Everything else in this app is a real form
submit, which remains the right default — a theme toggle is the one control where a full page round
trip to change a colour would be worse than the thing it fixes.

- `OnGet` returns **404**. There is no page; a handler-only route should not pretend otherwise, and
  `PageSmokeTests` tolerates a 404 for exactly this case.
- The antiforgery token rides in a `RequestVerificationToken` **header**, because a `fetch()` has no
  form to carry the hidden field. This needs no configuration — it is already
  `AntiforgeryOptions.HeaderName`'s default. An `AddAntiforgery(o => o.HeaderName = "…")` line was
  written in `Program.cs` first and **removed when a mutation test showed it changed nothing**; a
  no-op registration carrying a comment about why it is essential is worse than no line at all. The
  comment that replaced it says so, so nobody re-adds it.
- Authenticated by the app-wide `FallbackPolicy`, not by an `[Authorize]` attribute. There is
  nothing a signed-out visitor could usefully do here, and `_PublicLayout` renders neither the URL
  nor a token, so `app.js` skips the call entirely and localStorage is the whole story.
- Writes via `ExecuteUpdateAsync`. It is one scalar stamp on the account making the request — no
  ownership question to re-check, and no reason to run Identity's validators (or touch the security
  stamp) over a colour scheme.
- Fire-and-forget on the client. The theme is already applied locally; a failed save is not worth
  interrupting anyone to report.

## Testing

`tests/VeSessionManager.Web.Tests/ThemePreferenceTests.cs`. `theme.js` itself runs in a browser and
no test here executes it — what is pinned is the half the server owns, which is where both silent
failure modes live (the `System` attribute above, and the script's position in the document).

Two things the tests caught that review had not:

- The `data-theme=""` rendering described above.
- **`MapStaticAssets` makes `asp-append-version` emit a fingerprinted *filename*** —
  `/js/theme.qjcbqpniws.js` — not a `?v=` query. The first version of the ordering test searched for
  the literal `/js/theme.js`, found nothing, and reported the script as missing when it was right
  there in the head. Worth knowing for any future test that asserts on an asset URL.

And one mutation that did *not* discriminate, which is why the `AddAntiforgery` line is gone:
removing it left all eleven tests green.

## Not done

- **No browser verification.** Every affected page is `[Authorize]`d and Claude does not log in (see
  CLAUDE.md's standing note), and the two behaviours that most want a real browser — the OS default
  on a fresh profile, and the absence of the light flash — are both client-side. Worth a look at
  `prefers-color-scheme` on a phone and one hard refresh in dark mode.
- **No "follow my OS setting" option once a choice is made**, per the reasoning above.
- **The OS setting is read once, at page load.** Changing the system theme while a tab is open does
  not restyle it until the next navigation; a `matchMedia` change listener would fix that if it ever
  matters.
