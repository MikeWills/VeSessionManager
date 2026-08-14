# Two-factor authentication (TOTP)

Built 2026-08-14 for issue #356, which was split out of the #312 hygiene sweep because it is a
feature, not a fix. The infrastructure was already half there — `AddDefaultTokenProviders()` and
`AddIdentityCookies()` had been registered since Phase 9a, supplying the authenticator token provider
and the two two-factor cookie schemes, entirely unused.

## The shape of the decision

**Opt-in for everyone. Enforced for nobody. Loudly recommended for admins.**

Enforcement was considered and deliberately not built. On this deployment system SMTP has
historically not been configured, so an admin who loses a phone cannot necessarily be emailed a way
back in — and the account that would rescue them is the one that would be locked. The nudge
(`_TwoFactorSuggestionBanner`, shown to SystemAdmin and TeamAdmin only) is not dismissible, because a
dismissed nudge stops working the moment it is inconvenient and the only way to remove it — turning
2FA on — is the point.

Restricting the banner to admin roles is deliberate too: shown to everyone it becomes wallpaper, and
wallpaper is not a nudge.

## The sign-in flow, and why it is hand-rolled

```
password POST ──> correct? ──no──> audited SignInFailed, generic error
                     │
                    yes
                     │
        2FA on AND device not trusted?
              │                    │
             no                   yes
              │                    │
     app cookie issued      TwoFactorUserId cookie (10 min)
     (unchanged, #340)      redirect to /Account/TwoFactorChallenge
                                     │
                          code or recovery code
                                     │
                            app cookie issued here
```

**No application cookie exists until the challenge is passed.** That is the property the whole
feature rests on, and the easy thing to get wrong while everything still looks right in a browser —
`CorrectPasswordAlone_IssuesNoSession_AndRedirectsToTheChallenge` is the test that pins it.

**Why not `PasswordSignInAsync`.** Identity's own flow stores the pending-user state for you, but only
from `PasswordSignInAsync` — which this app deliberately does not use. #340 split password-check from
sign-in precisely so the application cookie could be issued exactly once, with an explicit 30-day
lifetime that `isPersistent` cannot express. Going back would reintroduce the double `Set-Cookie`
that fix exists to prevent, and calling it purely to establish the pending cookie would verify the
password a second time.

So `TwoFactorSignIn` writes that cookie itself. It carries the user id in a `ClaimTypes.Name` claim
on the `TwoFactorUserIdScheme` — Identity's own internal shape, which
`GetTwoFactorAuthenticationUserAsync` reads back. **That is behaviour, not documentation**, so
`TheChallengePageFindsThePendingUser` asserts the round trip rather than trusting it. Same standard
applied to MimeKit's header handling in #261.

The pending cookie is not a session: nothing in the app authorises against that scheme, so holding
one grants access to the challenge page and nothing else. It expires in ten minutes.

## Details that are load-bearing

- **Failed codes count against Identity's lockout.** Otherwise six digits is a million guesses for
  whoever already holds the password, with nothing counting. The per-IP limiter on `/Account` bounds
  the rate on top.
- **A recovery code never earns device trust.** Someone using one has *lost* their authenticator, so
  trusting that device for 30 days would mean a stolen recovery code buys a month of unchallenged
  access. Mutation-tested.
- **The two kinds of code are normalized differently, and conflating them is a real bug** — caught by
  a test here. A TOTP code is six digits, so stripping spaces and hyphens only ever helps; an Identity
  **recovery code contains a hyphen**, and stripping it makes a correctly-copied code fail to redeem.
- **Enrolment does not enable 2FA until a code verifies.** Enabling first is the version that locks
  people out: a mistyped key, or a phone with a drifted clock, and the account now demands a code
  nobody can produce. Recovery codes are issued in the same action, so the window between "2FA is on"
  and "I have a way back in" does not exist.
- **Disabling resets the authenticator key**, so re-enabling cannot silently reuse a secret that has
  been sitting unused — and any app still holding the old one stops working, which is what "I turned
  this off" should mean.

## Device trust and "sign out other devices"

Trust lasts 30 days, matched to `RememberMe.Duration` rather than the 8-hour `ExpireTimeSpan`. An
8-hour trust window would fight "keep me signed in": a 30-day session asking for a code every working
day is the friction that makes people turn the feature off.

**"Sign out other devices" covers trusted devices without doing anything extra.** Identity registers
its security-stamp validator on the two-factor remember-me cookie as well as the application cookie
([docs](https://learn.microsoft.com/aspnet/core/security/authentication/identity-configuration)), so
rotating the stamp invalidates other devices' trust on their next revalidation — the same
up-to-30-minutes window that page already warns about.

`ForgetTwoFactorClientAsync()` is deliberately **not** called there: it clears *this* browser's trust,
which is the one device the button is not about. The person who clicked stays signed in on purpose
(`RefreshSignInAsync`), and challenging them on their own machine afterwards would be a surprise
rather than a security gain. That was written the wrong way first.

## The lost-phone escape hatch

Two layers:

1. **Recovery codes** — ten, shown once, hashed by Identity so no page can ever show them again.
2. **An admin can clear another user's 2FA** (`UserManagementService.ClearTwoFactorAsync`, offered on
   the Users row menu only when it would do something).

The second is unavoidably a way to remove someone else's second factor. It is gated by the same
`AuthorizeManageAsync` as every other action on that page, which is the right bar: an admin who can
reach that row can already reset the account's password, so it grants no reach they did not have. It
audits loudly as `TwoFactorClearedByAdmin`.

## External logins

`ExternalLoginSignInAsync` keeps `bypassTwoFactor: true`, and that is now a documented decision rather
than something that reads like an oversight. Google and Microsoft enforce their own second factor, so
this app's challenge would stack a factor it does not control on one it cannot verify.

**Untestable today either way**: SSO is not configured on this deployment (#185), so flipping it would
ship an unexercised change to the one sign-in path nobody can currently try.

## Testing

`TwoFactorAuthTests` drives the real pages, because the properties that matter are about the HTTP
response — whether an application cookie was issued, and where the browser is sent. A page-model test
can see neither.

One thing worth knowing before writing more of these: **`GenerateTwoFactorTokenAsync(user,
"Authenticator")` returns an empty string.** Identity's authenticator provider deliberately cannot
generate — only the phone can. It reads exactly like the method you want and produced a test that
failed against completely correct code. `Totp.Generate` computes the code the way a phone would, from
the same stored base32 secret, so these tests exercise the real validation path.

## Not built

- **Enforcement.** See the top; revisit if system SMTP is configured, which makes emailed recovery
  viable and changes the lockout calculus.
- **WebAuthn / passkeys.** A larger question than a second factor, and a different one.
- **Per-team policy.** Two-factor is an account property, not a team one, and this deployment's teams
  cooperate rather than being unrelated tenants.
