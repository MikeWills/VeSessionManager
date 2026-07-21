// Plain vanilla JS, no framework — matches CLAUDE.md's "minimal JS" stack preference. Handles the
// three interactive-but-static-in-the-mockup behaviors the design handoff calls out: theme toggle
// (persisted to localStorage, same key/behavior as the mockup), per-row kebab dropdown menus
// (click to open, click-outside to close — the mockup notes this wasn't wired in the static HTML),
// and the small inline "add VE" / "walk-in" modals.
(function () {
  "use strict";

  var THEME_KEY = "vesm-theme";
  var root = document.documentElement;

  function applyTheme(theme) {
    root.setAttribute("data-theme", theme);
    var button = document.getElementById("themeToggle");
    if (!button) return;
    button.innerHTML = theme === "dark"
      ? '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg"><circle cx="12" cy="12" r="4.2" fill="currentColor"/><g stroke="currentColor" stroke-width="1.8" stroke-linecap="round"><line x1="12" y1="2" x2="12" y2="4.5"/><line x1="12" y1="19.5" x2="12" y2="22"/><line x1="2" y1="12" x2="4.5" y2="12"/><line x1="19.5" y1="12" x2="22" y2="12"/><line x1="4.9" y1="4.9" x2="6.6" y2="6.6"/><line x1="17.4" y1="17.4" x2="19.1" y2="19.1"/><line x1="4.9" y1="19.1" x2="6.6" y2="17.4"/><line x1="17.4" y1="6.6" x2="19.1" y2="4.9"/></g></svg>'
      : '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg"><path d="M21 12.8A9 9 0 1111.2 3 7.2 7.2 0 0021 12.8z" fill="currentColor"/></svg>';
    var label = theme === "dark" ? "Switch to light mode" : "Switch to dark mode";
    button.setAttribute("aria-label", label);
    button.title = label;
  }

  applyTheme(localStorage.getItem(THEME_KEY) || "light");

  document.addEventListener("DOMContentLoaded", function () {
    var toggle = document.getElementById("themeToggle");
    if (toggle) {
      toggle.addEventListener("click", function () {
        var next = root.getAttribute("data-theme") === "dark" ? "light" : "dark";
        localStorage.setItem(THEME_KEY, next);
        applyTheme(next);
      });
    }

    // Kebab row-action menus: click the trigger toggles its own .menu, closes any other open one;
    // click outside any open menu closes it.
    document.addEventListener("click", function (event) {
      var kebab = event.target.closest(".kebab");
      if (kebab) {
        var menu = kebab.parentElement.querySelector(".menu");
        if (menu) {
          var wasOpen = menu.classList.contains("open");
          document.querySelectorAll(".menu.open").forEach(function (m) { m.classList.remove("open"); });
          if (!wasOpen) menu.classList.add("open");
        }
        event.stopPropagation();
        return;
      }

      if (!event.target.closest(".menu")) {
        document.querySelectorAll(".menu.open").forEach(function (m) { m.classList.remove("open"); });
      }

      var modalOpener = event.target.closest("[data-open-modal]");
      if (modalOpener) {
        var modal = document.getElementById(modalOpener.getAttribute("data-open-modal"));
        if (modal) modal.classList.add("open");
        event.stopPropagation();
      }

      var modalCloser = event.target.closest("[data-close-modal]");
      if (modalCloser) {
        var backdrop = modalCloser.closest(".modal-backdrop");
        if (backdrop) backdrop.classList.remove("open");
      }
    });
  });
})();
