# Emailing candidates from a session (2026-08-16)

Pick candidates on a session, start from a template, edit the message, send it.
Issue [#144](https://github.com/MikeWills/VeSessionManager/issues/144).

## What this is, and what it is not

Every other candidate email in this app is sent by code: a job decides the moment, fills a stored
template's placeholders and posts it. This one is sent by a person, who edits it first.

The issue asked for a "getting started locally" email after a session — clubs, nets, repeaters. What
got built is the mechanism rather than the one email, because Mike widened it twice while it was
being scoped: **several candidates at once, and a picker over which template to start from**. So
"Getting started locally" ships as the first template on that mechanism, and a blank draft covers the
cases no template anticipated.

Reached from Session Detail → **Email candidates**, beside "Refresh candidates".

## Decisions

**Offered for anyone reachable** — no `Tested` gate, no pass/fail gate. Mike's answer when asked
which candidates should be eligible was "those who I choose". The app only declines to offer a
candidate it has no address for, and one who is withdrawn (their PII is cleared immediately, so there
is usually nothing left to write to).

**The draft is never written back to the template.** Admin → Email Templates is where a team
maintains its standard text; this screen takes a copy and adjusts it for these people today.
Switching templates replaces the draft, which the page says out loud — a GET form reload, no
JavaScript beyond the existing `data-autosubmit`.

**Payment links are deliberately unavailable.** The automated emails carry
`{{PaymentLinkUrl}}`/`{{OutstandingPaymentLinkUrl}}` because they are sent at the moment those links
are live. A hand-composed message goes out whenever somebody decides to send it — usually after the
session — so a checkout link that is expired, already paid or simply blank is worse than no link.
Anything that needs one has an automated template that already sends it.

Available: `{{CandidateName}}`, `{{CandidateFirstName}}`, `{{CallSign}}`, `{{SessionDate}}`,
`{{TeamName}}`, and the universal `{{Logo}}`.

**Which templates the picker offers.** Getting started locally, Registration confirmation, Day-before
reminder — plus the blank draft. Felony disclosure and Youth Program stay the per-candidate buttons
they already are: both carry applicability rules that exist because sending them to the wrong person
is a real harm ([#221](https://github.com/MikeWills/VeSessionManager/issues/221),
[#274](https://github.com/MikeWills/VeSessionManager/issues/274)), and bulk is the shape #221
deliberately moved away from. Payment expiration is excluded outright — it goes to the team's own
admin address, not to candidates.

**No new opt-out switch.** "A team that doesn't want it never sees it" is served by the existing
per-team Email integration gate; a team that does not want this simply never presses the button.

**No nudging.** A send shows in the candidate's Email history and nowhere else — no roster chip, no
count, no alert. An optional courtesy email should not become outstanding work the app chases you
about.

## Two things that would be easy to get wrong

**There is one renderer, and this uses it.** `EmailTemplateRenderer.RenderAsync` renders a template
loaded from the database; the draft here is text that is not in the database, so the class gained
`RenderTextAsync` and the old method delegates to it. The alternative — a private `Replace` chain —
is exactly what `VeSessionInvitationService` did, and it shipped without HTML-encoding: a session
title carrying markup rendered as a live link in every invited VE's mail client, inside a genuine
message from the team's real address ([#260](https://github.com/MikeWills/VeSessionManager/issues/260)).
Candidate names come from the same class of source, ExamTools' public registration intake. The
encoding rule, the subject line-break stripping (#261) and `{{Logo}}`'s raw-HTML-plus-attachment
handling are all in one place because of that.

**The recipient list is re-scoped in the service, not just checked on the page.** The ids arrive from
a posted form, so "the screen only offered this session's candidates" is a default, not a constraint.
Unscoped, this sends an attacker-authored subject and body from the team's own SMTP to any candidate
row on the deployment — indistinguishable from genuine mail because it *is* genuine: same From, same
Reply-To, same server. That is [#238](https://github.com/MikeWills/VeSessionManager/issues/238) in a
new place. Ids outside the session are dropped and counted rather than failing the send, since a
legitimate sender reaches this by leaving the screen open while a candidate is withdrawn.

## History, and why it is a table

`CandidateEmailSend` (CandidateId, TemplateLabel, SentUtc, SentByUserId) — one row per recipient per
send, feeding the Email history modal alongside the automated `...SentUtc` columns, and the compose
screen's "already sent" column.

A column per template is what this app does everywhere else, and it stops working the moment a team
can write its own templates (which is the next PR): a column cannot be added by somebody at runtime.

- **Only a delivery that succeeded is recorded.** The list answers "who has already had one", and a
  second pass over a session skips the people on it — so a failed send recorded here would hide
  exactly the person that pass exists to catch.
- **No subject or body is stored.** A subject routinely carries the candidate's own name, so a store
  holding content is a store the PII purge has to reach into and keep reaching into.
- **The label is a string, not a foreign key.** The draft is editable, so what went out is not the
  template — and a template deleted later must not take the history of what it sent with it.

Deleting a user who has sent one of these is refused, with a count, like every other record of what a
person did (`UserManagementService.FindDeleteBlockersAsync`).

## Reported like a fan-out

`CandidateEmailBatchResult` — Sent / Failed / NoEmailAddress / NotOnSession / `Error` — rather than
the single-candidate `CandidateEmailSendResult`. A partial outcome is the normal case for a fan-out
over addresses people typed. Candidates with no address are counted rather than skipped: "sent 8 of
10" with no explanation is worse than a number somebody can act on.

**A muted team is an error here, not a quiet success.** `TrySendAsync` returns true for a muted team —
the deliberate settle-without-doing rule that stops scan-based jobs building a backlog they would
later flush all at once — which is right for a job and exactly wrong for somebody standing at a button
waiting to hear what happened.

*Adjacent and not fixed here:* the existing felony and youth-program buttons report "Sent." for a
muted team. Same bug, different scope — it wants an issue rather than a silent change to a method four
background jobs depend on.

## What the guards caught

Worth recording, because all three were found by tests that already existed rather than by review:

- `FormBindingTests` — the hidden field posted `name="SelectedTemplateKey"` while the property binds
  as `template`, so **every send would have been labelled "Custom message"**. A send test cannot catch
  this: it posts a hand-built body, so the markup can name the field anything. Verified by reverting
  the fix and watching the end-to-end test stay green.
- `ActionMessageSingleSourceTests` — a `CanReceive` rule computed inside the page model instead of
  `CandidateCapabilities`, which is the one home for "is this action applicable to this candidate".
- `UserDeleteCoverageTests` — the new FK to `User` with nothing in the delete path, which would have
  thrown a Restrict violation instead of refusing with a reason.

## Team-defined templates (second PR, 2026-08-16)

A team can now write its own templates in Admin → Email Templates — create, rename, delete — and they
appear in the compose picker alongside the shipped ones. That is what makes the screen cover cases
nobody shipped a template for.

`EmailTemplateAdminService` was deliberately edit-only, on the stated grounds that *"the set of Keys
is fixed by what CandidateNotificationService/PaymentReminderService actually look up"*. **That
reasoning is still right, and it is exactly why this is safe:** a team-defined template is never
looked up by anything. A person picks it, so no code path can break by its absence and none can point
at one that no longer exists.

**The dot is the whole mechanism.** Generated keys are `Custom.<slug>`; no shipped key contains a dot,
so a name somebody types can never become a key the sending code looks up — including a key added
years from now. Everything a person sees is `DisplayName`; the key never appears in the UI.

Other decisions:

- **Rename does not move the key.** A rename is a label change; regenerating the key would strand an
  open compose screen and make the row look like a different template. History rows keep the label
  they were sent under — they record what was actually sent, not what it is called today.
- **Delete is a real delete, and it is safe because nothing points at the row.** `CandidateEmailSend`
  stores the label as a string precisely so the record that somebody was told something outlives the
  template it was written from. There is a confirm step, because the body may be the only copy of
  wording someone spent time on.
- **Rename and delete refuse a shipped template in the service**, not just by hiding the buttons —
  something in the app sends that key and has no other way to find it. The message says so rather
  than reporting a permission problem.
- **Duplicate names are allowed**, on one team and across teams; `(TeamId, Key)` is unique, so the key
  gets a numeric suffix. A name of nothing but punctuation slugs to `template` rather than to nothing,
  which would otherwise make every such name the same key.
- Team-defined templates group under **"Your own templates"** on the admin page — they belong to no
  session phase, because nothing sends them on a schedule.
- Their placeholder chips are `CandidatePlaceholderValues` plus `{{Logo}}`: there is no send-site
  dictionary to read a token list from, and that is what the compose screen actually resolves.

## Next

Issue [#191](https://github.com/MikeWills/VeSessionManager/issues/191) — the same shape aimed at VEs
rather than candidates, waiting on this so it can ship with the picker from day one (Mike, 2026-08-16).
Its stated blocker is stale: `VolunteerExaminer.Email`, `Phone` and `ContactPreference` all exist now.
Decided already: one team is chosen on the message screen, and the recipients are that team's active
members — a VE can be on several teams, but a message sends from one team's SMTP.
