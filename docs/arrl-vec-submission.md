# Filing a session with ARRL-VEC

**Built 2026-08-19, issue [#197](https://github.com/MikeWills/VeSessionManager/issues/197).** Replaces
a four-step manual routine: download the VEC archive from ExamTools, upload it to ARRL's form, print
the confirmation to PDF, file the PDF and zip together.

**ARRL only, and that is not a simplification to tidy up later.** Every VEC has its own submission
process — some want an email, some a form, some something else again. There is no shared shape to
abstract over, and inventing one from a sample size of one would be guessing at the other thirteen.
ExamTools models per-VEC exports itself (`laurel_export.csv`, `w5yi_export.csv`), which is
independent corroboration that this is the shape of the real world rather than over-caution. A
session under any other VEC finds no submitter and is told so; it keeps the existing "I filed this by
hand" toggle.

## Why this subsystem is shaped so defensively

There is **no sandbox, no staging endpoint and no dry-run** on ARRL's side. Every exercise of the real
code path files a real session with the organization that issues licenses, on behalf of a team whose
reputation is attached to it. A bad submission is not a failed test; it is wrong data delivered to a
VEC, and there is no rollback — the answer is "contact ARRL".

Every other integration here can be exercised safely: Square has a sandbox, ExamTools has a dev site,
email has a deployment-wide test-mode redirect. This one has nothing, so the feedback loop that
normally catches mistakes has to be front-loaded into the design.

## What ARRL's form actually is

`https://www.arrl.org/vec-upload.php` — 4KB of hand-written HTML, no framework, **no authentication,
no cookies and no CSRF token**.

⚠️ **The POST target is not the page URL.** The form's action is a relative `?processed=1`, so filing
goes to `vec-upload.php?processed=1`. Posting to the bare URL most likely returns the empty form with
a 200 — a silent no-op that reads as success to anything checking status codes.

| field | notes |
|---|---|
| `fullname` | |
| `callsign` | |
| `email` | |
| `phone` | digits only in real submissions |
| `sessionDate` | `<input type="date">`, so `yyyy-MM-dd` |
| `location` | "city and state" in the USA, otherwise city and country |
| `paymentMethod` | radio: `mail-in` \| `phone-in` \| `credit-card-filed` |
| `amountCharged` | plain decimal, **no `$`** |
| `note` | |
| `the_upload[]` | `multiple`; PDF, DOC, DOCX, JSON, ZIP; 40MB |
| `submit-btn` | value `Upload!` |

`amountCharged` carries a static `required` attribute on every render while ARRL's own `checkForm()`
only demands it for the credit-card case. The page contradicts itself, so **its client-side rules are
not evidence of the server's** — and there is no way to learn the server's without a real filing.

## Where each value comes from

The split is: **per-team configuration** for anything describing how a team operates, **derived** for
anything the app already knows, **decided at submission** only where a human must look.

| field | source |
|---|---|
| `fullname` | the session lead's name + a per-team **postfix**, concatenated **verbatim** |
| `callsign`, `phone` | the session lead |
| `email` | per-team choice: the lead's address, or one fixed team address |
| `sessionDate` | the session's own date, **in Eastern** |
| `location` | per-team |
| `paymentMethod` | per-team |
| `amountCharged` | derived from the session's fee summary |
| `note` | per-team default, edited most times |
| files | the VEC archive, fetched; plus one optional attachment |

**Every field is editable on the preview.** The configuration and the derived values are *prefill*;
what is on screen is what gets sent. That is also why the stored record keeps the submitted values
rather than re-deriving them later — what was filed and what today's configuration would produce are
two different questions.

It also dissolves the ugliest edge in the derived-contact design: ExamTools supplies no contact
details at all, and the VE retention purge clears them, so a session lead with no phone on record is
ordinary rather than exceptional. The operator types it.

### Nothing is defaulted, deliberately

`Remote Online` is right for both teams on this deployment and is still the wrong thing to bake into
the column. A shipped default that happens to be non-empty reads as "an admin configured this" when
nobody did — the same trap CLAUDE.md records against `SmtpHost`. Here the consequence is worse than a
repeating log line: a team that meets in person, whose admin never opened the screen, would file
`Remote Online` to ARRL, with nothing on either side looking broken.

The email source is an **enum plus an address**, not one nullable string, for the same reason: "blank
means fall back to the lead" reads identically to "nobody has filled this in yet".

### The name is a postfix, not a template

HRCC files as `Mike Wills/Nick Booth (CC)/HRCC VE Team`; MARC files the bare name. In both real
samples the addition sits strictly at the end, so a postfix is what the evidence shows and a `{{…}}`
placeholder syntax would be generality invented for a case nobody has.

⚠️ **Concatenated with no separator inserted.** The real value has no space before the slash.

### ⚠️ The session date is Eastern, not UTC

Every FCC date arrives date-only; a session start is a real instant. **697 of 867 stored sessions
start between 23:00 and 04:00 UTC**, so `.Date` answers "what day is it in London" and is *tomorrow*
for most of them — the [#248](https://github.com/MikeWills/VeSessionManager/issues/248) bug class.

Confirmed against a real receipt: a session starting `2026-04-22 01:30Z` files as `2026-04-21`.

Note the archive's own filename uses the **UTC** timestamp. Both are right: the filename reproduces an
identifier ExamTools already minted, the form field answers "what day did the exam happen".

## The amount

`Session.GetFeeSummary().TotalRemitToVec` — paid payments **net of refunds**, less either the
per-candidate retained cap or the session's flat `RetainedAmountOverride`, clamped at zero.

**A refunded fee is not owed to the VEC** (Mike, 2026-08-19): "the person has not tested". This was a
real bug in shipped code, not merely a wrong assumption in this feature — see
`docs/square-refunds.md` and PR #430.

The preview shows the **arithmetic**, not just the total, and names any refunded or
`AmountMismatchFlaggedUtc` payment feeding it. A confident-looking number with no indication that its
inputs are unusual is worse than no derivation at all — and the amount stays editable because a
payment can land after the summary is read.

## The archive

```
GET /api/veUser/sessions/{examToolsSessionId}/vecDownload/ExamSession_{vecCode}_archive.zip
```

Verified live 2026-08-18 against a closed MARC session. Returns `application/octet-stream`; the zip
holds the signed session PDF and a JSON summary, so **it carries candidate PII**. See
`docs/examtools-api.md`.

⚠️ **Read the filename from `Content-Disposition`, never from the URL.** The URL's filename is the
generic `ExamSession_arrl_archive.zip` — identical for every session of every team. The descriptive
name exists only in the header. A client taking the last URL segment would file a run of
identically-named archives, destroying the identifying value of records this team has had to go back
to years later.

An **incomplete session returns a real 403** with a structured body
(`{"type":"ForbiddenError","message":"Exam Session needs to be completed",…}`). This is the most
common expected failure and it is self-correcting, so the preview shows ARRL's own wording. Note the
200-with-an-error-body quirk is specific to `/api/ve/login` and does **not** generalize across
ExamTools endpoints.

## Two files, at most

The archive is fetched automatically; the operator may attach **one** more, in practice the youth
grant program form. Both go in a **single POST** — ARRL's input is `the_upload[]` with `multiple` — so
there is no upload state machine and `VecSubmissionStatus`'s one-way toggle is untouched.

The app prompts for the form when the session has a youth-rate payment, and says so when one is
expected and nothing is attached.

## Deciding whether it worked

**Success is recognized. Failure deliberately is not.**

The only positive signal is **the filename we posted** echoed back followed by `has been uploaded
successfully`. Everything else is `Unknown`.

Nobody on this team has ever seen ARRL's failure page — years of filing by hand, no sample, and the
only way to obtain one is to make a real bad submission. A matcher built from zero samples would be
guessing, and it would guess in the expensive direction: a real rejection classified as handled,
marked `Submitted`, and never filed.

⚠️ **Status codes are not consulted at all.** Both outcomes arrive on the same endpoint.

⚠️ **Whether two files produce two success lines is unverified.** Until a real youth submission
settles it, every posted filename must be confirmed — so a two-file submission may come back
`Unknown` even when it worked.

### There is no retry, anywhere

A fire-and-forget form POST supports neither of this app's usual answers to a crash between an API
call and its persistence: there is nothing to query before creating, and no idempotency key ARRL would
honour. A timeout after the request left the machine may mean it succeeded.

So **absence of a receipt is not absence of a filing**. A transport failure is recorded rather than
raised, and the UI says "this may or may not have been filed" rather than "it failed" — telling
somebody it failed is what produces a duplicate.

### Two guards against a second filing

1. The session's own `VecSubmissionStatus`.
2. **The existence of any submission row, including an unconfirmed one.** This is the one that
   matters: an `Unknown` outcome deliberately leaves the session unsubmitted — that is the state
   needing a human — so nothing else would stop a second press.

ARRL cannot dedupe and has no unsend, so whatever guards the button is the entire protection.

## The operator flow

1. A **Session Manager** presses "Submit to VEC" on the session. For an ARRL session this is a
   **GET** to the preview; every other VEC keeps the POST toggle.
2. The complete form renders, prefilled and editable, with the archive, the fee arithmetic, and any
   blocking blank named individually.
3. They read it and confirm. **Only that confirmation posts.**
4. The response is stored and matched against the posted filenames. `VecSubmissionStatus` moves to
   `Submitted` **only** on a positive match.

There is no dry-run mode beside this — the preview *is* the only route to the POST, so it cannot be
skipped. The issue asked for a preview and a confirmation as two safeguards; collapsing them into one
removes the possibility of forgetting the first.

**Session Manager, not TeamAdmin.** `ToggleVecSubmission` is already an SM action on that page, and
splitting one workflow across two roles by account flag would be the worse outcome.

Note `ToggleVecSubmission` is a misnomer: it calls `MarkSubmittedAsync`, which refuses when already
submitted, and the button only renders when the session is unsubmitted. **It has always been
one-way** — the name misleads, the behaviour does not.

## Keeping what was filed

Mike: *"We just want an archive of what was sent in case there's ever a question. I have had to use
this in the past."*

That settles PDF-vs-HTML toward storing the **raw response verbatim** and adding no PDF library: the
purpose is evidentiary, and an app-generated PDF is a *rendering* of the response, strictly weaker
than the response itself if a filing is disputed. The team can still print to PDF exactly as today.

Note the emphasis: an archive of **what was sent**. The request is not a supporting detail beside the
receipt — it is the half that says what was actually filed.

### ⚠️ The receipt carries PII

#197 originally recorded that it did not, and that claim was the sole basis for treating stored
receipts as exempt from retention. It is wrong. The confirmation page echoes the submitter's **name,
call sign, email and phone**, adds an **IP address** of its own, and reproduces the note — which in
one real submission contained a card's last four digits tied to a named person.

**Never render it back into a page.** It is offered as a download; this codebase has zero `Html.Raw`
and should keep it.

### Files on disk, not database blobs

Decided against measurements: an archive is ~377KB and 279 sessions closed across this deployment's
teams in the last year — roughly **106MB/year into a 4.5MB database**, a 20x growth in year one,
compounding, with every off-box backup re-shipping the accumulated set.

Two more reasons behind that one: deleting a BLOB row does not remove the bytes from the SQLite file
until a `VACUUM`, so a "purged" archive would linger on disk and in every backup taken before it — and
`VACUUM` is a poor fit here, since Web and Worker share one file and it rewrites under an exclusive
lock.

The cost accepted in exchange is **atomicity**: row and file can diverge, so the purge deletes the
file *before* marking the row. That order decides which way an interrupted run fails — this way leaves
a deleted file with an unmarked row, which the next run settles harmlessly; the reverse would mark it
purged and strand the file with nothing pointing at it.

**Known gap:** a crash between writing the files and saving the row leaves orphans no row can name.
Catching those needs a walk of an unbounded directory tree to cover a window of milliseconds.

### Layout and paths

`team/vec/year/month`, four deep — close to how these are filed by hand, so the archive stays
browsable by a person. Segments come from `Team.ExamToolsTeamCode` and `Vec.MatchCode`, every one
sanitized.

**The on-disk filename is ours; the wire filename is ARRL's.** They are normally identical, which is
exactly why the distinction is explicit: a third-party or operator-supplied string must never shape a
path.

## Nothing can post to ARRL by accident

- **The endpoint is configuration, blank in the shipped `appsettings.json`.** A fresh clone, a
  developer machine and the test suite have nowhere to post. Only `appsettings.Production.json`
  carries the URL — not a secret, so it deploys like any other setting.
- **`ArrlEndpointIsNotHardcodedTests` fails the build** if ARRL's host appears in any other file. It
  caught itself on the first run, because its own constant contained the host; the literal is split
  rather than the file exempted, since an exemption would leave a hole exactly where the rule is
  defined.
- **Unconfigured refuses loudly** — the one deliberate break from the optional-integration pattern. A
  quiet skip is right for a job that will retry next poll; here it would leave somebody believing they
  had filed when nothing was sent, and they would find out from the VEC.
- **The Worker gets the archive store but never the submitting client**, and its configuration carries
  no `UploadUrl`. No background job may be able to reach ARRL.

A useful consequence: on a dev machine, pressing "Send to ARRL-VEC" is safe. It refuses before any
request and without creating a submission row, so the whole flow is exercisable except the POST.

## Retention

`SystemSettings.VecSubmissionArchiveRetentionDays`, swept by `RecordRetentionService` alongside the
audit-log and job-history windows. **Null means keep forever, and that is the shipped default** — and
given this team has needed one of these after the fact, that may be the right permanent answer rather
than a placeholder.

**The files age out; the submission row never does.** The row is the record that a filing happened,
and for an unconfirmed submission it is the only account of what went.

⚠️ **A window longer than `PiiRetentionWindowDays` or `VeContactRetentionYears` means the archive
outlives those purges** — the zip is the session's paperwork. That may well be right, since a filing
record plausibly outranks a retention policy, but it is a **deliberate exception**, not an oversight.
The receipt in `ResponseBody` is a column rather than a file and is **not** covered by this window;
whether it should age out with the VE's contact details is still open on #197.

## When ARRL never confirms

An unconfirmed submission raises an alert on the nav bell — exactly the class of problem the bell
exists for. The session still looks unsubmitted, which is correct, but nothing else anywhere would
make anyone go and look, and the only resolution is a person telephoning ARRL.

The alert says **"may still have been filed"**, never "failed", and a test asserts it.

It clears when a human marks the session submitted, which is what they do once ARRL confirms — there
is no separate resolved flag, because that action already means it.

Adding it required `AlertFeedService`'s role gate to become **per source**: reconciliation is
admin-only, but this alert points at session detail, which every role can open. A single gate at the
top would have hidden it from exactly the Session Managers who press the button.

## Deployment

- `appsettings.Production.json` carries `UploadUrl` and `ArchiveRootPath` and deploys automatically.
- ⚠️ **Create the archive directory** (`/var/lib/vesessionmanager/vec-archives`) owned by the
  `vesessionmanager` account, **outside the app path** — `deploy.yml` runs `rsync --delete` over that
  on every release, which is why the database lives under `/var/lib` too.
- ⚠️ **Add it to the off-box backup.** Done on the live box (confirmed 2026-08-20) — backed up
  alongside the database and key ring from [#256](https://github.com/MikeWills/VeSessionManager/issues/256),
  which originally covered those two only. Any other deployment must arrange this itself: an
  unbacked-up archive fails silently, and nothing looks wrong until a receipt is wanted and missing.
- Fill in each team's ARRL settings. Nothing is defaulted.

## Rollout

Run it **alongside the manual process** for the first few real sessions: open the preview, compare it
against what would be filed by hand, and only then start trusting it. That comparison is the entire
substitute for the testing that cannot exist here.
