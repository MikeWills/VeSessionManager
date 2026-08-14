# Admin Config Screens (Phase 9c)

The SystemAdmin/TeamAdmin split the spec flagged as undecided ("Needs a real split when this phase
is actually built") was resolved as **one shared Razor Pages set under `Pages/Admin/`, not separate
SystemAdmin/TeamAdmin folders** — SystemAdmin gets a team-picker (`?teamId=`), TeamAdmin is silently
locked to their own team regardless of what's in that query string.

Every page follows the exact Phase 9b pattern: `[Authorize(Roles=...)]` + an independent
`AuthorizeAsync()`-style re-check per POST handler (defense in depth against a tampered `?teamId=`,
same reasoning as Phase 9b's session-id re-check), business logic in a Core service under
`VeSessionManager.Core/Admin/` (`TeamSettingsService`/`VecManagementService`/
`FeeConfigurationService`/`EmailTemplateAdminService`/`UserManagementService`/`SystemSettingsService`,
each with its own result enum + `AddAudit` helper + test file), pages as thin wiring.

## New authorization class

`Authorization/AdminAccessScope` — `SessionAccessScope`'s own doc comment had already flagged this
as needed; it wraps `SessionAccessScope` for team-resolution and adds
`CanManageTeam`/`CanManageUser`/`CanAssignRole`/`ScopeAuditLog`/`ScopeJobRunHistory`.

## New singleton-row entity: SystemSettings

`SystemSettings` (`Id` always 1, seeded via the `Phase9cSystemSettings` migration's own `InsertData`
— same idiom as `Phase6_5MultiTeamFoundation`'s seeded `Team` row, no separate seeder class needed)
holds deployment-wide values:

- `PiiRetentionWindowDays` (nullable, seeded `NULL` — spec.md is explicit "no default is assumed")
- ULS polling settings — `UlsWatcherIntervalHours`/`UlsWatcherStartHourEt`, seeded from the
  `Jobs:*` defaults in `src/Shared/appsettings.Shared.json`. Read through `JobSchedules`, which both
  hosts share, so the Worker schedules from the same values Web reports on the Job Schedule screen.
  **Three jobs move together when these change** — `UlsWatcher`, `LicenseWatch` and `VeLicenseWatch`
  all read this one pair (see `docs/job-schedule.md`), which is deliberate: they read the same FCC
  data through the same mirror, and splitting their schedules once meant a renewal sat unseen for
  most of a day.
- Later gained `SessionIngestionIntervalMinutes` (see `docs/candidate-refresh.md`),
  `TestModeEnabled`/`TestModeOverrideEmail` (see `docs/test-mode.md`), and the seven `SystemSmtp*`
  fields behind the System Email screen — the deployment-wide sender used for password reset and
  other account mail, distinct from each team's own candidate-facing SMTP credentials.

> **Superseded (2026-07-31).** This section originally described four settings —
> `FccDailyWatcherIntervalHours`, `FccDailyWatcherStartHourEt`, `FccWeeklyCatchupIntervalHours`,
> `FccWeeklyCatchupDayOfWeek` — belonging to the FCC bulk-file watcher. That subsystem was replaced
> by the ExamTools ULS mirror (`docs/uls-watcher.md`); the columns survive only in the
> `Phase9cSystemSettings` migration, which is why they still turn up in a search.

## FeeConfiguration: real CRUD for the first time

`FeeConfigurationService.UpdateAsync` blocks editing any row a `Session` already references
(`InUse` result) since `Session.FeeConfiguration` is a live navigation, not a snapshot copy, and
editing an in-use row would retroactively change that session's fee data; the correct flow for "the
fee changed" is always `CreateAsync` a new dated row — this is the concrete mechanism behind the
spec's "a new FeeConfiguration doesn't retroactively change past sessions" test requirement.

## User deactivation

Reuses ASP.NET Core Identity's existing `LockoutEnd`/`LockoutEnabled` (`LockoutEnd =
DateTimeOffset.MaxValue` to deactivate, `null` to reactivate) rather than a new column —
`SignInManager.PasswordSignInAsync(..., lockoutOnFailure: true)` already enforces it on both the
password and external-login paths via its own `PreSignInCheck`. `DeactivateAsync` also rejects
acting on your own account (`CannotDeactivateSelf`) since there's no invite/reset-email flow to
recover a self-lockout.

## Email template editing

Edit-only (no create/delete — the set of `Key`s is fixed by what `CandidateNotificationService`/
`PaymentReminderService` actually look up) with the `EmailTemplatePlaceholders` registry
(`VeSessionManager.Core/Email/`) hand-collected from those services' real send-time
`Dictionary<string,string>` literals (not the seeded template body text) — surfaced as chips on the
edit page; a dedicated test (`EmailTemplateAdminServiceTests`) edits a template then calls
`EmailTemplateRenderer.RenderAsync` directly in the same test to prove the "next send, no deploy
needed" guarantee end-to-end.

Also added `IX_Teams_Name`/`IX_Vecs_Name` uniqueness constraints since real "Create Team"/"Create
VEC" screens exist for the first time.

## Known, accepted limitation

`AuditLog`/`JobRunHistory` got read-only viewer pages scoped via `AdminAccessScope`'s two new
`Scope*` methods. `AuditLog` has no `TeamId` column, so a TeamAdmin's audit view resolves via
"actions performed by a user on my team," which misses background-job entries (`UserId == null`)
even when they're about that team; fully fixing this means adding `AuditLog.TeamId` and populating
it at every existing `AddAudit` call site across the whole app, deliberately deferred rather than
done piecemeal here.

## Live-verified

Real browser click-through as both `sysadmin@example.com` (create VEC, edit System Settings,
deactivate/reactivate a user, confirm audit rows for every action) and `teamadmin@example.com`
(confirmed Teams/VECs/System Settings are `[Authorize]`-blocked with "Access denied," confirmed the
Users page shows only that team's SessionManager/TeamLead rows, confirmed a tampered `?teamId=` on
TeamSettings is silently ignored in favor of the account's own team).
