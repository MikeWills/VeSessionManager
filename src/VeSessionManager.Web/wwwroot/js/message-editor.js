// Rich-text editing for the message editor (Admin -> Messages -> Edit/New). Loaded only by those two
// pages — Quill is ~210KB and has no business on every screen. See docs/email-template-editor.md.
//
// Was email-template-editor.js, on the Email Templates page, until 2026-08-21: a message owns its
// words now, so this is where bodies are authored. Nothing about the mechanism changed.
//
// The <textarea name="body"> stays the field the form actually posts, in both modes. Quill is a
// second view onto it, never a replacement: switching to the HTML tab writes the editor's HTML into
// the textarea, and submitting from the Visual tab does the same first. That keeps the server
// contract identical to before this editor existed, and means the source view is authoritative
// rather than a read-only preview.
(function () {
  "use strict";

  if (typeof Quill === "undefined") return;

  // Emit alignment as an inline style, not Quill's default `class="ql-align-center"`. Email has no
  // stylesheet to resolve a class against, so the default would silently do nothing in the
  // recipient's inbox while looking correct in the editor.
  Quill.register(Quill.import("attributors/style/align"), true);

  var TOOLBAR = [
    [{ header: [false, 1, 2, 3] }],
    ["bold", "italic"],
    [{ align: [] }],
    [{ list: "bullet" }, { list: "ordered" }],
    ["link"],
    ["clean"]
  ];

  // Quill 2's getSemanticHTML() is the right source — root.innerHTML renders bullets as
  // `<ol><li data-list="bullet">`, which an email client shows as a NUMBERED list, and leaves
  // `ql-ui` spans behind. But getSemanticHTML converts every space to &nbsp;, and a paragraph of
  // non-breaking spaces will not wrap — which is exactly wrong on a phone. Both quirks were
  // measured against a real seeded template before this was written.
  //
  // A blanket &nbsp; swap is safe here because Quill is the only thing producing them; nobody is
  // authoring a deliberate non-breaking space through this toolbar.
  function toEmailHtml(quill) {
    return quill.getSemanticHTML()
      .replace(/&nbsp;/g, " ")
      .replace(/&#39;/g, "'")
      .trim();
  }

  function initEditor(host) {
    var textarea = document.getElementById(host.getAttribute("data-editor-for"));
    if (!textarea) return;

    var wrapper = host.closest(".message-editor");
    // The placeholder chips sit in .roster-list, a sibling of the <form> — NOT inside
    // .message-editor. Scoping the chip lookup to the wrapper found none and silently bound
    // nothing, so clicking a chip did nothing at all. The per-template .session-panel is the
    // nearest element that contains both, which also keeps each card's chips bound to its own
    // editor when several templates are on the page.
    var card = host.closest(".session-panel") || wrapper;

    var quill = new Quill(host.querySelector(".editor-surface"), {
      theme: "snow",
      modules: { toolbar: TOOLBAR }
    });

    // Clicking a chip blurs the editor, so the caret position has to be remembered rather than
    // read back after the click.
    var lastRange = null;
    quill.on("selection-change", function (range) { if (range) lastRange = range; });

    // dangerouslyPasteHTML, despite the name, is the *safer* load path: Quill parses the stored
    // markup into its own document model and discards anything it has no format for. An
    // `<img onerror=...>` hand-typed into the HTML tab by one admin therefore cannot fire in
    // another admin's browser when the template is next opened. Nothing here ever assigns the
    // stored body to innerHTML directly, and there is deliberately no rendered preview pane.
    quill.clipboard.dangerouslyPasteHTML(textarea.value);

    function syncToTextarea() {
      textarea.value = toEmailHtml(quill);
    }

    function showTab(mode) {
      var visual = mode === "visual";
      // Leaving the visual tab must flush first, or the HTML tab shows stale markup.
      if (!visual) syncToTextarea();
      else quill.clipboard.dangerouslyPasteHTML(textarea.value);

      wrapper.classList.toggle("show-html", !visual);
      wrapper.querySelectorAll(".editor-tab").forEach(function (tab) {
        tab.classList.toggle("active", (tab.getAttribute("data-mode") === "visual") === visual);
        tab.setAttribute("aria-selected", String((tab.getAttribute("data-mode") === "visual") === visual));
      });
    }

    wrapper.querySelectorAll(".editor-tab").forEach(function (tab) {
      tab.addEventListener("click", function () { showTab(tab.getAttribute("data-mode")); });
    });

    // Submitting from the visual tab would otherwise post whatever the textarea held when the page
    // loaded, silently discarding every edit.
    wrapper.closest("form").addEventListener("submit", function () {
      if (!wrapper.classList.contains("show-html")) syncToTextarea();
    });

    // Click a placeholder chip to insert it. Typing {{CandidateFirstName}} by hand is the single
    // easiest thing to get wrong, and a typo is only discoverable by reading Worker logs after a
    // real send — the renderer leaves an unknown placeholder as literal text and logs a warning.
    //
    // Two chip markups, because two screens grew them independently: .placeholder-chip[data-token]
    // from the old template editor, and .token-chip[data-insert-token] from the message editor.
    //
    // ⚠️ app.js has its own handler for the second kind, for the compose screens that have no Quill.
    // Both listeners fire on the same click, so each chip bound here is stamped data-token-handled
    // and app.js bails on it — otherwise the tag is inserted twice, once into the hidden textarea
    // and once into the editor. Checked at click time rather than bind time so it does not matter
    // which script loaded first.
    card.querySelectorAll(".placeholder-chip, .token-chip").forEach(function (chip) {
      chip.setAttribute("data-token-handled", "true");
      chip.addEventListener("click", function () {
        var token = chip.getAttribute("data-token") || chip.getAttribute("data-insert-token");
        if (wrapper.classList.contains("show-html")) {
          var at = textarea.selectionStart != null ? textarea.selectionStart : textarea.value.length;
          textarea.value = textarea.value.slice(0, at) + token + textarea.value.slice(textarea.selectionEnd != null ? textarea.selectionEnd : at);
          textarea.focus();
          textarea.selectionStart = textarea.selectionEnd = at + token.length;
        } else {
          var at = lastRange ? lastRange.index : quill.getLength() - 1;
          quill.insertText(at, token, "user");
          quill.setSelection(at + token.length, 0, "user");
        }
      });
    });

    showTab("visual");
  }

  document.addEventListener("DOMContentLoaded", function () {
    document.querySelectorAll("[data-editor-for]").forEach(initEditor);
  });
})();
