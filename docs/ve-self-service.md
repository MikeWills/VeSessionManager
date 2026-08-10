# VE self-service (issue #142)

"Lazy login to their email" — a volunteer examiner maintaining their own contact details without an
account. See [`docs/ve-management.md`](ve-management.md) for the person model behind it.

> **This is the app's first unauthenticated endpoint that reaches personal data.** It was scheduled
> last for that reason, and nearly every decision below is about that rather than about convenience.
> Behind these pages is somebody's home address.

## Why not an Identity user

A VE is a person on a roster, not an account. Giving every VE an `AspNetUsers` row so they can edit a
phone number would put them in the same table as SystemAdmins and make the role model answer questions
it should not have to. Instead there is a **second cookie authentication scheme**, and three
independent barriers keep it apart from the admin app:

| Barrier | What it stops |
|---|---|
| **Scheme** — admin pages authorise against the default (Identity); the VE page names `VeSelfService` explicitly | A VE cookie satisfying an admin page |
| **Cookie path** — scoped to `/VeSelfService` | The browser even *sending* it to an admin route |
| **Claims** — a VE principal has an id and a name and **no role claim** | Every `[Authorize(Roles = ...)]` in the app, which fails closed |

Three, so that no single mistake merges them. Verified live: signed in as SystemAdmin,
`/VeSelfService/Details` bounced to the VE sign-in page.

The VE id comes from the **cookie's own claim**, never a route or form value — the session is the
statement of who this is, and accepting an id from the request would let anyone edit anyone. It uses a
custom claim type rather than `NameIdentifier`, so a VE id and a User id cannot be confused.

## The sign-in link

- **No enumeration.** Every syntactically usable address gets the same answer, whether or not a VE has
  it — *and being throttled looks identical too*, since a distinguishable "slow down" confirms the
  address exists just as surely as "no such VE" would. The one honest failure is a missing SMTP
  sender, which is a deployment fault an admin must see rather than a fact about a person.
- **Only the SHA-256 hash is stored**, so a leaked backup yields nothing presentable.
- **Single use, consumed on arrival** rather than at end of session. An emailed link outlives the
  email; one still working after it has been followed is sitting in an inbox waiting to be found.
- **30-minute life**, 30-minute session, absolute rather than sliding, non-persistent so closing the
  browser ends it — this is a five-minute errand, quite possibly on someone else's machine.
- **Five-minute throttle per VE**, so the endpoint cannot bombard a mailbox or burn SMTP quota.
- Every failure to redeem — expired, already used, never issued — renders **one identical message**.

Sent from the **deployment-wide** SMTP sender, not a team's: a VE may serve several teams, so sending
as one would be arbitrary. Same reasoning and the same sender as password reset.

Links are built from `App:PublicBaseUrl`, never the request Host — the password-reset flow learned
that the hard way (see [`docs/security-hardening-2026-08-03.md`](security-hardening-2026-08-03.md)).

## Rate limiting

`/VeSelfService` joins `/Account` in the **global** limiter, and it is the more exposed of the two:
reachable with no account, sends email on request, and a home address sits behind it. Adding the path
to that predicate is what protects it — the pages carry no per-page attribute deliberately, so a new
page under either prefix is covered the moment it exists rather than when someone remembers.

## Changing their own email

Decided with Mike, 2026-08-07: **confirmation goes to the address already on file**, then the new one
takes effect.

That is the whole security of it. This field is not just contact detail — it is the credential for
sign-in. Applying a change on the strength of the session that requested it would make one leaked link
**permanent takeover**: whoever held it points the address at themselves and every future link
follows. Requiring the current mailbox to approve caps a stolen link at a single session.

**The confirmation email names the new address.** Old-address approval authorises the change; showing
what it will become is what catches a typo, which would otherwise send every future link somewhere
unreadable and leave an admin as the only way back.

Four more guards:

- An address **already belonging to another VE** is refused — and **re-checked at confirmation**, not
  only at request. Sign-in resolves an address to one person, and the link is valid for a day: plenty
  of time for someone else to take it.
- A **second request supersedes the first**. Two live links pointing at different addresses, last
  click wins, is not a race worth having.
- Single use, hashed, 24-hour life, throttled. Longer-lived than the sign-in link because the person
  who must act is reading their *old* mailbox and may not be watching it.
- A VE with **no current address** cannot self-serve a change: nothing to confirm against, and
  applying it anyway is exactly the unconfirmed path this prevents.

The confirm page is **anonymous, and that is required rather than lax** — the link is opened from the
mailbox they already had, quite possibly on another device, so demanding the session would make the
flow unusable for exactly the person it protects.

The audit entry has a **null `UserId`** on purpose: the VE did this, not an admin, and inventing an
acting user would make the trail say something untrue.

## What a VE may change

Their contact details and their email. **Not** their tags, their accreditations, or the admin-facing
notes — those belong to the team, and the notes they should not even see.

That is a separate `UpdateOwnContactDetailsAsync` rather than reuse of the admin call, so a wider
field set cannot leak in later when someone extends the shared method.

## An admin can also set the email — corrected 2026-08-07

It was originally locked on the admin page too, on the grounds that it is the sign-in credential. That
was wrong, and using the feature for real proved it: **no VE had an email and there was no supported
way to give one**, so nobody could ever start self-service. An admin already has full write access to
the person; refusing them one field was theatre.

What needed protecting was the *VE* changing it unconfirmed, which the flow above handles. An admin's
change is a different act by a different party, uses the same uniqueness rule, and is **called out
specifically in the audit entry** rather than folded into "details updated".

## Not verified

The email → link → edit journey has never been run end to end against a live SMTP server. The routes
respond correctly, the auth boundary is verified live, and the service has 23 tests — but the actual
delivery has not happened.
