# Deployment-Wide Email Test Mode (2026-07-21)

`SystemSettings.TestModeEnabled`/`TestModeOverrideEmail` (migration `TestMode`) — deployment-wide,
not per-Team, an explicit user decision. The alternative (a per-Team toggle living on `Team`
alongside its SMTP credentials) was rejected as more than needed while only one team is actually
live.

## How it works

`SmtpEmailSender` is the single enforcement point: it reads `SystemSettings` fresh on every send (no
caching, same as every other admin-editable setting) and, when test mode is on, redirects via
`TestModeEmailRedirector.Apply` — every real send this app makes (registration confirmations,
reminders, disclosure/youth-program instructions, `PaymentExpirationNotice` to
`EmailSettings.AdminNotificationEmail`, everything, for every team) goes to `TestModeOverrideEmail`
instead, with the original recipient noted (HTML-encoded) in the redirected body and `[TEST MODE]`
prefixed on the subject. No calling service needs to know test mode exists — it's fully encapsulated
below `IEmailSender`.

`SystemSettingsService.UpdateAsync` rejects turning test mode on without an override address
(`TestModeMissingOverrideEmail`) — a missing address would otherwise silently drop every email
instead of redirecting it.

## Banner

A red banner (`Pages/Shared/_TestModeBanner.cshtml`, queries `SystemSettings` fresh per request, no
caching) is wired into all three of this app's layouts — `_AppLayout` (SessionManager/Admin),
`_PublicLayout` (Login/Privacy/AccessDenied/Logout/ExternalLoginCallback), and the near-vestigial
scaffold `_Layout` — so it shows even to an unauthenticated visitor on the Login page, not just
after signing in.

See CLAUDE.md's Known Constraints for the Razor runtime-compilation gotcha hit while building this
banner (editing a `.cshtml` file needs a `dotnet run` restart to take effect).
