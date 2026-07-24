# Admin Backend Auth (Phase 9a)

What the auth/scaffolding slice (`VeSessionManager.Web`'s Identity wiring +
`VeSessionManager.Core/Authorization/SessionAccessScope.cs`) does and why.

## Role model: four roles, not the spec's original three

`docs/spec.md`'s Phase 9 originally described three roles (Admin/SessionManager/TeamLead). During
Phase 9a the user asked for a real 4-role hierarchy instead, since a single "Admin" role didn't fit
well once the multi-team foundation (Phase 6.5) gave each `Team` its own credentials/settings:

- **SystemAdmin** (renamed from the spec's "Admin"): sees/edits everything across every team,
  creates `Team` rows, grants the TeamAdmin role. Kept as a full superset — provisioning duties are
  additive, not a narrowing of the old Admin role's scope.
- **TeamAdmin** (new): controls all settings within their own team, and is a superset of
  SessionManager (same session/candidate actions) *within that team* — plus grants
  SessionManager/TeamLead to users within their team.
- **SessionManager** (unchanged): full visibility/edit on their own team's sessions.
- **TeamLead** (unchanged): scoped to whichever sessions their assigned manager can see, read-only.

`UserRole` (`VeSessionManager.Core/Entities/Enums.cs`) is `{ SystemAdmin, TeamAdmin,
SessionManager, TeamLead }`. Deliberately **not** using ASP.NET Core Identity's own Role tables
(`AspNetRoles`/`AspNetUserRoles`) — Role stays one plain enum column on `User`, matching every
other "pick one of N" field in this codebase. `AppDbContext` uses `IdentityUserContext<User, int>`
(not `IdentityDbContext`) for exactly this reason — it gives `Users`/`UserClaims`/`UserLogins`/
`UserTokens` (external logins need `UserLogins`) without the unused Role tables.

## Team scoping: reconciling "own sessions" with multi-team

The spec's "Session Manager sees their own sessions" / "Team Lead scoped to sessions they're
assigned to" predates the multi-team foundation and was never reconciled with it — the spec itself
flagged Team Lead's scope as needing confirmation before building. Resolved:

- `User.TeamId` (nullable int) — null for SystemAdmin (deployment-wide); set for
  TeamAdmin/SessionManager (their own team).
- `User.ManagedByUserId` (already existed) — a TeamLead's assigned manager. **Role-agnostic**: the
  manager can be a SessionManager *or* a TeamAdmin, so a TeamLead's effective team is always
  `user.ManagedByUser?.TeamId`, whoever that manager is. No new multi-manager join table yet (a
  TeamLead has exactly one assigned manager) — deferred until there's a real need.

`SessionAccessScope` (`VeSessionManager.Core/Authorization/SessionAccessScope.cs`) is the actual
mechanism, plain C# with no ASP.NET dependency so it's directly unit-tested
(`SessionAccessScopeTests.cs`) rather than requiring a web host:

- `GetEffectiveTeamId(User)` — SystemAdmin → `null` (no filter); TeamAdmin/SessionManager →
  `user.TeamId`; TeamLead → `user.ManagedByUser?.TeamId`.
- `Scope(IQueryable<Session>, User)` — SystemAdmin gets the query back unfiltered; everyone else is
  `.Where(s => s.TeamId == effectiveTeamId)`. An unassigned TeamAdmin/SessionManager
  (`TeamId = null`) or an unassigned TeamLead correctly sees **nothing**, not everything.
- `CanEdit(User, Session)` — SystemAdmin/TeamAdmin/SessionManager (own team) → true; TeamLead →
  false (read-only, pending explicit sign-off on any TeamLead write access).

TeamAdmin and SessionManager are **equivalent** in `SessionAccessScope` — both resolve to
`user.TeamId`. The only difference between those two roles is settings/user-management access
(who can edit Team credentials, who can grant SessionManager/TeamLead), a separate authorization
surface Phase 9c will build, not this class's concern.

This is real infrastructure for Phase 9b to consume once an actual session-list page exists — 9a
builds and tests it now, per the spec's own framing ("build the *mechanism* ... even though
there's barely any real data to filter yet"); it isn't wired into a real data page this phase.

## Identity setup

`User` (`VeSessionManager.Core/Entities/User.cs`) is `IdentityUser<int>` — inherits `UserName`/
`Email` (now nullable, superseding the old `required string Email`)/`PasswordHash`/
`SecurityStamp`/etc. Adds `Name` (required display name), `Role`, `TeamId`/`Team`,
`ManagedByUserId`/`ManagedByUser`.

`VeSessionManager.Web`'s `Program.cs` uses `AddIdentityCore<User>()`, not `AddIdentity<User,
TRole>()` — deliberately skips Identity's Role system (see above). `AddIdentityCookies()` supplies
the `ApplicationScheme`/`ExternalScheme` cookie schemes that `AddIdentity` would otherwise add for
you automatically; with `AddIdentityCore` you wire that up explicitly. `app.UseAuthentication()`
was **missing entirely** before this phase — `app.UseAuthorization()` alone never populated
`HttpContext.User`, so authorization had been a silent no-op since Phase 0's scaffold.

A custom `AppClaimsPrincipalFactory` (`VeSessionManager.Web/AppClaimsPrincipalFactory.cs`) adds a
`ClaimTypes.Role` claim from `user.Role` and a `TeamId` claim at sign-in, so `[Authorize(Roles =
"...")]` and future authorization logic read straight from the signed-in cookie's claims — no
extra DB hit per request.

Pages are hand-built (`Pages/Account/Login.cshtml`, `Logout.cshtml`, `AccessDenied.cshtml`,
`ExternalLoginCallback.cshtml`), not the scaffolded Identity UI Razor Class Library — keeps this
consistent with the project's existing hand-built `Pages/` scaffold and avoids pulling in a whole
prebuilt page set this app doesn't need.

**No self-service registration.** `ExternalLoginCallback.cshtml.cs` only completes a Google/
Microsoft sign-in if it's already linked to a local account, or its email matches an existing
account it can link to. A brand-new email with no matching local `User` row is rejected with "no
account found, contact your administrator" — accounts are provisioned by an admin (today only via
`DevDataSeeder`/direct DB edit; Phase 9c's admin UI will do this properly), never auto-created from
an OAuth callback.

## Sign-in methods: username/password + Google + Microsoft — Apple decided against

The spec called for username/password + Google + Microsoft + Apple, but explicitly flagged Apple as
needing a cost tradeoff confirmed first ($99/year Apple Developer account, `.p8`-key JWT
client-secret setup). Phase 9a (2026-07-21) built the first three and deferred the Apple decision;
**the user decided 2026-07-22 that Apple Sign-In isn't worth the cost — not just deferred, skipped
outright.** No code exists for it (username/password + Google + Microsoft is the real, final sign-in
set), so there's nothing to remove — this is purely closing out the open decision.

Google/Microsoft are registered **conditionally** in `Program.cs` — the same optional-integration
pattern as every other external credential in this app (Zoom/Discord/Square/Email): no
`Authentication:Google:ClientId`/`Authentication:Microsoft:ClientId` yet just means that sign-in
button doesn't render on the login page, never a startup failure.

```bash
dotnet user-secrets set "Authentication:Google:ClientId" "<Google OAuth client id>" --project src/VeSessionManager.Web
dotnet user-secrets set "Authentication:Google:ClientSecret" "<Google OAuth client secret>" --project src/VeSessionManager.Web
dotnet user-secrets set "Authentication:Microsoft:ClientId" "<Entra app registration client id>" --project src/VeSessionManager.Web
dotnet user-secrets set "Authentication:Microsoft:ClientSecret" "<Entra app registration client secret>" --project src/VeSessionManager.Web
```

On the server: `Authentication__Google__ClientId` / `Authentication__Google__ClientSecret` /
`Authentication__Microsoft__ClientId` / `Authentication__Microsoft__ClientSecret` environment
variables.

## Dev seeding: four test users

`DevAuthSeeder` (`VeSessionManager.Web/DevAuthSeeder.cs`), Development-only, runs from Web's
`Program.cs` startup (not the Worker's `DevDataSeeder`, since `UserManager<User>` — needed to hash
passwords — is naturally a Web-hosted service). Seeds one user per role:

| Email | Role | Team |
|---|---|---|
| `sysadmin@example.com` | SystemAdmin | none (deployment-wide) |
| `teamadmin@example.com` | TeamAdmin | the seeded Team (Id 1) |
| `sessionmanager@example.com` | SessionManager | the seeded Team (Id 1) |
| `teamlead@example.com` | TeamLead | the seeded Team (Id 1), `ManagedByUserId` = the SessionManager |

All four share the password `Dev-Password1!` — Development-only, not a real secret, safe to commit
in source (this is exactly why it's documented here instead of in user-secrets).

**Guard gotcha, already hit once:** the seeding guard checks specifically for
`sessionmanager@example.com`, not "does any `User` row already exist" — the Worker's own
`DevDataSeeder` creates a "System" user (for `CreatedByUserId` audit trails) sharing this same
`AspNetUsers` table, which would otherwise make an "any user exists" guard skip before ever
seeding the four role test users.

## Migration

`Phase9aIdentityAuth`: renames `Users` → `AspNetUsers`, adds every Identity column (`UserName`,
`PasswordHash`, `SecurityStamp`, etc. — all with EF Core Identity's own defaults), adds
`AspNetUserClaims`/`AspNetUserLogins`/`AspNetUserTokens` tables, adds `Users.TeamId`/FK. `Email`
becomes nullable (`AlterColumn`, not drop+recreate — existing data preserved). No committed
migration-seeded `User` rows existed before this, so there was nothing to backfill for the new
columns beyond ordinary EF Core Identity defaults.

## Live-verified

Ran the Web app, confirmed the migration applied cleanly, confirmed the four dev users seeded,
then a real browser click-through: logged in as `sessionmanager@example.com`, landed on
`/SessionManager`; navigated to `/SystemAdmin` and got redirected to `/Account/AccessDenied`
(correctly blocked); logged out, logged in as `teamlead@example.com`, landed on `/TeamLead`. Not
yet live-tested: Google/Microsoft sign-in (no real OAuth app credentials configured yet — see
`TODO.md`).

## TeamLead read-only view (added 2026-07-22)

`Pages/TeamLead/Index.cshtml` above was only ever a placeholder — TeamLead had no real view of
session/candidate data until this addition, tracked as a known gap since Phase 9d's self-audit.

`SessionAccessScope` gained a `CanView` method distinct from the pre-existing `CanEdit` — `CanEdit`
still returns `false` for TeamLead unconditionally (write actions stay off-limits), but `CanView`
doesn't carve TeamLead out, so it can gate page *display* separately from write actions.
`Pages/SessionManager/Index.cshtml.cs`/`Detail.cshtml.cs`/`VeRoster.cshtml.cs`/
`VecSubmission.cshtml.cs` all added `TeamLead` to `[Authorize]`; `Detail.cshtml.cs`'s page-load gate
switched from `CanEdit` to `CanView` (it had been reusing the write-gate to decide visibility, which
is why TeamLead access needed more than just the role attribute), and a new `CanEdit` property on
the page model lets `Detail.cshtml`/`VecSubmission.cshtml` hide every write control (buttons, forms,
kebab menu, modals) instead of showing a TeamLead dead controls that would 403. `RoleLandingPages`
now sends TeamLead to `/SessionManager/Index` like every other role; the old
`Pages/TeamLead/Index.cshtml` placeholder was deleted.

**A second, previously-latent bug was found and fixed in the same pass:**
`SessionAccessScope.GetEffectiveTeamId`'s TeamLead branch reads `user.ManagedByUser?.TeamId`, which
requires that navigation eager-loaded — but `UserManager.GetUserAsync(ClaimsPrincipal)` (the pattern
every page used) never loads it, and since no page had ever actually exercised the TeamLead path
before this fix, nothing had caught it: a TeamLead would sign in successfully and silently see zero
sessions regardless of their real team assignment. Fixed with `CurrentUserLoader.GetUserWithManagerAsync`
(`VeSessionManager.Web/CurrentUserLoader.cs`), a `UserManager<User>` extension that loads the user
via `dbContext.Users.Include(u => u.ManagedByUser)` instead — this gotcha is also in CLAUDE.md's
Known Constraints, since it's easy to reintroduce in a brand-new page.

Live-verified in a real browser: `teamlead@example.com` lands on Sessions, sees only their assigned
team's data with no write controls anywhere on Sessions/Detail/VE Roster/VEC Submission;
`sessionmanager@example.com` re-checked as a regression test and still has full edit access.
