# "Keep me signed in", and signing out other devices

Issue #340. Built 2026-08-13.

## The report

*"Why does my phone constantly log out, but my PC doesn't?"*

Two independent causes, and a phone hits both. A desktop dodges both, which is why it looked like a
phone-specific fault rather than a policy that only ever inconvenienced one kind of device.

**1. Every sign-in produced a session cookie.** All three sign-in paths passed
`isPersistent: false` — password, external-provider-already-linked, external-provider-just-linked —
so the cookie carried no `Expires`, and its lifetime was the lifetime of the browser *process*. A
desktop browser runs for days. iOS and Android kill and restart their browser constantly to reclaim
memory, and every restart discarded the cookie. There was no checkbox, so there was no way to opt
out.

**2. `ExpireTimeSpan` is eight hours, sliding.** Deliberate, from #159, and the reasoning holds: this
is an admin backend holding candidate PII, and the framework default of fourteen days is a long time
for an abandoned session on a shared machine. But it means *any* gap over eight hours signs you out
on any device. Desktop use spans a working day and keeps refreshing it. A phone gets used for two
minutes and put down until tomorrow.

On a phone the session cookie usually died first, and the eight-hour window would have ended it
anyway.

## Why persistence alone was not the fix

This is the part worth remembering, because it looks finished after the first half.

`PasswordSignInAsync` takes an `isPersistent` flag and nothing else. Set it, and the cookie gains an
`Expires` — computed from `ExpireTimeSpan`. Eight hours. So a phone picked up once a day would go
from "logged out every single time" to "logged out daily", which is not what was asked for and is
easy to mistake for success while testing on a machine you use continuously.

The lifetime has to be set explicitly, as `AuthenticationProperties.ExpiresUtc`.
`CookieAuthenticationHandler` only falls back to `ExpireTimeSpan` when the ticket carries no expiry
of its own.

**Sliding expiration keeps working, and slides by the right amount.** The handler renews using the
ticket's own duration (`ExpiresUtc - IssuedUtc`), not `ExpireTimeSpan` — so an active remembered
session is extended by another 30 days rather than being silently shortened to eight hours.

## What shipped

- **A checkbox, default off.** Ticked: `IsPersistent` plus an explicit **30-day** expiry. Unticked:
  a session cookie, byte for byte what the page produced before. The shared-computer behaviour was
  deliberate and #340 only ever asked for a way out of it, not a replacement.
- **`RememberMe`** (`Web/RememberMe.cs`) owns the duration, the properties, and the label. Two
  sign-in paths use it; a difference between them would surface as *"it works with my password but
  not with Google"*, which reads as a broken feature rather than a policy.
- **The label is derived from the duration**, so the page cannot advertise a window the cookie does
  not use.
- **"Sign out other devices"** (`/Account/SignOutOtherDevices`), in the user menu directly above
  Log out.

## Three implementation details that are not obvious

**The password path checks and signs in as two steps.** `PasswordSignInAsync` does both, but takes
only `isPersistent`, so correcting the lifetime afterwards means a *second* `SignInAsync` — and a
second `Set-Cookie` for the same cookie name. Browsers take the last, so it works, but the response
carries two competing values and the first test written against it read the wrong one.
`CheckPasswordSignInAsync` followed by one `SignInAsync` issues exactly one cookie with the right
lifetime the first time. It performs the same `PreSignInCheck` and the same `lockoutOnFailure`
accounting, so failed-attempt handling is unchanged.

**The external buttons live inside the credentials form.** The callback from Google or Microsoft is
a fresh GET with no form state, so the checkbox has to survive the round trip — it rides in
`AuthenticationProperties.Items`, which returns in `ExternalLoginInfo.AuthenticationProperties`. For
that to be populated the checkbox must be *posted* with the provider button, which means one form,
not two. The provider buttons carry `formnovalidate` because the browser would otherwise demand the
empty username and password before allowing a submit that does not use them.

**Both external branches honour it.** Already-linked and just-linked accounts end in different
sign-in calls, and a change applied to one and not the other is exactly the drift this is prone to —
they differ only in how the account was found, and nothing about that should change how long the
session lasts.

## Sign out other devices

The cookie is self-contained; nothing server-side tracks sessions. What ends them is rotating the
user's **security stamp**: `SecurityStampValidator` re-checks the stamp in each cookie against the
database and rejects the principal when they differ. The mechanism already existed — account
deactivation has relied on it since it was built.

Two consequences stated on the page rather than buried:

- **It is not instant.** Revalidation runs on a rolling interval (30 minutes by default), so a
  device that is switched off or idle notices late. "Signed out everywhere" implies immediacy, so
  the page says otherwise in plain words.
- **The current browser stays signed in.** Without re-signing in, the person who clicked the button
  is logged out too, which reads as the action having failed. `RefreshSignInAsync` handles it, the
  same way `ChangePassword` does after its own stamp change.

Audited as `UserSignedOutOtherSessions`. Actor and subject are the same by construction — there is
no admin-facing variant, and a test pins that.

## Testing

The original bug was invisible to every kind of test this repo already had. Sign-in worked, the
session worked, the page rendered — the cookie simply had no `Expires`. Nothing about that is
observable from a page model, a status code, or rendered HTML.

So `RememberMeLoginTests` asserts on the raw `Set-Cookie` header, through a real password sign-in
against `WebApplicationFactory`. **A test that only asked "am I signed in?" would have passed
against the broken version.**

Both directions are pinned: ticked produces an `Expires` about 30 days out, unticked produces a
session cookie with none. Checked by breaking it — reverting to `IsPersistent = false` fails the
first test, and *so does setting a persistent cookie with an eight-hour expiry*, which is the
half-fix described above.

One trap worth recording: **ASP.NET Core writes `expires=` and not `max-age=`.** The first version
of the test asserted on `max-age` and failed against a perfectly correct cookie.

## Still open

Whether the eight-hour `ExpireTimeSpan` is still the right default for an *unticked* session is
untouched here, and deliberately so — #159 chose it for a reason and nothing about this change
disturbs that reasoning.
