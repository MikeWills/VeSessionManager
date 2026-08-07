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
flagged Team Lead's scope as needing confirmation before building. Originally resolved with a
single, nullable `User.TeamId`; **replaced (issues #17/#19, 2026-07-28) with a real many-to-many
`User`↔`Team` relationship** once a Session Manager needed to belong to more than one team and
filter the session list down to just one at a time:

- **`UserTeam`** (`Id`/`UserId`/`TeamId`/`CreatedUtc`, composite key on `(UserId, TeamId)`) — the one
  source of truth for which team(s) a TeamAdmin/SessionManager belongs to, replacing the old
  `User.TeamId`/`User.Team` (both removed). Migration `Phase14UserTeamMultiTeam` backfills one
  `UserTeam` row per existing single-team user before dropping the column.
- `User.ManagedByUserId` — a TeamLead's assigned manager, role-agnostic (a SessionManager or a
  TeamAdmin). **Informational only since 2026-08-07: it records who a lead reports to and
  grants no access whatsoever.** It used to determine the lead's team scope transitively; see
  "TeamLead scope" below for why that was removed.

`SessionAccessScope` (`VeSessionManager.Core/Authorization/SessionAccessScope.cs`) is the actual
mechanism, plain C# with no ASP.NET dependency so it's directly unit-tested
(`SessionAccessScopeTests.cs`) rather than requiring a web host:

- `GetEffectiveTeamIds(User)` (plural, renamed from the old singular `GetEffectiveTeamId`) —
  SystemAdmin → `null` (no filter); TeamAdmin/SessionManager → their own `UserTeams` team ids;
  TeamLead → `user.UserTeams` team ids, exactly like the other scoped roles. Callers must have
  `UserTeams` eager-loaded — see `CurrentUserLoader.GetUserWithManagerAsync`
  gotcha below, now load-bearing for every role, not just TeamLead.
- `Scope(IQueryable<Session>, User, int? selectedTeamId = null)` — SystemAdmin: unfiltered unless a
  specific team was requested (the session list's own team filter, issue #17); everyone else: every
  session across all their teams by default, or narrowed to `selectedTeamId` when that's one of
  their own teams (a tampered/foreign id is silently ignored, not erred on). An unassigned
  TeamAdmin/SessionManager or TeamLead correctly sees **nothing**, not everything.
- `CanView`/`CanEdit(User, Session)` — set-membership (`Contains`) instead of scalar equality;
  TeamLead is still always read-only for `CanEdit` (pending explicit sign-off on any TeamLead write
  access).
- `TryResolveViewableTeamId(User, int? requestedTeamId)` — new: the single "which team is this user
  looking at right now" resolution for per-team list pages (VE Roster, VEC Submission, Unmatched
  Payments, Fee Configurations) that show one team at a time rather than a mixed multi-team list
  like the session list. Mirrors `AdminAccessScope.TryResolveManageableTeamId`'s shape for the
  session-viewing side rather than the admin-config side.

TeamAdmin and SessionManager are **equivalent** in `SessionAccessScope` — both resolve to their own
`UserTeams`. The only difference between those two roles is settings/user-management access (who
can edit Team credentials, who can grant SessionManager/TeamLead), a separate authorization surface
(`AdminAccessScope`) covers, not this class's concern. `AdminAccessScope` got the same
set-based treatment: `ScopeTeams`/`ScopeAuditLog`/`ScopeJobRunHistory` and `CanManageTeam`/
`CanManageUser` all use `Contains` against a TeamAdmin's team set now, which is what makes the
existing `AvailableTeams`/`filter-pill` picker on `Users`/`TeamSettings`/`EmailTemplates` (previously
hardcoded to `[]` for anyone but SystemAdmin) work for a multi-team TeamAdmin too. Team membership
itself is managed via `UserManagementService.SetTeamsAsync` — a separate action from role assignment
(`SetRoleAsync`, which no longer takes a `teamId`) and from account creation (`CreateAsync`, which
now creates a user with zero teams; an admin assigns teams afterward via the Users page's "Manage
teams" action).

This is real infrastructure for Phase 9b to consume once an actual session-list page exists — 9a
builds and tests it now, per the spec's own framing ("build the *mechanism* ... even though
there's barely any real data to filter yet"); it isn't wired into a real data page this phase.

## Identity setup

`User` (`VeSessionManager.Core/Entities/User.cs`) is `IdentityUser<int>` — inherits `UserName`/
`Email` (now nullable, superseding the old `required string Email`)/`PasswordHash`/
`SecurityStamp`/etc. Adds `Name` (required display name), `Role`, `UserTeams` (the multi-team join
collection, replacing the old single `TeamId`/`Team` — see "Team scoping" above),
`ManagedByUserId`/`ManagedByUser`.

`VeSessionManager.Web`'s `Program.cs` uses `AddIdentityCore<User>()`, not `AddIdentity<User,
TRole>()` — deliberately skips Identity's Role system (see above). `AddIdentityCookies()` supplies
the `ApplicationScheme`/`ExternalScheme` cookie schemes that `AddIdentity` would otherwise add for
you automatically; with `AddIdentityCore` you wire that up explicitly. `app.UseAuthentication()`
was **missing entirely** before this phase — `app.UseAuthorization()` alone never populated
`HttpContext.User`, so authorization had been a silent no-op since Phase 0's scaffold.

A custom `AppClaimsPrincipalFactory` (`VeSessionManager.Web/AppClaimsPrincipalFactory.cs`) adds a
`ClaimTypes.Role` claim from `user.Role` at sign-in, so `[Authorize(Roles = "...")]` reads straight
from the signed-in cookie's claims — no extra DB hit per request. It no longer adds a `TeamId`
claim (dropped for issues #17/#19) — a user can belong to more than one team now, which a
single-value claim can't represent, and nothing in the authorization path actually read it: every
real team-scoping check re-fetches `User` (with `UserTeams` included) from the DB via
`SessionAccessScope`/`AdminAccessScope` rather than trusting the cookie for that.

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

**Superseded in part (2026-08-01):** VE Roster is no longer one of the pages TeamLead (or
SessionManager) can reach — see below.

## VE Roster restricted to admin roles (2026-08-01)

`Pages/SessionManager/VeRoster.cshtml.cs` went from
`[Authorize(Roles = "SystemAdmin,TeamAdmin,SessionManager,TeamLead")]` to
**`[Authorize(Roles = "SystemAdmin,TeamAdmin")]`**.

The page is two sensitive things at once: a full contact roster for a team's volunteer examiners,
and a per-VE **session-count leaderboard**. The count half is the sharper edge — a visible
sessions-served number next to each person's name invites comparison between volunteers that nobody
asked for, and there's no way to serve that report to a general audience without it reading as a
ranking.

Scope, deliberately:

- **Session Detail's VE chips are unchanged.** Those show the VEs actually serving that one session
  — operational context a Session Manager running it needs, not a roster and not a count.
- **The nav gate in `_AppLayout.cshtml` moved in step with the attribute.** The whole "VEs ▾" group
  is now wrapped in a `SystemAdmin or TeamAdmin` check, following the same rule already applied to
  Unmatched Payments: never render a link whose target the user's role will 403 on. The attribute is
  the actual enforcement — the nav gate only stops the dead link — so **both must be changed
  together**, and the page's own XML doc says so at the point someone would edit it.

Nothing else surfaces the roster or the counts: `VolunteerExaminerReportService.GetSessionCountsAsync`
has exactly one caller (this page), and `VolunteerExaminerRosterService`'s only Web consumer is the
session Detail page.

## Admin navigation: parent pages, not a flat menu (2026-08-06)

The Settings menu used to list every admin page flat — Teams, Team Settings, Team Maintenance,
Users, Email Templates, Fees, VECs. Three of those need **one team chosen** before they show
anything, so a nav link landed on "Select a team…" and made you pick a team you had usually just
been looking at.

They now come off the nav entirely and are reached from the **Teams row menu**, which picks the team
for you. Fees likewise moves to the **VECs row menu**. Each child page gained a breadcrumb back to
its parent (`ParentCrumb` + `Pages/Shared/_ParentCrumb.cshtml`), rendered in place of the page's
eyebrow so it costs no vertical space.

### What this forced, and why

**Teams had to open up to TeamAdmin.** It was SystemAdmin-only, while every per-team child page is
SystemAdmin *and* TeamAdmin — so removing the nav links without changing that would have locked
TeamAdmins out of their own settings, users and templates completely. Teams is now
`SystemAdmin,TeamAdmin`, **scoped through the existing `AdminAccessScope.ScopeTeams`**: a SystemAdmin
sees every team, a TeamAdmin sees only their own.

Scoping the *list* was chosen over showing all teams with the row actions gated per row. Both work,
but scoping leaves nothing to get wrong later — every row a viewer can see is one they may manage, so
the row menu needs no condition. The show-everything variant was written first and discarded: a
TeamAdmin could click a row for another team, and the child page would then quietly resolve to a team
they *do* manage (those pages fall back rather than refusing), so you would land on different settings
than the row you clicked. Nothing leaked, but it read as a bug.

**Creating a team stays SystemAdmin**, enforced in `OnPostCreateAsync` via
`AdminAccessScope.CanCreateTeam` — not merely by hiding the button. The page is reachable by
TeamAdmin now, so the handler is the only real guard.

**Fees became SystemAdmin-only.** A `FeeConfiguration` belongs to a `Vec`, which is shared reference
data across every team, so one team's admin editing it would change what every other team charges.
Its per-team checks are left in place underneath rather than removed.

**Users deliberately stays in the nav.** Unlike the other three it is genuinely cross-team for a
SystemAdmin — with no team selected it lists every user — so routing them through one team's row
would make the global list *harder* to reach, not easier.

### The breadcrumb's role check is per-parent

`ParentCrumb` carries the roles allowed on the **parent** page and renders a link only if the viewer
has one of them; otherwise the original eyebrow text renders unchanged. It has to be per-parent
because the two parents differ — Teams admits TeamAdmin, VECs does not — and a blanket check would
hand some viewers a link straight to a 403.


## TeamLead scope: their own team, not their manager's (2026-08-07)

A TeamLead used to be scoped **transitively** — `GetEffectiveTeamIds` returned
`user.ManagedByUser?.UserTeams`, so a lead saw whatever teams their manager belonged to.

That leaks across teams, and it was reported from the live site:

> I could have a session for two different teams, but the team lead is only on one of the teams.

A SessionManager or TeamAdmin can legitimately work across several teams. A team lead belongs to one.
Inheriting the manager's whole set therefore handed the lead sight of every other team that manager
covered — their sessions, their candidates, and the PII a lead is deliberately given for day-of
check-in. Nothing in the UI hinted at it, because the Users page showed the lead's *own* (unused)
team rows rather than what they were actually scoped by.

This was not an oversight in one branch: a test asserted the inheritance as correct
(`TeamLead_WhoseManagerBelongsToMultipleTeams_InheritsAllOfThem`), so the behaviour was deliberate
and simply wrong about the real-world shape.

**Now:** a TeamLead is scoped by their own `UserTeams`, identically to a TeamAdmin or SessionManager,
and the Users page offers them **Manage teams** so they can be given one. The manager link stays as a
reporting record and is validated only for "is this person a SessionManager or TeamAdmin".

### What this changed in `SetManagerAsync`

The old validation required the manager to share a team with the lead — a guard whose entire purpose
was stopping a TeamAdmin widening a lead's scope through the manager link. With no access riding on
that link there is nothing to widen, so the rule is gone. It was also actively harmful while it
lasted: because a lead's own team was never consulted for scope, the check blocked legitimate
reporting lines (a lead on WX0MIK could not be pointed at a manager on HRCC) while protecting
nothing that the lead's own team assignment does not now protect directly.
