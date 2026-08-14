// Resolves the colour scheme and stamps it on <html> BEFORE the page paints.
//
// This is a separate file from app.js, and loaded synchronously in <head> rather than at the bottom
// of <body>, for one reason: app.js runs after the document has already been laid out, so resolving
// the theme there means a dark-mode user sees a white page flash on every single navigation. That
// was tolerable while dark mode was opt-in and rare; it is not once the OS setting decides it.
//
// The obvious alternative — a two-line inline <script> in the layout — is blocked outright: the CSP
// is `script-src 'self'` with no nonce, so an inline script silently never runs (see Program.cs).
// Hence a real file. Keep it small; it is a render-blocking request.
(function () {
  "use strict";

  var THEME_KEY = "vesm-theme";
  var root = document.documentElement;

  // Order matters, most authoritative first:
  //
  //  1. A data-theme the server already rendered. That is the signed-in user's saved
  //     User.ThemePreference, which is the whole point of storing it on the account — it must win
  //     over whatever this browser happens to remember, or signing in on a second device would show
  //     that device's old choice instead of yours.
  //  2. localStorage — this browser's own last choice. The only home a signed-out page has, and on a
  //     signed-in page it is how the *next* navigation paints correctly.
  //  3. The OS/browser setting.
  //  4. Light, for a browser too old to answer (3).
  var serverPreference = root.getAttribute("data-theme");
  if (serverPreference) {
    // Sync it back down so the login page, the privacy page and VE self-service — none of which
    // know who you are — still paint in your colour scheme.
    write(serverPreference);
    return;
  }

  var theme = read()
    || (window.matchMedia && window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light");

  root.setAttribute("data-theme", theme);

  // Storage access throws rather than returning null in a few real configurations (Safari private
  // browsing historically, and any browser with third-party/all cookies blocked for the site). An
  // uncaught throw *here* is worse than in app.js: this script blocks rendering, so it would take
  // the theme down with it and leave data-theme unset on every page.
  function read() {
    try { return localStorage.getItem(THEME_KEY); } catch (e) { return null; }
  }

  function write(value) {
    try { localStorage.setItem(THEME_KEY, value); } catch (e) { /* ignore — see read() */ }
  }
})();
