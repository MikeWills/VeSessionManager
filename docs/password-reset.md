# Password reset

**Added 2026-08-01.** Before this, a user who signed in with email + password and forgot it was
**locked out permanently** — there was no forgot-password page, no change-password page, and no
admin reset action. `UserManagementService` could create a user with an initial password, change
role/call sign/teams/manager, and deactivate/reactivate, and that was the entire surface. The only
recovery was editing `AspNetUsers` by hand.

OAuth users (Google/Microsoft) were never affected — their provider owns the credential.

## What already existed

`Program.cs` has always called `.AddDefaultTokenProviders()`, so Identity's
`GeneratePasswordResetTokenAsync`/`ResetPasswordAsync` machinery was present the whole time. This
work is plumbing and UI, not new infrastructure.

## The system sender: why deployment-wide, not per-team

Every other email in this app is candidate-facing and sends from the `Team` that owns the session,
via `Team.ToEmailCredentials()`. A password reset does not fit that shape:

- It is addressed to an **app user**, not a candidate.
- A `SystemAdmin` may belong to **no team at all**, so there is no team credential to reach for.
- A user belonging to several teams would otherwise need an arbitrary tie-break.

So `SystemSettings` gained `SystemSmtpHost/Port/Username/Password/UseStartTls/FromAddress/
FromDisplayName`, edited on Admin → System Settings under "System Email". `SystemSmtpPassword` is
encrypted at rest by `EncryptedStringConverter` under the **same protector purpose** as `Team`'s
credentials, so there is one key ring to back up rather than two.

`SystemSettings.IsSystemEmailConfigured` requires **host *and* username** — "an admin actually
finished setup", not "a hostname is present". This is the same distinction behind the `SmtpHost`
gotcha in CLAUDE.md's Known Constraints, where a shipped `appsettings.json` default made an
unconfigured sender look configured.

Per-team SMTP is untouched and still owns all candidate mail.

**Test mode applies to reset emails too.** `SmtpEmailSender` redirects every send while
`TestModeEnabled` is on, and password reset goes through that same sender — so a reset link on a
test deployment lands in the override inbox, not a real user's.

## Non-disclosure is the design constraint

`RequestResetAsync` returns `Accepted` for *any* syntactically usable address. It sends no mail, and
still reports `Accepted`, when:

- no account has that address;
- the account is **deactivated** — which here means locked out (`LockoutEnd = MaxValue`), since
  there is no `IsActive` flag. A deactivated account must not be recoverable by whoever controls the
  mailbox;
- the account is **OAuth-only** (no password hash). Sending a reset there would let anyone with
  mailbox access *add* a password to an account that deliberately had none, downgrading an
  SSO-protected login to a password login. They still have working OAuth sign-in;
- the SMTP send **throws**. A failure that surfaced differently in the UI would reintroduce the
  oracle this exists to avoid — it is logged server-side and swallowed.

The one case that is *not* hidden is `SystemEmailNotConfigured`: that is a deployment
misconfiguration an admin must see, and it says nothing about any account.

`ForgotPasswordConfirmation` is worded to match — "if that address belongs to an account that signs
in with a password" — because a confirmation page that said "we sent you an email" would leak the
same fact the service is careful not to.

## Throttle

`User.LastPasswordResetRequestedUtc` gates repeat requests to one per
`PasswordResetService.RequestThrottle` (5 minutes). It is stamped **before** the send, not after —
otherwise a slow or failing SMTP server could be driven as a mail-bombing loop. Worst case a failed
send costs the user one throttle window. A successful reset clears it.

A throttled request is still reported as `Accepted`, for the same non-disclosure reason.

## Token lifetime

Identity validates the token's signature, purpose, expiry, and the user's current **security
stamp**. A successful `ResetPasswordAsync` rotates the stamp, which invalidates that token and every
other outstanding one for the user — that is what makes an emailed link effectively single-use.
`Reset_ReusingATokenAfterASuccessfulReset_Fails` pins it.

The token is a bearer credential for the account: it is never logged, and the reset page
round-trips it through a hidden field without ever displaying it.

## Testing note

`PasswordResetServiceTests` registers a small `StampTokenProvider` rather than Identity's real
`DataProtectorTokenProvider`, which lives in a package the test project doesn't reference (adding a
NuGet package needs sign-off per CLAUDE.md). It models the one property the suite depends on — a
token dies when the security stamp rotates — and deliberately nothing else; expiry and signing are
Identity's job.

## Not yet verified live

**No SMTP has ever been configured on any deployment** (see
[issue #181](https://github.com/MikeWills/VeSessionManager/issues/181)), so nothing in this app has
sent a real email — including this. The flow is unit-tested end to end with a fake sender, but the
first real send will be the first real proof. Configure Admin → System Settings → System Email, then
walk one reset through with a throwaway account before relying on it.
