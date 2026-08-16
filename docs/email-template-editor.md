# Email template WYSIWYG editor

Admin → Email Templates gained a rich-text editor (2026-08-05). Before this, every template was a
raw HTML `<textarea>`, which meant a Session Manager wanting to bold a line had to know HTML.

**The source view is a requirement, not a fallback.** Every template can still be edited as raw HTML
via the second tab, because these are emails and there will always be something the toolbar cannot
express.

## The editor is a second view, never a replacement

The `<textarea name="body">` remains the field the form posts, in both tabs. Quill is a view onto it:

- switching to **HTML** flushes the editor's HTML into the textarea;
- switching to **Visual** re-parses the textarea into the editor;
- **submitting from the Visual tab flushes first** — without that, the form would post whatever the
  textarea held at page load and silently discard every edit.

The server contract is therefore identical to before the editor existed, and with JavaScript
disabled the page degrades to exactly the plain textarea it used to be.

## Reading HTML back out of Quill — neither obvious option works

Measured against a real seeded template before any of this was built:

| | `root.innerHTML` | `getSemanticHTML()` | `getSemanticHTML()` + cleanup |
|---|---|---|---|
| `&nbsp;` in output | 0 | **33** | 0 |
| Bullets as `<ul>` | **no** | yes | yes |
| Quill artifacts (`ql-ui`, `data-list`) | **yes** | no | no |

- **`root.innerHTML` is a trap.** Quill 2 renders bullet lists as `<ol><li data-list="bullet">`, so an
  email client shows a bullet list **numbered**. It also leaves `ql-ui` spans in the markup.
- **`getSemanticHTML()` fixes the lists but converts every space to `&nbsp;`.** In email that is
  worse than cosmetic: a paragraph of non-breaking spaces will not wrap, so it runs off the side of a
  phone screen.

So the answer is `getSemanticHTML()` plus two replacements — `&nbsp;` → space, `&#39;` → `'`. The
blanket `&nbsp;` swap is safe because Quill is the only thing producing them here; nobody is
authoring a deliberate non-breaking space through this toolbar.

## Alignment must be an inline style

Quill's default align format emits `class="ql-align-center"`. Email has no stylesheet to resolve a
class against, so the default silently does nothing in the recipient's inbox while looking correct
in the editor. `Quill.register(Quill.import("attributors/style/align"), true)` switches it to
`style="text-align: center"`.

This is the reason the toolbar stops where it does. Headings, bold, italic, lists, links and
alignment all survive the trip to an inbox. Colour and font-size pickers were deliberately left out:
they are where users produce email that renders differently in every client, and Outlook ignores much
of it.

## Placeholders

`{{CandidateFirstName}}` and friends survive the round trip intact, **including inside `href="…"`**,
which was the main risk — a mangled token means `EmailTemplateRenderer` leaves it as literal text and
logs a warning, i.e. the breakage is only discoverable by reading Worker logs after a real send.

The placeholder chips above each template are now **click-to-insert**, in both tabs, at the caret.
Typing one by hand is the easiest thing to get wrong on this page.

> Two bugs found by the harness, both of which would have shipped silently:
> the chips live in `.roster-list`, a **sibling of the `<form>`**, so scoping the lookup to
> `.template-editor` bound nothing at all and clicking a chip did nothing; and the caret position has
> to be captured on `selection-change`, because clicking a chip blurs the editor before the handler
> can read the selection.

## Why loading through `dangerouslyPasteHTML` is the safe path

Despite the name, it is the safer option. Quill parses stored markup into its own document model and
discards anything it has no format for, so an `<img onerror=…>` hand-typed into the HTML tab by one
admin cannot fire in another admin's browser when the template is next opened.

Nothing assigns a stored body to `innerHTML` directly, and **there is deliberately no rendered
preview pane** — that would reintroduce exactly the execution path this avoids.

This is also why Quill was chosen over a contenteditable-based editor: the same document model that
causes the `&nbsp;` quirk is what makes pasting from Word or Outlook safe. Contenteditable editors
paste Word's raw markup — `mso-` styles, `<o:p>` tags, nested font tags — essentially verbatim, and
for an email-template editor that is the single most likely thing a user will do.

## Vendoring

Quill 2.0.3, BSD-3-Clause, in `wwwroot/lib/quill/` (~210KB JS + 25KB CSS). Self-hosted because the
CSP is `script-src 'self'` — a CDN reference would be blocked.

Loaded **only by this page**, via the `Scripts` section and a new `Head` section added to
`_AppLayout.cshtml` for page-specific stylesheets. It has no business on every screen.

Quill's Snow skin is light-only, so `app.css` carries dark-theme overrides for its toolbar strokes,
fills and picker surfaces — without them the editor is a white slab in dark mode.

## Not done here

The `{{Logo}}` placeholder is a **separate PR**: it touches `Team`, a migration, and
`SmtpEmailSender`'s message construction (MailKit `LinkedResources` for the CID attachment), which is
a materially different blast radius from an admin-only UI change.

## The list, the preview and the editor (2026-08-16)

Issue #395 — "email templates are kludgy". They were: every template rendered its own trigger panel,
placeholder chips, subject field and Quill editor, all stacked on one page. Finding the one you
wanted meant scrolling past the rest, and the page grew with every template added — the
team-defined ones especially, since a team can add as many as it likes.

Split three ways:

- **`/Admin/EmailTemplates`** is a list. Name, what sends it, when it was last edited, and two
  actions. It answers "what have we got" and nothing else. Creating one stays at the bottom, because
  the common case is editing something that already exists.
- **`/Admin/EmailTemplatePreview/{id}`** shows what actually goes out, rendered through the real
  `EmailTemplateRenderer` with sample values. A preview with its own renderer would agree with the
  email right up until it did not. The values are obviously fake ("Ana Ruiz") on purpose: a preview
  is opened casually, and pulling a live candidate's name and payment link into it would turn an idle
  click into a PII exposure. It also names any placeholder nothing will fill in, which is the typo
  check that previously only appeared as a status message after saving.
- **`/Admin/EmailTemplateEdit/{id}`** is the editor that used to be inline, unchanged — Quill, the
  chips, the trigger panel — plus rename and delete for a team's own templates.

**The preview renders into a sandboxed iframe**, not into the page. The body is stored HTML that an
admin wrote; inline, its markup could restyle or overlay the admin UI around it. `sandbox` with no
`allow-*` blocks scripts outright regardless of the page CSP, and `srcdoc` means no request is made,
so the app's own `frame-ancestors 'none'` is not involved.

Removing the inline editors left `AuthorizeTemplateAsync` and both `PlaceholdersFor` overloads with
no callers; they were deleted rather than left for a future audit to flag.
