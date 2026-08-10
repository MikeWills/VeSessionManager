# VE session invitations (issue #142)

Inviting a team's volunteer examiners to work an upcoming session. Reached from Session Detail, next
to the roster it concerns.

## The point is the Zoom link

The issue says so outright: this exists *"so we don't have to seek out the Zoom link"*. So
`{{ZoomJoinUrl}}` is a first-class placeholder, and the compose screen **warns up front when the
session has none yet** rather than letting someone discover a blank line in an email they already
sent. Zoom scheduling is asynchronous, so a session created minutes ago legitimately has no link.

Placeholders use the same `{{Token}}` convention as the email templates, so nobody has to learn two
syntaxes: `VeName`, `CallSign`, `SessionTitle`, `SessionDate`, `ZoomJoinUrl`, `TeamName`. An unknown
token is left as written — the same choice `EmailTemplateRenderer` makes, and for the same reason: a
visible `{{Typo}}` is a bug someone fixes, where a silently empty gap is one nobody notices.

## Ad-hoc text, not a stored template

The issue asks for a way to *draft* subject and body per send, and an invitation is a different
sentence every time — "we're short two", "same crew as last month?", "new VEs welcome". The screen
opens pre-filled with something sensible and expects it to be rewritten.

Which is why this does not go through `EmailTemplate`: that machinery exists for messages the app
sends automatically and an admin tunes once.

## Sent from the team

The **team's** SMTP credentials, and the team's own From/Reply-To out of `EmailSettings` — the same
row candidate mail sends from. An invitation arriving from a different address than everything else
the team sends would read as spam to its own volunteers.

Deliberately unlike the self-service links, which use the deployment-wide sender because a VE may
serve several teams. A session belongs to exactly one team, so there is no ambiguity to resolve here.

## Who can be invited

Every VE with an **active membership** on the session's team. The picker shows name, call sign, tags,
whether they are already on this session's ExamTools roster, and — the useful part —
**[eligibility for this session's date](ve-license-tracking.md)**.

That check surfacing here is where it earns most: inviting someone who cannot legally serve on the day
is a wasted seat and an awkward conversation later. It is the second place issues #107 and #142 pay
each other back.

A VE with **no email address cannot be selected at all** — the checkbox is disabled and labelled,
rather than letting someone tick it and wonder why the count came up short.

The picker shows names, tags and eligibility. **It does not show contact details**, which is why this
page is gated on `SessionAccessScope.CanEdit` rather than restricted to admins the way the VE
Directory is: a Session Manager running Saturday's session is exactly who invites people to it.

## Three outcomes, counted separately

`Sent`, plus:

- **Failed** — SMTP rejected it.
- **No email address** — selected but unreachable.
- **Text-only** — selected but preferring SMS, which does not exist yet.

"Sent 8 of 10" with no explanation is worse than a number someone can act on: one of those means go
and fill in an address, another means nothing is wrong. Per-recipient isolation, like every other
fan-out here — one bad address does not stop the rest.

Text-only is **honoured now even though nothing can set it** while SMS is unbuilt, so the loop does
not need remembering when it arrives. The contact-preference dropdown offers Text and Both as disabled
options for the same reason: modelling them now avoids a migration later, and offering a preference
the app cannot honour would quietly stop a VE being contacted at all.

## Not built

- **SMS.** Placeholder only, per the issue. It needs a provider and a decision about cost, and the
  model is ready for it.
- **Tracking replies or acceptances.** This sends an email; a VE replies to a human. Recording who
  said yes would duplicate the ExamTools roster that already answers it.
- **Scheduling or reminders.** One deliberate send, by a person, when they want it.

## Not verified

The send path has never run against a live SMTP server. Eleven tests cover the selection, rendering,
skip and failure-isolation logic with a fake sender.
