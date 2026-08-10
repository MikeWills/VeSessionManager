# Page smoke tests (2026-08-10)

`tests/VeSessionManager.Web.Tests` boots the real Web app in-process with `WebApplicationFactory`,
against a throwaway SQLite database, and requests every Razor page.

## Why

**Nothing in this repo rendered Razor.** Not the build, not the 928 Core tests, and not the
static-HTML harness used for layout checks — that one uses hand-written markup, not the real
`.cshtml`. Two bugs reached a deployment on the same day because of it:

- A `<form>` with both `action=` and `asp-page-handler`, which `FormTagHelper` throws on **at render
  time**. The build was clean, every test passed, and the VE Directory 500'd for anyone who opened
  it.
- An anchor with `asp-all-route-data` beside `asp-route-id`, where the dictionary **assigned** the
  route values and discarded the id. Nothing threw; every link to a VE simply pointed at nobody.

A page never rendered anywhere before it reaches a browser is not tested, however green the suite is.

## What it covers

| Test | Catches |
|---|---|
| Every discovered page renders for a SystemAdmin | render-time exceptions, missing DI registrations, view null-refs, broken layouts |
| Links on the VE Directory point somewhere real | URL generation that silently produced nothing |
| An anonymous visitor is challenged | an `[Authorize]` attribute that stopped matching |
| A SessionManager cannot reach the VE Directory | role gates on pages holding home addresses |

**Pages are discovered from the app's own `EndpointDataSource`**, not a hand-written list, so a new
page is covered the day it exists — a list would need maintaining by the same person who forgot to
render the page. A route needing a parameter the harness has no value for **fails loudly** rather
than being skipped, because a page quietly excluded from the smoke test is exactly the page that
breaks.

### An empty href is the signature of a whole bug class

The first version of the link test only followed links that *had* an href, and **passed with the
original bug reintroduced**. When a tag helper cannot generate a URL — missing route value, renamed
page, typo'd handler — it does not throw and does not warn. It emits `<a href="">`, which renders
as a link, looks normal, and goes nowhere. Confirmed by reintroducing the bug and dumping the
markup: `<a href="">Test VE</a>`.

So the test asserts **no anchor on the page has an empty href**, which generalises past the specific
regression.

## How the harness works

- **Environment `IntegrationTest`** — deliberately not `Development` (which runs `DevAuthSeeder`
  against whatever database is configured), and not `Production`/`Test` (whose appsettings carry
  real connection strings and key-ring paths).
- **SQLite in-memory**, with the connection held open for the factory's lifetime, registered in
  `ConfigureTestServices` so it is in place before the host's own startup runs.
- **Migrations are applied by the harness**, before the host starts. The app's own `Migrate()` then
  finds everything applied and no-ops — and every migration gets exercised on every run.
- **The database is seeded before startup, not after**, because `Program.cs` *refuses to start* when
  no account can sign in. That guard is right — a site whose every credential is rejected is worse
  than one that plainly did not start — so the harness satisfies it (a user with a non-null
  `PasswordHash`) rather than switching it off. Weakening a startup safety check to make a test pass
  would be testing a configuration nobody runs.
- **A header-driven auth scheme** (`X-Test-Role`) supplies a principal whose `NameIdentifier` is the
  seeded user's real id, because pages resolve the current user through `GetUserWithManagerAsync`
  and would otherwise 403 in a way that looks like a page bug.
- **`public partial class Program;`** was added to Web's `Program.cs` so the factory can name the
  entry point.

## Limits

- **It proves a page renders, not that it is correct.** A page showing wrong data returns 200.
- **It runs against fakes for anything external** — no ExamTools, Square or SMTP. That is what
  [`docs/reconciliation.md`](reconciliation.md) is for; the two cover different halves.
- **It is slower than the Core suite** (about a minute), because it boots the app and applies every
  migration per fixture.
