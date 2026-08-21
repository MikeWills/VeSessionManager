// Plain vanilla JS, no framework — matches CLAUDE.md's "minimal JS" stack preference. Handles the
// three interactive-but-static-in-the-mockup behaviors the design handoff calls out: theme toggle
// (persisted to localStorage, same key/behavior as the mockup), per-row kebab dropdown menus
// (click to open, click-outside to close — the mockup notes this wasn't wired in the static HTML),
// and the small inline modals. Plus click-to-sort table headers (see the
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

  // theme.js — loaded in <head>, before first paint — owns *resolving* which theme applies (server
  // preference, then localStorage, then the OS setting). By the time this file runs the answer is
  // already on <html>; all that is left is to paint the toggle button, which did not exist yet at
  // head time. Do not re-resolve here: reading localStorage again would quietly override the saved
  // account preference the server just rendered.
  applyTheme(root.getAttribute("data-theme") || "light");

  // Persist the choice to the signed-in user's account, so it follows them to their phone rather
  // than living only in this browser. The layout renders the URL and an antiforgery token onto the
  // button; both are absent on _PublicLayout, where there is nobody to save it for and localStorage
  // is the whole story. Fire-and-forget on purpose — the theme has already been applied locally, and
  // a failed save is not worth interrupting someone to report.
  function saveThemePreference(button, theme) {
    var url = button.getAttribute("data-save-url");
    var token = button.getAttribute("data-antiforgery-token");
    if (!url || !token) return;

    fetch(url, {
      method: "POST",
      // A header rather than the hidden form field a <form> would post, since there is no form
      // here. "RequestVerificationToken" is AntiforgeryOptions.HeaderName's default, so nothing in
      // Program.cs configures it — see Pages/Account/Theme.cshtml.cs.
      headers: {
        "RequestVerificationToken": token,
        "Content-Type": "application/x-www-form-urlencoded"
      },
      body: "theme=" + encodeURIComponent(theme),
      // The endpoint is authenticated, so the auth cookie has to ride along.
      credentials: "same-origin"
    }).catch(function () { /* see above — local state is already correct */ });
  }

  document.addEventListener("DOMContentLoaded", function () {
    var toggle = document.getElementById("themeToggle");
    if (toggle) {
      toggle.addEventListener("click", function () {
        var next = root.getAttribute("data-theme") === "dark" ? "light" : "dark";
        try { localStorage.setItem(THEME_KEY, next); } catch (e) { /* see theme.js read() */ }
        applyTheme(next);
        saveThemePreference(toggle, next);
      });
    }

    // Copy-to-clipboard for a field the user is meant to paste somewhere else — the authenticator
    // setup URI, which a password manager (Bitwarden, 1Password) takes whole.
    //
    // A data attribute rather than an inline onclick, because the CSP is `script-src 'self'` and that
    // silently drops inline handlers — a button that looks right and does nothing. Same convention as
    // data-autosubmit above.
    //
    // navigator.clipboard needs a secure context, so it is absent over plain http (a local dev run).
    // The fallback selects the text instead, which still gets the user to Ctrl+C rather than leaving
    // the button dead.
    document.querySelectorAll("[data-copy-target]").forEach(function (button) {
      button.addEventListener("click", function () {
        var target = document.getElementById(button.getAttribute("data-copy-target"));
        if (!target) return;

        var done = function () {
          var original = button.getAttribute("data-copy-label") || button.textContent;
          button.setAttribute("data-copy-label", original);
          button.textContent = "Copied";
          setTimeout(function () { button.textContent = original; }, 1500);
        };

        if (navigator.clipboard && window.isSecureContext) {
          navigator.clipboard.writeText(target.value).then(done, function () { target.select(); });
        } else {
          target.select();
          target.setSelectionRange(0, target.value.length);
        }
      });
    });

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
      closeAllMenus();
      setNavOpen(false);
    });

    // Filter controls that re-run their form the moment they change.
    //
    // A data attribute rather than an inline `onchange=`, because the CSP is `script-src 'self'`
    // and that silently drops inline handlers — the obvious spelling produces a control that looks
    // right, reads right in the markup, and does nothing at all when clicked. Two shipped that way
    // (VE Directory's "Show retired", the session list's page-size picker) and neither failed
    // loudly; only the browser console said so. InlineEventHandlerTests now guards it.
    //
    // Delegated from the document so any page can opt in with one attribute. `control.form` is used
    // ahead of a closest() walk so a control bound to a form by id — the page-size picker sits
    // outside its own <form> — still finds it.
    document.addEventListener("change", function (event) {
      var control = event.target.closest("[data-autosubmit]");
      if (!control) return;

      var form = control.form || control.closest("form");
      if (!form) return;

      // requestSubmit() over submit(): it fires validation and the submit event, where submit()
      // bypasses both.
      if (form.requestSubmit) form.requestSubmit();
      else form.submit();
    });

    // Kebab row-action menus: click the trigger toggles its own .menu, closes any other open one;
    // click outside any open menu closes it.
    document.addEventListener("click", function (event) {
      var kebab = event.target.closest(".kebab");
      if (kebab) {
        var menu = kebab.parentElement.querySelector(".menu");
        if (menu) {
          var wasOpen = menu.classList.contains("open");
          closeAllMenus();
          if (!wasOpen) {
            menu.classList.add("open");
            liftMenuOutOfScrollContainer(kebab, menu);
          }
        }
        event.stopPropagation();
        return;
      }

      if (!event.target.closest(".menu")) {
        closeAllMenus();
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

    // A menu positioned against the viewport does not travel with the row it belongs to, so any
    // scroll closes it rather than leaving it stranded mid-page. Capture phase, because the scroll
    // that matters most is the table wrapper's own and that does not bubble.
    //
    // ONLY the lifted ones. This originally closed every open menu, which made the Settings menu
    // unusable on a phone: the chassis nav's dropdowns are position:static inside the collapsed
    // panel, so they scroll with the page perfectly well, and closing them on the smallest scroll
    // meant the menu shut before anyone could tap an item (reported 2026-08-06).
    window.addEventListener("scroll", closeLiftedMenus, true);

    // Resize only matters for the same lifted menus, whose coordinates go stale — and only when the
    // WIDTH changes. On mobile, scrolling hides and shows the browser's URL bar, which fires resize
    // with a changed height: the second reason the Settings menu kept collapsing, and the one that
    // would have survived fixing the scroll handler alone.
    var lastWidth = window.innerWidth;
    window.addEventListener("resize", function () {
      if (window.innerWidth === lastWidth) return;
      lastWidth = window.innerWidth;
      closeLiftedMenus();
    });

    document.querySelectorAll("table.cards").forEach(labelCardTable);
    document.querySelectorAll("table[data-sortable]").forEach(initSortableTable);
    initInvitePicker();
  });

  // ---- Row menus inside a scrolling table ------------------------------------------------------
  // `.table-scroll` exists to let a wide table scroll sideways, but `overflow-x: auto` makes the
  // wrapper a scroll container in BOTH axes — CSS refuses to pair `overflow-x: auto` with
  // `overflow-y: visible` and silently promotes the other axis too. An absolutely-positioned row
  // menu is therefore clipped by the wrapper instead of floating above the page, which is what it
  // did on 2026-08-05: the kebab menu opened as a sliver cut off at the wrapper's edge, and the
  // wrapper grew a stray vertical scrollbar.
  //
  // The fix is to take the open menu out of the wrapper's coordinate space entirely — `position:
  // fixed` is relative to the viewport, so no ancestor's overflow can clip it. Coordinates are
  // computed from the trigger, and cleared again on close so the CSS rules apply normally when the
  // menu is not inside a scroll container.
  //
  // Only menus inside `.table-scroll` are touched. The chassis nav's dropdowns and the mobile
  // accordion are unaffected.
  function liftMenuOutOfScrollContainer(kebab, menu) {
    if (!menu.closest(".table-scroll")) return;

    var trigger = kebab.getBoundingClientRect();

    // Fixed first, so the measurements below are of the menu at its final width rather than one
    // constrained by the narrow cell it nominally lives in.
    menu.style.position = "fixed";
    menu.style.left = "0px";
    menu.style.top = "0px";

    var width = menu.offsetWidth;
    var height = menu.offsetHeight;
    var margin = 8;

    // Right-aligned to the trigger, matching where the menu sits when CSS positions it, then
    // clamped so it can never hang off either edge on a narrow screen.
    var left = Math.min(trigger.right - width, window.innerWidth - width - margin);
    menu.style.left = Math.max(margin, left) + "px";

    // Below the trigger, or flipped above it when there isn't room — a menu opened on the last row
    // of a long table would otherwise run off the bottom of the window.
    var top = trigger.bottom + 4;
    if (top + height > window.innerHeight - margin) {
      top = Math.max(margin, trigger.top - height - 4);
    }
    menu.style.top = top + "px";
  }

  function closeAllMenus() {
    document.querySelectorAll(".menu.open").forEach(function (menu) {
      menu.classList.remove("open");
      // Always cleared, not just for lifted menus: leaving inline coordinates behind would override
      // the CSS the next time this same menu opens outside a scroll container.
      menu.style.position = "";
      menu.style.left = "";
      menu.style.top = "";
    });
  }

  /// Only the menus that were taken out of the document flow by liftMenuOutOfScrollContainer — the
  /// inline `position: fixed` is exactly the marker for "this one no longer moves with its row".
  /// Everything else (the chassis nav's accordions, any menu outside a .table-scroll) scrolls with
  /// the page and must be left alone.
  function closeLiftedMenus() {
    document.querySelectorAll(".menu.open").forEach(function (menu) {
      if (menu.style.position !== "fixed") return;
      menu.classList.remove("open");
      menu.style.position = "";
      menu.style.left = "";
      menu.style.top = "";
    });
  }

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
  // ---- VE invitation picker -------------------------------------------------------------------
  // A team can have 150+ VEs, which made the compose box and the Send button sit 9,000 pixels down
  // the page — the button was reported as missing, which it effectively was. The message now comes
  // first and the actions live in a sticky bar; this adds the three things that make a list that
  // long usable: a filter, select-all/none over whatever is currently visible, and a live count so
  // the sticky bar says what Send will actually do.
  //
  // Opt-in via [data-ve-picker], same convention as data-sortable. No inline script anywhere: the
  // CSP is script-src 'self'.
  function initInvitePicker() {
    var picker = document.querySelector("[data-ve-picker]");
    if (!picker) return;

    var boxes = Array.prototype.slice.call(picker.querySelectorAll("input[type=checkbox][name=SelectedVeIds]"));
    var counter = document.querySelector("[data-ve-selected-count]");
    var filter = picker.querySelector("[data-ve-filter]");

    function rowOf(box) { return box.closest("tr"); }
    function visible(box) { return rowOf(box) && rowOf(box).style.display !== "none"; }

    function updateCount() {
      if (!counter) return;
      var n = boxes.filter(function (b) { return b.checked; }).length;
      counter.textContent = n === 1 ? "1 VE selected" : n + " VEs selected";
    }

    boxes.forEach(function (b) { b.addEventListener("change", updateCount); });

    picker.addEventListener("click", function (event) {
      var action = event.target.getAttribute && event.target.getAttribute("data-ve-select");
      if (!action) return;
      event.preventDefault();
      boxes.forEach(function (b) {
        // Never tick a disabled box — those are VEs with no email address, who cannot be sent to.
        // And only touch what the filter is currently showing, or "select all" would quietly pick
        // people the user cannot see.
        if (!b.disabled && visible(b)) b.checked = action === "all";
      });
      updateCount();
    });

    // Text and tag filters are ANDed through one apply(), not two independent handlers: with a
    // handler each, whichever ran last would undo the other's hiding, and "Team Member" + "granger"
    // would show everyone named Granger.
    // One control on the invitation screen (a <select>), several on Email VEs (a checkbox per tag,
    // #394 follow-up) — so this reads "whatever is selected" rather than "the value of the filter".
    var tagFilters = picker.querySelectorAll("[data-ve-tag-filter]");
    // Sentinel for the "Untagged" option. Must stay byte-identical to
    // VeTagFilter.UntaggedValue -- two copies of one constant, nothing tying them together.
    //
    // The leading character is a SPACE and must stay one. It was a literal U+0000 until 2026-08-11
    // (issue #300), which broke this filter outright: an HTML parser rewrites U+0000 to U+FFFD, so
    // tagFilter.value never equalled this literal, and the ternary below fell through to
    // rowTags.indexOf(tag) -- searching for a tag nothing has, hiding every VE. A space is safe
    // because tag names are Trim()ed and rejected when blank, so no stored tag can start with one.
    var UNTAGGED = " untagged";

    // Which tags are being filtered on. A <select> contributes its value; checkboxes contribute the
    // ones ticked. Nothing selected means no tag filter at all, which is why an empty array and
    // "show everything" are the same answer below.
    function selectedTags() {
      var chosen = [];
      tagFilters.forEach(function (control) {
        if (control.tagName === "SELECT") {
          if (control.value) chosen.push(control.value);
        } else if (control.checked) {
          chosen.push(control.value);
        }
      });
      return chosen;
    }

    function apply() {
      var term = filter ? filter.value.trim().toLowerCase() : "";
      var tags = selectedTags();

      boxes.forEach(function (b) {
        var row = rowOf(b);
        if (!row) return;

        var matchesText = !term || row.textContent.toLowerCase().indexOf(term) !== -1;

        var rowTags = (row.getAttribute("data-ve-tags") || "").split("|").filter(Boolean);
        // ANY of the chosen tags, not all of them. Picking "Liaison" and "Mentor" means "show me
        // both groups" — the reason for choosing two is to widen the list, not narrow it to the
        // handful of people who happen to hold both.
        var matchesTag = tags.length === 0 || tags.some(function (tag) {
          return tag === UNTAGGED ? rowTags.length === 0 : rowTags.indexOf(tag) !== -1;
        });

        row.style.display = matchesText && matchesTag ? "" : "none";
      });

      updateTagLabel(tags.length);
    }

    // The chosen tags live inside a closed dropdown on Email VEs, so the trigger has to say how many
    // there are or the filter is invisible once the menu shuts.
    var tagLabel = picker.querySelector("[data-ve-tag-label]");
    function updateTagLabel(count) {
      if (!tagLabel) return;
      tagLabel.textContent = count === 0 ? "Any tag" : count + " selected";
    }

    if (filter) filter.addEventListener("input", apply);
    // Delegated: the checkboxes are inside a dropdown that is built server-side, and binding each
    // one individually would miss any added later.
    picker.addEventListener("change", function (event) {
      if (event.target.closest("[data-ve-tag-filter]")) apply();
    });

    updateCount();
  }


  // A checkbox that shows or hides a panel by id (#64: the per-integration switches sit behind the
  // master toggle, so an ordinary team's settings page does not grow controls nobody touches).
  //
  // Progressive enhancement only. The panel is rendered expanded or collapsed server-side from the
  // saved state, so with JavaScript unavailable the controls are simply always visible and still
  // submit correctly — hiding is never the enforcement, which is read from the master switch on the
  // server every time.
  document.querySelectorAll("[data-toggle-panel]").forEach(function (toggle) {
    var panel = document.getElementById(toggle.getAttribute("data-toggle-panel"));
    if (!panel) return;

    toggle.addEventListener("change", function () {
      panel.style.display = toggle.checked ? "" : "none";
    });
  });


  // ---- Alert highlight -------------------------------------------------------------------------
  // The row an alert-bell link navigated to (#339). The server renders the marker itself, so with
  // JavaScript unavailable the row is still visibly picked out — all this adds is bringing it into
  // view, which is what makes the difference on a list hundreds of rows long.
  //
  // scrollIntoView rather than an `#id` fragment on the link: a fragment jump puts the row at the
  // very top of the viewport, under the chassis header, and it would also fire before the sortable
  // tables above have reordered the rows it is scrolling to.
  var highlightedRow = document.querySelector(".row-highlight");
  if (highlightedRow && typeof highlightedRow.scrollIntoView === "function") {
    highlightedRow.scrollIntoView({ block: "center", behavior: "smooth" });
  }


  // ---- Message rule channel fields (#401 PR4) ---------------------------------------------------
  // A rule sends an email or posts to Discord, and the two need different questions answered: a
  // recipient, or a channel id and how many posts. Both sets are in the markup and this hides the
  // one that does not apply.
  //
  // Hidden rather than removed, and the server validates either way — with JavaScript unavailable
  // every field is simply visible, which is a slightly cluttered form rather than a broken one.
  // Radios are grouped per trigger point because each modal on the page is its own form.
  document.addEventListener("change", function (event) {
    var radio = event.target.closest("[data-channel-radio]");
    if (!radio) return;

    var group = radio.getAttribute("data-channel-radio");
    var selected = radio.value === "1" ? "Discord" : "Email";
    var fields = document.querySelectorAll('[data-channel-group="' + group + '"]');
    Array.prototype.forEach.call(fields, function (field) {
      field.hidden = field.getAttribute("data-channel-only") !== selected;
    });
  });

  // ---- Insert a tag into a message ---------------------------------------------------------
  //
  // The tags a message can use depend on its trigger, and retyping {{CandidateFirstName}} by hand is
  // how you get a token that renders blank with nothing to show it went wrong. Clicking inserts it.
  //
  // A listener here rather than onclick= in the markup: the CSP is script-src 'self', so an inline
  // handler is silently dropped by the browser — the control renders, nothing happens, and only the
  // console says so. Two shipped that way before anyone noticed.

  var lastMessageField = null;
  document.querySelectorAll("[data-token-target]").forEach(function (field) {
    field.addEventListener("focus", function () { lastMessageField = field; });
  });

  document.querySelectorAll("[data-insert-token]").forEach(function (chip) {
    chip.addEventListener("click", function () {
      // Whichever box they were last in, so a tag can go in the subject as easily as the body.
      // Falls back to the message, which is where all but a couple of tags belong.
      var field = lastMessageField || document.querySelector("[data-token-target='body']");
      if (!field) { return; }

      var token = chip.getAttribute("data-insert-token");
      var start = typeof field.selectionStart === "number" ? field.selectionStart : field.value.length;
      var end = typeof field.selectionEnd === "number" ? field.selectionEnd : field.value.length;

      field.value = field.value.slice(0, start) + token + field.value.slice(end);

      // Caret after what was inserted, so several tags in a row land where you expect rather than
      // stacking at the start.
      var caret = start + token.length;
      field.focus();
      if (typeof field.setSelectionRange === "function") { field.setSelectionRange(caret, caret); }
      lastMessageField = field;
    });
  });

})();
