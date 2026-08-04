# Public-internet hardening pass — 2026-08-03

Tier 1 of the 2026-08-03 six-agent audit (`docs/audit-2026-08-03-tasks.md`): the findings that are
**directly exploitable by anyone on the internet** once this deployment is publicly reachable, as
opposed to ones that need an authenticated session or filesystem access. Five changes, all in the
Web project.

The exposed surface this pass is scoped to is small and worth naming, because it is the whole
attack surface an anonymous visitor has: `/Account/Login`, `/Account/ForgotPassword`,
`/Account/ResetPassword`, `/youth-confirm/{token}`, `/webhooks/square/{teamId}`, the OAuth callback,
`/Privacy`, and `/Error`.

## 1. Rate limiting on `/Account/*` (audit T14)

`Program.cs`. There was none anywhere in the app. Two distinct abuses:

- **Login.** Identity's lockout (5 attempts) does not protect the account here — it *is* the attack.
  Five deliberate wrong passwords against a known SystemAdmin address locks that account on a
  rolling 5-minute basis, repeatable indefinitely, by an unauthenticated stranger.
- **Forgot password.** `PasswordResetService` throttles per *user*, which does nothing against
  breadth: a script with 10,000 addresses still produces one real SMTP send per address that
  exists, burning the deployment's mail quota and its sending-domain reputation.

Implemented as a **global limiter with an explicit no-limiter partition** for everything outside
`/Account`, rather than `[EnableRateLimiting]` per page, so the protection is on by default for any
future page added under `/Account` — the direction the mistake should fall. 20 requests/minute per
IP: a human login is one GET plus one POST, so this is far above real use and far below useful
brute force. Static assets live outside `/Account` and are untouched.

**This depends on `UseForwardedHeaders`** (below). Without it every request behind the Apache proxy
carries the proxy's own loopback address, and the entire internet shares one 20/minute bucket —
which would be a self-inflicted denial of service rather than a protection.

## 2. `UseForwardedHeaders` (prerequisite, not previously present)

`Program.cs`, first in the pipeline. Production runs behind Apache on the same box
(`docs/deployment.md`), so without this the app sees loopback as the client IP and Kestrel's own
plain-HTTP scheme. Defaults trust only loopback proxies, which is exactly this topology, so no
configuration is needed. In Development there are no `X-Forwarded-*` headers and this is a no-op.

Beyond the rate limiter, this also makes `Request.Scheme` correct behind TLS termination.

## 3. Security response headers (audit T13)

`Program.cs`, a small middleware before `UseRouting`. There were **none** — no CSP, no
`X-Content-Type-Options`, no framing protection, no `Referrer-Policy`.

The load-bearing one is `frame-ancestors 'none'` / `X-Frame-Options: DENY`: without it an attacker
can frame an authenticated page and overlay a decoy over a destructive control, clickjacking a
signed-in Session Manager into deleting a candidate. The CSP is defence in depth — with it, a future
encoding slip is contained instead of escalating to a session-stealing XSS.

Two allowances are deliberate and were **verified against the actual markup, not assumed**:

- `style-src 'unsafe-inline' https://fonts.googleapis.com` — both layouts load Google Fonts, and
  there are ~139 inline `style=""` attributes across the pages. A stricter `style-src` would have
  silently destroyed the site's typography and layout. Removing the inline styles is the
  prerequisite for tightening this, not something to do blind.
- `font-src https://fonts.gstatic.com` — what the Google Fonts stylesheet itself then pulls.

`script-src` stays `'self'`: there is no inline JavaScript anywhere under `Pages/` (verified by
grep; the only non-`src` `<script>` is an empty importmap in the otherwise-unused Bootstrap layout).
`form-action` lists Square because the youth-rate flow hands off to Square-hosted checkout — today
that is a server-issued redirect, so `'self'` alone would also pass, but this stays correct if it
ever becomes a direct cross-origin post.

## 4. Password-reset links no longer read the request host (audit T17)

`Pages/Account/ForgotPassword.cshtml.cs` + `appsettings.Production.json`.

The reset URL was built with `Url.Page(..., protocol: Request.Scheme)`, which emits the **Host header
the request supplied**. With the framework default `AllowedHosts: "*"`, an attacker could request a
reset for a known SystemAdmin address with a forged `Host`, and the victim would receive a genuine,
correctly-signed email whose link points at the attacker's server — handing over a valid single-use
reset token for an admin account. Whether the forged header reached the app depended entirely on the
Apache vhost configuration; the app itself offered no defence.

Two independent fixes, deliberately both:

- The link is now built from the configured `App:PublicBaseUrl`, so it cannot read the request host
  at all, regardless of what any proxy forwards. This is the same source the Worker already uses for
  the youth-confirmation link, so every absolute URL this deployment emits now agrees on one host.
- `AllowedHosts` is pinned in `appsettings.Production.json`.

> **Deployment note.** `AllowedHosts` is pinned to `ve.wx0mik.radio`. A deployment served under any
> other hostname — a beta box, a staging name, an IP — will return **400 Bad Request** for every
> request until this value (and `App:PublicBaseUrl` beside it) is updated to match. Both accept a
> semicolon-separated list if one deployment answers to several names.

## 5. Youth-rate attestation is now enforced server-side (audit T06)

`Pages/Public/YouthConfirm.cshtml.cs`. `[Required]` on a non-nullable `bool` is a **client-side-only**
guard: jQuery unobtrusive validation reads it as "must be checked", but server-side it always
passes, because the checkbox tag helper posts a hidden `false` and any bound value satisfies
`Required` for a value type. So `ModelState.IsValid` was always true for that field, and a
JS-disabled browser or a direct POST could claim the reduced youth rate without ever making the
attestation the honor system depends on — on an anonymous page reachable by anyone holding the
token. (The client-side guard was itself inoperative for an unrelated reason — see audit T09, jQuery
is never loaded under `_PublicLayout`.)

`[Required]` is kept for the browser experience once T09 is fixed; the authoritative check is now an
explicit `if (!Input.ConfirmYouth)` in `OnPostAsync`, with comments on both so nobody removes one
believing the other covers it.

## 6. Square webhook body size cap (audit L-2)

`SquareWebhookEndpoint.cs`. This is the only unauthenticated endpoint that buffers its entire body
into a string, and it must do so *before* the signature can be verified — so it was bounded only by
Kestrel's 30MB default, letting anyone force repeated large-object-heap allocations at whatever rate
they could open connections. Capped at 64KB (payloads are a few KB).

Both halves are needed: the `Content-Length` check rejects a declared oversize body without reading
it, and lowering `MaxRequestBodySize` makes the read itself throw for a chunked body that omits or
lies about its length.

## Verification

Built clean (0 warnings), full suite green (556 tests), and each change exercised against a running
instance rather than assumed:

| Check | Result |
|---|---|
| All four security headers on `/Account/Login` | present, CSP exactly as authored |
| 25 rapid requests to `/Account/Login` | 19 × 200 then 6 × 429 (the 20th was consumed by the header check in the same window) |
| 30 rapid requests to `/Privacy` | 30 × 200 — the no-limiter partition works |
| 100KB POST to the webhook | 413 |
| Small unsigned POST to the webhook | 401 (invalid signature) — the cap doesn't disturb the normal path |
| `/`, `/Privacy`, `/Account/ForgotPassword`, `/Account/Login` | 200, unbroken under the new CSP |

**Not runtime-verified:** the youth-confirm server-side check (§5) and the reset-link host change
(§4). Both need state this environment doesn't have — a valid youth payment token, and a configured
SMTP sender (no deployment has ever had one, per `docs/password-reset.md`). Both are short,
unconditional code paths reviewed directly; neither has automated coverage, because the Web project
has no test project (tests are Core-only).

## Deliberately not in this pass

Still open from the audit, in rough priority: the P0/P1 remainder (`T02` FirstName purge, `T04` Web
prod config, `T08` payment double-create index, `T12` key-ring location, `T10`/`T11` Worker
resilience), then the lower-severity web items — `T15` fallback authorization policy, `T16` cookie
lifetime, `T09` jQuery, `T05` STARTTLS.

Also outside what any of this can address: the audit read **code only**. The reverse-proxy config,
TLS setup, firewall, and the ownership/permissions on the DB and key-ring files are server-side
concerns that still want their own review before the app faces the public internet.
