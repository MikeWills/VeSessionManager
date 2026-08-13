# Architecture

How the pieces fit together and why they are shaped that way. Setup lives in
[`README.md`](README.md); each subsystem has its own deep-dive under [`docs/`](docs/).

## The shape of the problem

A Volunteer Examiner team runs amateur radio exam sessions. The session itself is scheduled in
**ExamTools** (hamstudy.org's VE tooling) — that is where candidates register, where VEs are
rostered, and where results are filed. Everything this app does hangs off that: it polls ExamTools,
and then does the surrounding work a Session Manager would otherwise do by hand — create the Zoom
meeting and Discord event, generate a payment link, send the confirmation and reminder emails, watch
the FCC for the resulting license grant, and track what has been submitted to the VEC.

Two consequences shape the whole design:

- **ExamTools is the source of truth, and this app is a follower.** It never invents a session or a
  candidate. If ExamTools and the local database disagree, ExamTools wins.
- **This app is not in the critical path of an exam.** If it is down for a day, the session still
  happens. That buys a simple design: polling instead of webhooks, SQLite instead of a server
  database, and jobs that catch up rather than queues that must not be lost.

## Three projects

```
src/
  VeSessionManager.Core/     entities, EF Core DbContext + migrations, every service, API clients
  VeSessionManager.Worker/   generic Host — the background jobs, one per concern
  VeSessionManager.Web/      ASP.NET Core Razor Pages — the admin backend
  Shared/                    appsettings.Shared.json, linked into both hosts
tests/
  VeSessionManager.Core.Tests/     services, mostly EF InMemory + fake clients
  VeSessionManager.Web.Tests/      pages rendered for real via WebApplicationFactory
  VeSessionManager.Worker.Tests/   each job's tick, driven against real SQLite
```

**Web cannot reference Worker, and does not need to.** All behaviour lives in Core; the Worker is
timers plus scheduling, and Web is pages plus authorization. Anything both hosts must agree on —
job schedules, shared configuration — is a Core type or a shared config file, never a copy. Two
concrete examples, both born from real drift: `JobSchedules` (one descriptor per job, the Worker
schedules from it and Web reports from it) and `src/Shared/appsettings.Shared.json` (which exists
because Web once ran against Square Sandbox while the Worker ran Production).

**One SQLite file, two processes.** Both call `Database.Migrate()` at startup, which is why start
order matters on a deploy. It is deliberately outside the deployed app directory so a sync with
`--delete` can never reach it.

## Data model, in one breath

`Vec` (the FCC-recognized coordinating organization — ARRL, W5YI, GLAARG…) is a shared global
reference table. `Team` is the group of VEs running one deployment, and holds all of that team's
integration credentials. **A `Vec` is never owned by a `Team`** — they are siblings, and `Session`
carries independent `TeamId` and `VecId`. Getting this backwards is the most natural mistake to make
here; [`docs/multi-team.md`](docs/multi-team.md) has the reasoning.

Under `Session` sit `Candidate` (the person testing, and the PII this app most needs to protect),
`Payment`, and the VE roster. `VolunteerExaminer` is deliberately **global rather than team-scoped**
— a VE may serve several teams, and a call sign is one person; `VeTeamMembership` joins them to
teams. That reach is intended, and [`docs/ve-management.md`](docs/ve-management.md) records the
conditions that would reverse it.

## How work actually happens: scan-based jobs

Every background job in this app has the same shape, and new ones should too:

> Diff stored state against a remote feed or a date threshold on each tick, and use a
> `...SentUtc` / `...SyncedUtc` / status flag as **both** the "needs action" query filter **and** the
> idempotency guard — saved immediately after each item, so a crash mid-run neither double-processes
> nor loses the progress already made.

Nothing is event-driven, and nothing depends on a message surviving. A job that missed yesterday
does the work today. This is why the app can be restarted, redeployed, or left off for a week
without a reconciliation step.

The per-team refresh sequence — ingest, VE roster, exam results, Zoom/Discord, payment links,
confirmation emails — is defined exactly once, in `TeamPipeline`. It used to be written out at three
call sites and they drifted, silently, for weeks.

**Retry-safety against a crash between an API call and its local save** is the one hard rule for
anything creating an external resource. Either query-before-create (list what exists, match by name
and time — Zoom, Discord) or persist an idempotency key *before* calling and reuse it on every
retry (Square). Real duplicate Discord events were created before this rule existed.

## Integrations: one required, the rest optional

**ExamTools is the hard requirement** and fails loudly — without ingestion there is nothing to act
on. **Zoom, Discord, Square and SMTP are each optional**, and follow one pattern: the client exposes
`IsConfigured`, the calling service checks it *before* the call, skips with a single quiet log line,
and leaves the tracking field null so the next poll retries automatically. Adding credentials later
needs no backfill step — the work simply starts happening.

Credentials are per-`Team` and live in the database, **encrypted at rest** through
`EncryptedStringConverter` and ASP.NET Core Data Protection. Two things follow from that and are
easy to get wrong:

- Web and Worker must register Data Protection with the **same application name and key-ring path**,
  or one process's writes are unreadable by the other.
- A wrong or missing key ring is *indistinguishable* from un-migrated plaintext, because the
  converter falls back to the raw value rather than throwing. `DataProtectionKeyRingGuard` refuses
  to start rather than run in that state, and `--verify-keyring` runs the same check on demand.

See [`docs/credential-encryption.md`](docs/credential-encryption.md).

## The web side

Razor Pages, ASP.NET Core Identity, and a role model of four:
**SystemAdmin → TeamAdmin → SessionManager → TeamLead**. Authorization is two-layered and both
layers are required: a `[Authorize(Roles=…)]` attribute decides who may open a page, and
`SessionAccessScope` / `AdminAccessScope` decide *which teams' rows* they may see or touch. Every
handler that takes an id re-checks ownership; a role attribute alone would be an IDOR.

Pages are authenticated by default via a `FallbackPolicy`, with the public ones opting out
explicitly. That default applies to minimal-API endpoints too, which is why the Square webhook has
to say `AllowAnonymous` out loud.

There is no SPA and no JavaScript framework — server-rendered pages, one small `app.js`, and one
hand-written `app.css` design system.

## Testing, and which project a test belongs in

The rule is *what the test can observe*:

- **Core.Tests** — services, mostly EF InMemory with fake API clients.
- **Web.Tests** — real pages rendered through `WebApplicationFactory` against throwaway SQLite, plus
  source scans over Razor for mistakes that produce no error (a duplicated user-facing string, a
  form field bound to nothing).
- **Worker.Tests** — each job's tick driven directly against real SQLite.

**EF InMemory is the default, not the rule.** Transactions, `ExecuteUpdateAsync`, SQL null
semantics, and unique-index behaviour are all invisible to it, and a test that depends on any of
them uses `DataSource=:memory:` SQLite instead. Several real bugs passed a green InMemory suite.

A recurring pattern worth knowing: **some mistakes cannot be caught by a behavioural test, because
the broken and correct versions behave identically until someone edits one of them.** Two copies of
a string agreeing is the normal state right up until it isn't. Those are guarded by source scans
that assert "one definition, one home".

## Where to read next

| Topic | File |
|---|---|
| Full phased build plan and data model | [`docs/spec.md`](docs/spec.md) |
| Multi-team model, and why VEC ⇒ Team ⇒ VE | [`docs/multi-team.md`](docs/multi-team.md) |
| Roles, scoping, sign-in | [`docs/admin-auth.md`](docs/admin-auth.md) |
| Credential encryption and the key ring | [`docs/credential-encryption.md`](docs/credential-encryption.md) |
| Job cadences and the schedule registry | [`docs/job-schedule.md`](docs/job-schedule.md) |
| Server setup, systemd, CI/CD | [`docs/deployment.md`](docs/deployment.md) |
| ExamTools API shapes and quirks | [`docs/examtools-api.md`](docs/examtools-api.md) |
