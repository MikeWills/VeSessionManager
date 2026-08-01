# Admin → Team Maintenance

Issues #77 and #73, built 2026-07-31. The operational counterpart to Team Settings: that page is
*configuration* (credentials, addresses, fee behaviour), this one is *operations* — when was this
team last polled, poll it now, and import history.

Kept as one page because the pieces only make sense together. "Last polled 58 minutes ago" is most
useful directly beside a button that says "poll now", and both are most useful beside the thing that
tells you the Worker is running at all.

## Why it exists

**#77 — there was no team-level refresh.** `ManualCandidateRefreshService` already runs a team's
whole ingestion pipeline on demand; it is the intended escape hatch from the hourly
`SessionIngestionIntervalMinutes` gate. But its only trigger was the "Refresh candidates" button on a
session's Detail page, which fails exactly when it is most needed: **a team with no ingested sessions
has no session page, so there was no way to trigger ingestion from the UI at all.** Hit live
2026-07-31 — WX0MIK had 0 sessions locally while two existed upstream, and the only way to force a
poll was setting `Team.LastIngestionRunUtc = NULL` by hand in the database (which
`IngestionScheduleService` treats as "always due"). Not something a TeamAdmin should ever need to do,
and not documented anywhere as supported. The same gap applied to any newly-created team: enter
credentials, then wait up to an hour with no feedback and no way to hurry it.

**#73 — the schedule was invisible, and so was the Worker.** "When will the next pull happen?" took a
direct database query to answer. It is genuinely non-obvious because there are *two* independent
schedules:

- `SessionIngestionJob` ticks every `Jobs:SessionIngestionIntervalSeconds` (default 300s).
- Each team is *then* gated by `IngestionScheduleService.IsDue` against
  `SystemSettings.SessionIngestionIntervalMinutes` (default 60), tracked per team in
  `Team.LastIngestionRunUtc`.

So the job "runs" every 5 minutes while a given team is polled hourly — and **a skipped team writes
no `JobRunHistory` row at all.** That is why the ops dashboard's silence is indistinguishable from a
dead Worker.

## Access

`[Authorize(Roles = "SystemAdmin,TeamAdmin")]`, matching the existing per-team-settings convention —
a team-level refresh hits ExamTools' API on demand, so it isn't unrestricted. SessionManager keeps
the per-session button and is unaffected. Team resolution goes through
`AdminAccessScope.TryResolveManageableTeamId`, so a TeamAdmin is locked to their own team regardless
of a tampered `?teamId=`, and the POST handler re-resolves rather than trusting the form.

## Ingestion status

`IngestionStatusService` derives everything from data already stored — `Team.LastIngestionRunUtc`
plus the one `SystemSettings` row. No new schema.

The "is it due" arithmetic **delegates to `IngestionScheduleService.IsDue`** rather than restating
it, so the countdown can never disagree with the gate it describes. Times render absolute (Eastern,
via `EasternTimeFormatter`) with a relative hint beside them — relative alone silently goes stale on
a page left open, which is precisely the state someone waiting on a registrant is in.

### Reading SystemSettings without writing it

`SystemSettingsService.GetAsync` *get-or-creates* the singleton row — a write. This service is called
from page renders, including the site-wide banner on every request, so it reads `SystemSettings`
directly and falls back to `SystemSettingsService.DefaultSessionIngestionIntervalMinutes` when the
row is missing. Same reasoning, and the same shape, as `_TestModeBanner.cshtml`. The constant is
shared rather than a second literal `60`, so the fallback cannot drift from what would have been
created.

## The Worker-health banner

The more valuable half of #73. Web and Worker are separate processes, and nothing in the UI revealed
whether the Worker was alive: a dead Worker looks exactly like a quiet week. That gap bit repeatedly
during one evening's work, and matters far more once the beta server runs unattended.

`_IngestionHealthBanner.cshtml` renders site-wide for SystemAdmin/TeamAdmin only (a Session Manager
can't act on it, so for them it would be pure alarm).

**Four states, not a bool** — `IngestionHealthState`. A fresh deployment and a dead Worker both look
like "nothing has been polled" but need different messages, and a brand-new install must not open on
a red alarm:

| State | Meaning | Banner |
|---|---|---|
| `Healthy` | Some team polled within the window | none |
| `NeverPolled` | Teams exist, none ever polled | "ingestion has never run" |
| `Stale` | Polled before, but not recently enough | "the Worker is probably not running" |
| `NoTeams` | Nothing configured to poll | none |

**Stale means no team polled in `2 ×` the configured interval.** Two rather than one because the
job's 5-minute tick doesn't align with the hourly per-team gate, so real gaps routinely run a little
over one interval; warning at 1× would fire during normal operation and train everyone to ignore the
banner. The threshold is a multiple of the admin's own setting, not a constant — a deployment polling
every 5 minutes doesn't wait two hours to be told.

**Health is deployment-wide, evaluated across every team regardless of who is looking.** Scoping it
to the viewed team would tell a TeamAdmin who just created a team that the Worker is down while it
polls another team happily (`HealthIsDeploymentWide_EvenWhenTheReportIsScopedToOneNewTeam`).

`IngestionHealthCache` caches the report for 60 seconds because the banner renders on *every* page
request for those users. It is a **singleton**, which is exactly why it resolves
`IngestionStatusService` through a fresh `IServiceScope` per refresh rather than by injection —
injecting a scoped `AppDbContext` into a singleton is the classic way to get one context living for
the process lifetime. A stale-by-a-minute answer is fine when the condition being reported is "no
activity for at least two hours"; the Team Maintenance page itself always reads live.

## Refresh now

Calls the existing `ManualCandidateRefreshService.RunAsync(team, …)` — no new pipeline logic, just a
second entry point. Job names stay `Manual*`-prefixed, so Job History still distinguishes
user-triggered runs from scheduled ones.

**It deliberately does not touch `Team.LastIngestionRunUtc`.** A manual run is extra work on top of
the schedule, not a substitute for it, so pressing this must not push the next scheduled poll an hour
further out. That was already the behaviour; #77 asked for it to be confirmed deliberately rather
than left as an undocumented accident, so it is now stated on the field itself.

**Debounced 60 seconds per team** (`TeamRefreshThrottle`) so a double-click, or an impatient admin
watching for a registrant, doesn't stack full pipeline passes over someone else's servers. The
per-session button is deliberately **not** throttled — it's pressed by a Session Manager working one
session in real time, which is the situation a throttle would obstruct.

The throttle is schema-free: it reads the `ManualSessionIngestion` `JobRunHistory` rows a manual run
already writes, rather than adding a `Team` column and a migration for a 60-second value. That also
makes it shared across web instances and restart-proof, since the evidence is in the database. It
keys on the `Manual`-prefixed name specifically — keying on `SessionIngestion` would make Refresh now
unusable for an hour after every scheduled poll, i.e. exactly the wait it exists to skip
(`TheBackgroundJobsOwnRun_DoesNotBlockAManualRefresh`).

A team with no ExamTools credentials gets the button disabled and an explanatory line pointing at
Team Settings, rather than a refresh that quietly does nothing.

## Historical import

See `docs/historical-import.md` — the third section of this page, and the other half of issue #67.
