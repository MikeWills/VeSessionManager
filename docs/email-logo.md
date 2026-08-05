# Team logo in emails

A team can upload a logo and place it in any email template with `{{Logo}}` (2026-08-05). Companion
to `docs/email-template-editor.md`, split into its own change because it touches the send pipeline
rather than just the admin UI.

## CID, not a hosted URL

The logo travels **inside the message** as a MIME linked resource, referenced from the body as
`<img src="cid:vesm-team-logo">`.

The obvious alternative — an `<img>` pointing at the public site — was rejected because **Gmail and
Outlook block remote images by default**. The logo would show as a broken-image placeholder until the
recipient clicked "show images", which most never do. A CID part renders immediately, with nothing to
click.

`LinkedResources`, not `Attachments`: a linked resource is referenced from the HTML and is not
offered as a downloadable file. Adding it as a plain attachment would both fail to render inline and
hang a stray `logo.png` paperclip on every email.

The content id is a constant (`InlineImage.TeamLogoContentId`). The renderer writes the `<img>` tag
and the sender labels the MIME part, in different classes with no shared state — a generated id would
have to be threaded between them for no benefit, since one message never carries two logos. **The two
must agree exactly**, or the client silently shows a broken image.

## Stored in the database

`Team.LogoBytes` / `LogoContentType` / `LogoUpdatedUtc`.

**Not on disk**, deliberately: `deploy.yml` runs `rsync --delete` over the app directory on every
release — precisely why the SQLite file lives outside it — so an uploads folder under `wwwroot` would
be wiped by the next deploy. As a column it sits beside the other per-team settings and is backed up
by whatever backs up the database.

Not encrypted, unlike the credential columns on the same entity: a logo is public branding that ends
up in every candidate's inbox, so `EncryptedStringConverter` would add cost and key-ring risk
protecting something published by design.

## Upload validation

- **PNG and JPEG only**, decided from the file's own magic numbers. **The browser-declared
  `Content-Type` is never consulted** — it is attacker-controlled and trivially spoofed, so trusting
  it would let anything at all be stored and then served to mail clients under an image label.
- **SVG is deliberately excluded.** Mail clients broadly do not render it, and an SVG is an
  executable document that would be a stored-XSS vector anywhere it were ever served back.
- **200KB cap.** Every byte is added to every email the team sends. Anything approaching the limit is
  a photo pasted in by mistake.

The preview on Team Settings is a `data:` URI rather than a served route — the image is small, the
page is admin-only, and it avoids adding a public endpoint that hands out team images.

## `{{Logo}}` is the one placeholder that is not HTML-encoded

`EmailTemplateRenderer` HTML-encodes every body placeholder, deliberately: values like
`CandidateName` come from ExamTools' public registration intake, i.e. registrant-controlled data, and
without encoding a script-bearing name would be injected verbatim into a real HTML email.

`{{Logo}}` has to be exempt, or the recipient would see the literal text of an `<img>` tag.

> **The exemption is safe only because the value is built inside the renderer, from a constant, out
> of app-owned data.** It is never supplied by a caller and never derived from registrant input.
> Nothing registrant-controlled may ever join that branch — doing so would inject attacker-authored
> markup straight into a real email. `OtherPlaceholders_AreStillHtmlEncoded_EvenAlongsideTheLogo`
> pins this: it renders a `<script>` payload as `CandidateName` in the same body as `{{Logo}}` and
> asserts it comes out encoded.

## Two behaviours worth knowing

- **A template carrying `{{Logo}}` stays valid for a team with no logo.** It renders to an empty
  string — never the literal `{{Logo}}`, which is what the unknown-placeholder path would otherwise
  emit into a candidate's inbox. So the placeholder is always safe to leave in a template.
- **A template that never mentions `{{Logo}}` attaches nothing**, even when the team has one. The
  renderer only loads the bytes when the body actually asks, so unrelated templates don't pay the
  size on every send.

## `Logo` is a universal placeholder, not a per-key one

`EmailTemplatePlaceholders.ByKey` means "tokens the calling service passes in", and
`EmailTemplatePlaceholdersTests` asserts its exact contents to catch drift against
`CandidateNotificationService`/`PaymentReminderService`. `Logo` is supplied by the *renderer*, from
team-level data, and is available in every template — so it lives in a separate `Universal` list.
Only `ForEditor(key)` merges the two, which is what the admin page's insertable chips use.

Folding it into `ByKey` would have broken the drift test and, worse, made it meaningless.

## A test-suite hazard this surfaced

`PaymentUniqueIndexSqliteTests` has two tests that migrate to a **historical** migration and then
seed rows — but the `DbContext` is always the **current** model. The moment `Team` gained its logo
columns, EF emitted an `INSERT` naming them and SQLite failed the whole test with *"table Teams has
no column named LogoBytes"*.

Fixed at the seam rather than patched around: those tests now insert the Team with raw SQL naming
only the columns that existed at that migration. **Any migration test that seeds through the model
has this hazard** — `Team` is simply where it bit first, being the most-extended entity.
