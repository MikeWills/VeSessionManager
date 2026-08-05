// Plain vanilla JS, no framework — matches CLAUDE.md's "minimal JS" stack preference. Handles the
// three interactive-but-static-in-the-mockup behaviors the design handoff calls out: theme toggle
// (persisted to localStorage, same key/behavior as the mockup), per-row kebab dropdown menus
// (click to open, click-outside to close — the mockup notes this wasn't wired in the static HTML),
// and the small inline "add VE" / "walk-in" modals. Plus click-to-sort table headers (see the
// "Sortable tables" section at the bottom).
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

    // Mobile nav toggle. Below app.css's 768px breakpoint the chassis nav links and the .who
    // cluster are one collapsed panel that `header.chassis.nav-open` reveals; from 768px up the
    // button is display:none and this class is inert, so no width check is needed here.
    var navToggle = document.getElementById("navToggle");
    var chassis = navToggle && navToggle.closest("header.chassis");

    function setNavOpen(open) {
      if (!chassis) return;
      chassis.classList.toggle("nav-open", open);
      navToggle.setAttribute("aria-expanded", open ? "true" : "false");
    }

    if (navToggle && chassis) {
      navToggle.addEventListener("click", function (event) {
        // Without this the document handler below treats the click as "outside a menu" and the
        // panel would close again in the same tick it opened.
        event.stopPropagation();
        setNavOpen(!chassis.classList.contains("nav-open"));
      });
    }

    document.addEventListener("keydown", function (event) {
      if (event.key !== "Escape") return;
      document.querySelectorAll(".menu.open").forEach(function (m) { m.classList.remove("open"); });
      setNavOpen(false);
    });

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

      // Tapping the page body closes the mobile nav panel. Anything inside the header is excluded,
      // so opening one of the panel's own accordion menus doesn't collapse the panel under it.
      if (!event.target.closest("header.chassis")) setNavOpen(false);

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

    document.querySelectorAll("table.cards").forEach(labelCardTable);
    document.querySelectorAll("table[data-sortable]").forEach(initSortableTable);
  });

  // ---- Card tables -------------------------------------------------------------------------
  // Below app.css's 768px breakpoint a <table class="cards"> restacks each row into a labelled
  // card, where the label is rendered from `content: attr(data-label)`. Rather than hand-writing
  // data-label on every <td> across every page — hundreds of attributes to add, and to keep in step
  // with the headers forever after — each cell is stamped here from its own column's <th>.
  //
  // That makes promoting another table to cards a one-word markup change (add the class), which is
  // the point: the admin tables are on horizontal scroll for now and are expected to follow later.
  //
  // Two cases get marked instead of labelled:
  //   .is-unlabelled — the column has no header text at all (the View / ⋮ action columns), so the
  //                    cell spans the full card width rather than leaving an empty label gutter.
  //   .is-blank      — the cell rendered nothing. A desktop table still needs the empty cell to
  //                    keep its grid aligned; a card row reading "FRN —" with no value is noise.
  function labelCardTable(table) {
    var head = table.tHead;
    var body = table.tBodies[0];
    if (!head || !head.rows.length || !body) return;

    var headerRow = head.rows[head.rows.length - 1];
    var labels = Array.prototype.map.call(headerRow.cells, function (th) {
      return (th.getAttribute("data-card-label") || th.textContent).trim();
    });

    Array.prototype.forEach.call(body.rows, function (row) {
      Array.prototype.forEach.call(row.cells, function (cell) {
        // The "nothing matches this filter" row spans the table and is styled on its own.
        if (cell.hasAttribute("colspan")) return;

        var label = labels[cell.cellIndex] || "";
        if (label) cell.setAttribute("data-label", label);
        else cell.classList.add("is-unlabelled");

        // textContent alone would call a cell holding only an icon button or a link empty.
        if (!cell.textContent.trim() && !cell.querySelector("a, button, input, select, svg, img")) {
          cell.classList.add("is-blank");
        }
      });
    });
  }

  // ---- Sortable tables ---------------------------------------------------------------------
  // Opt in with <table data-sortable="unique-key-on-this-page">. Clicking a header cycles
  // ascending → descending → back to the order the server rendered; clicking a different header
  // replaces the current sort rather than adding a second key to it. The choice is remembered per
  // page + table in localStorage, so navigating away and back restores it.
  //
  // Sorting is over the rows already in the DOM, so a table that pages server-side must NOT use
  // this — reordering only the rows currently on screen would misrepresent the whole result set.
  // The Sessions list is exactly that case and renders real server-side sort links instead
  // (see IndexModel.BuildSortUrl); everything else here renders its full set in one page.
  //
  // A cell sorts on its data-sort-value attribute when present, otherwise its visible text. Use the
  // attribute for anything whose display form doesn't sort correctly as text — dates above all
  // ("Apr 2" vs "Mar 30"), where the value should be a round-trip ("o") timestamp.

  var SORT_STORE_PREFIX = "vesm-sort:";
  // Every "no value here" spelling used across these tables — always sorted last, in both
  // directions, so the rows that actually have data stay together at the top.
  var BLANKS = ["", "—", "-", "–"];
  var NUMERIC = /^[$]?-?[\d,]+(\.\d+)?%?$/;

  function sortStorageKey(table) {
    return SORT_STORE_PREFIX + window.location.pathname + "#" + table.getAttribute("data-sortable");
  }

  // localStorage throws in some privacy modes and when the quota is full. Sorting itself doesn't
  // depend on it, so a failure just means this table's sort won't be remembered.
  function readStoredSort(table) {
    try {
      var raw = localStorage.getItem(sortStorageKey(table));
      return raw ? JSON.parse(raw) : null;
    } catch (e) {
      return null;
    }
  }

  function writeStoredSort(table, key, direction) {
    try {
      if (direction) localStorage.setItem(sortStorageKey(table), JSON.stringify({ key: key, dir: direction }));
      else localStorage.removeItem(sortStorageKey(table));
    } catch (e) {
      /* not remembered — see above */
    }
  }

  // Headers are identified by their own label, not their column index: several tables show a Team
  // column only when the user can see more than one team, which shifts every index to its right.
  function headerKey(th) {
    return (th.getAttribute("data-sort-key") || th.textContent).trim().toLowerCase();
  }

  // The "No sessions match this filter." style empty-state row — one cell spanning the table. It
  // isn't data, so it's held out of the sort entirely and re-appended at the top afterwards.
  function isPlaceholderRow(row) {
    return row.cells.length === 1 && row.cells[0].hasAttribute("colspan");
  }

  function cellText(row, index) {
    var cell = row.cells[index];
    if (!cell) return "";
    var explicit = cell.getAttribute("data-sort-value");
    return (explicit !== null ? explicit : cell.textContent).trim();
  }

  function asNumber(text) {
    if (!NUMERIC.test(text)) return null;
    var parsed = parseFloat(text.replace(/[$,%]/g, ""));
    return isNaN(parsed) ? null : parsed;
  }

  function compareText(a, b) {
    var numberA = asNumber(a);
    var numberB = asNumber(b);
    if (numberA !== null && numberB !== null) return numberA - numberB;
    return a.localeCompare(b, undefined, { numeric: true, sensitivity: "base" });
  }

  function applySort(table, index, direction) {
    var body = table.tBodies[0];
    var rows = Array.prototype.slice.call(body.rows);
    var placeholders = rows.filter(isPlaceholderRow);
    var data = rows.filter(function (row) { return !isPlaceholderRow(row); });

    if (direction) {
      var sign = direction === "asc" ? 1 : -1;
      data.sort(function (rowA, rowB) {
        var a = cellText(rowA, index);
        var b = cellText(rowB, index);
        var blankA = BLANKS.indexOf(a) !== -1;
        var blankB = BLANKS.indexOf(b) !== -1;
        if (blankA || blankB) return blankA && blankB ? 0 : blankA ? 1 : -1;
        return sign * compareText(a, b);
      });
    } else {
      data.sort(function (rowA, rowB) {
        return rowA.sortOriginalIndex - rowB.sortOriginalIndex;
      });
    }

    var fragment = document.createDocumentFragment();
    placeholders.concat(data).forEach(function (row) { fragment.appendChild(row); });
    body.appendChild(fragment);
  }

  function initSortableTable(table) {
    var head = table.tHead;
    var body = table.tBodies[0];
    if (!head || !head.rows.length || !body) return;

    // The order the server rendered, so a third click can put the table back the way it arrived.
    Array.prototype.forEach.call(body.rows, function (row, i) { row.sortOriginalIndex = i; });

    var headerRow = head.rows[head.rows.length - 1];
    var headers = Array.prototype.filter.call(headerRow.cells, function (th) {
      // Action/kebab columns are headerless and have nothing to sort on; data-sort="none" opts a
      // labelled column out explicitly.
      return th.getAttribute("data-sort") !== "none" && th.textContent.trim() !== "";
    });

    var stored = readStoredSort(table);
    var active = null;

    headers.forEach(function (th) {
      var key = headerKey(th);
      th.setAttribute("data-sort-key", key);
      th.classList.add("sortable");
      th.setAttribute("tabindex", "0");
      th.setAttribute("role", "button");
      th.setAttribute("aria-sort", "none");
      var arrow = document.createElement("span");
      arrow.className = "sort-arrow";
      arrow.setAttribute("aria-hidden", "true");
      th.appendChild(arrow);

      function cycle() {
        var next = active !== th ? "asc" : th.getAttribute("aria-sort") === "ascending" ? "desc" : th.getAttribute("aria-sort") === "descending" ? null : "asc";
        select(th, next, true);
      }

      th.addEventListener("click", cycle);
      th.addEventListener("keydown", function (event) {
        if (event.key !== "Enter" && event.key !== " ") return;
        event.preventDefault();
        cycle();
      });
    });

    function select(th, direction, remember) {
      headers.forEach(function (other) { other.setAttribute("aria-sort", "none"); });
      th.setAttribute("aria-sort", direction === "asc" ? "ascending" : direction === "desc" ? "descending" : "none");
      active = direction ? th : null;
      applySort(table, th.cellIndex, direction);
      if (remember) writeStoredSort(table, th.getAttribute("data-sort-key"), direction);
    }

    if (stored && stored.dir) {
      var restore = headers.filter(function (th) { return th.getAttribute("data-sort-key") === stored.key; })[0];
      // A remembered column can legitimately be gone — e.g. the Team column disappears once the
      // user narrows to a single team. Drop the stored sort rather than guessing at a substitute.
      if (restore) select(restore, stored.dir, false);
      else writeStoredSort(table, null, null);
    }
  }
})();
