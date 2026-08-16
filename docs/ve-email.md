# Emailing VEs (2026-08-16)

Writing to a team's volunteer examiners from the directory, the opt-out behind it, and the opt-in
beside it. Issue [#191](https://github.com/MikeWills/VeSessionManager/issues/191).

## What was actually missing

The issue asked for a per-team roster showing each VE's phone and email, plus a follow-on to message
them. Its stated blocker — "neither field exists on `VolunteerExaminer`" — had already gone stale:
`Email`, `Phone` and `ContactPreference` all exist, editable per VE and in the CSV export. What was
missing was a way to *write* to VEs outside a single session's invitation, and any at-a-glance sense
of who could be reached at all.

**The directory shows presence, not values** (Mike, 2026-08-16): a Contact column with an envelope
and a phone icon. The addresses stay on the VE's own page and in the export. That matches the VE
invitation screen, which has always listed names, tags and eligibility and deliberately no contact
details.

## The message screen

`/SessionManager/VeEmail`, reached from the directory, admin-gated like the directory itself.

**One team sends, always.** A VE can be on several teams, but a message goes out over one team's SMTP
with that team's From and Reply-To — so the team is chosen on the screen and the recipients are that
team's active members (Mike, 2026-08-16). Sending "as" a team somebody does not belong to puts a
stranger's address in their inbox, which is the shape phishing takes.

Everything else mirrors the candidate screen built for #144: a template picker (team-defined
templates written for VEs, or a blank draft), a plain HTML textarea, a checkbox list with filter and
select-all, one SMTP handshake for the batch, and per-recipient failure isolation. Rendering goes
through `EmailTemplateRenderer.RenderTextAsync` rather than a private substitution, which is the
lesson of [#260](https://github.com/MikeWills/VeSessionManager/issues/260) — this feature's own
sibling hand-rolled one and shipped it without HTML-encoding.

The recipient list is **re-scoped inside the service**, not just checked on the page. That is
[#238](https://github.com/MikeWills/VeSessionManager/issues/238) again, in the place it originally
happened.

## Unsubscribe (CAN-SPAM)

A link in every message. If the draft places `{{UnsubscribeUrl}}` itself it stays where the author
put it; otherwise a footer carrying it is appended — an unsubscribe that depends on somebody
remembering a placeholder is one that will eventually be missing from a real send.

**It stops every email the app sends that VE, session invitations included.** This is the decision
with a real operational cost, and it was deliberate: somebody who clicks "stop emailing me" and then
receives an invitation has been filtered, not unsubscribed. CAN-SPAM would arguably permit continuing
to send those as relationship mail. Both the invitation screen and the message screen show the state
as an **Unsubscribed** chip with the checkbox disabled, so the team knows to telephone them rather
than assuming the invite landed. The two account emails a VE triggers themselves — a self-service
sign-in link, an email-change confirmation — are unaffected, since suppressing those would break the
only route a VE has back to their own details.

Three properties the page exists to satisfy:

- **No account needed.** A VE has no account here, so a gated opt-out would be no opt-out.
- **It keeps working.** The token is minted once and reused, not re-minted per send — re-minting
  would break the link in every message already delivered, which is exactly what the 30-day rule is
  about. **It is stored in the clear**, a deliberate exception to this codebase's hash-at-rest
  convention: a hash cannot be re-derived, so hashing would force the re-minting the rule forbids.
  What the token grants is correspondingly tiny — stop or resume email to one person, exposing no
  name, address or history.
- **Looking is not consenting.** The link shows state and a button; the change is a POST. A GET that
  unsubscribed on sight would be tripped by every mail client and scanner that prefetches links.

Unsubscribing twice is not an error, and the same link resubscribes.

**Considered and deliberately not added: a physical postal address.** CAN-SPAM requires one in
commercial email, and no team field holds one, so nothing puts it in the footer. Mike's call
(2026-08-16) was that this is not a concern for how these messages are used — a team writing to its
own volunteers about its own sessions, rather than promotional mail to strangers. Recorded here as a
decision rather than a gap so it does not get re-raised as an oversight; it becomes worth revisiting
only if this is ever pointed at a list somebody did not join by volunteering.

## Subscriptions, and why they are a team switch

`Team.VeEmailSubscriptionsEnabled` gates whether VEs on that team may subscribe at all;
`VeTeamMembership.EmailSubscribed` is each VE's answer, per team.

Mike's reason, and the whole point of the switch: one of his teams emails every VE about every
session, and the other does not work that way. A subscribe box on the VE's own details page would,
for that second team, promise notifications nobody sends — and somebody who ticked it would sit
waiting rather than checking the schedule. So the box only appears for teams that actually do it.

Turning the switch off leaves existing answers alone rather than clearing them: it is a decision
about what to offer, and a team that turns it back on should find its volunteers' answers still
there. The message screen gains a **Subscribed only** filter when the switch is on.

**Subscribing is opting in to more; unsubscribing is opting out of all of it, and the unsubscribe
wins wherever both are set.**

## Template audience

Team-defined templates now carry an `EmailTemplateAudience` — candidates or VEs — chosen at creation.
It cannot be inferred, and getting it wrong is visible to the recipient: the two audiences have
different tokens, and the renderer deliberately leaves an unknown one as literal `{{CandidateFirstName}}`
text rather than a silent blank. Each compose screen offers only templates written for its audience,
and the placeholder chips follow the same split.

## Files

| File | |
|---|---|
| `Core/VolunteerExaminers/VeMessageService.cs` | The send, its scoping, and the unsubscribe footer |
| `Core/VolunteerExaminers/VeUnsubscribeService.cs` | Token, resolve, unsubscribe, resubscribe |
| `Core/Email/VolunteerExaminerPlaceholderValues.cs` | `{{VeName}}`, `{{CallSign}}`, `{{TeamName}}`, `{{UnsubscribeUrl}}` |
| `Web/Pages/SessionManager/VeEmail.cshtml(.cs)` | The message screen |
| `Web/Pages/Public/VeUnsubscribe.cshtml(.cs)` | The opt-out page, anonymous and rate-limited |
| `Web/Pages/VeSelfService/Details.cshtml(.cs)` | Per-team subscribe boxes, and the unsubscribed notice |
| `Web/Pages/Admin/TeamSettings.cshtml(.cs)` | The team switch |
