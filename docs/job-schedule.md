# Job Schedule (Admin → Job Schedule)

Read-only screen listing every background job the Worker runs: what it does, how often, when it last
ran, and **when it runs next**.

Built 2026-08-06. The trigger was a simple question with no good answer — "when does the next run
happen?" Only ingestion exposed it anywhere in the UI (Team Maintenance's `NextDueUtc`, and only for
one team at a time); for every other job the answer lived in the Worker's source. Job History records
what already happened and cannot answer what happens next.

## Why a registry, not numbers on the page

`Core/Jobs/JobSchedules.cs` holds one descriptor per job — name, description, cadence shape, config
key, defaults, anchor hour. **Both hosts read it**: the Worker to schedule, Web to report.

Before this, every interval was a literal at its own job's call site
(`configuration.GetValue("Jobs:PiiPurgeIntervalHours", 24)`), invisible to Web, which cannot
reference the Worker project at all. A schedule page built by re-typing those numbers would be wrong
the first time anyone retuned a job — and wrong *silently*. **A screen that confidently reports the
wrong next-run time is worse than no screen**, because it is believed: unlike a wrong list, a wrong
timestamp looks exactly like a right one.

This is the same lesson as `TeamPipeline` (the per-team pipeline order that drifted while it was
written out in three places), applied before the drift rather than after.

`Jobs:*` configuration moved out of the Worker's `appsettings.json` into
`src/Shared/appsettings.Shared.json` for the same reason — Web resolves those keys now, and a key
present in only one host is precisely how **T04** happened.

## Two cadence shapes, and why the distinction is on the page

The jobs genuinely differ, and flattening them into one "next run" column would misreport half of
them.

**`AnchoredToEasternHour`** — `UlsWatcher` (08:00/20:00 ET by default, tunable in System Settings)
and `LicenseWatch` (06:00 ET). These tick hourly but only *work* when the current slot has no
successful run yet, so restarts and outages self-heal and the schedule never drifts. Reported as
**Scheduled**: the stated time is what will happen.

**`IntervalFromWorkerStart`** — everything else. A `PeriodicTimer` created when the Worker process
started, so the cycle resets on every deploy or restart. Reported as **Estimated** (last run +
interval), with the caveat stated on the page rather than buried here. An estimate in the past is not
a bug in the page: it means the Worker restarted, or is down.

### Due now

An anchored job whose current slot has no *successful* run is **not** waiting for the following slot
— it catches up on its next hourly tick. Reporting the next slot in that state would be wrong by a
whole interval and would hide that the job is late, so it renders as `Due now` instead.

A **failed** run does not satisfy a slot, matching the Worker's own `Success` filter. Without that,
a job failing every attempt would read as on schedule.

## Deliberate choices

- **Not team-scoped.** A schedule is a property of the deployment; several jobs are global, and the
  per-team ones still run on one shared timer. Job History stays the place for per-team outcomes.
- **TeamAdmin can see it**, matching Job History. "When does the next ingestion land?" is exactly the
  question a team admin asks after changing something in ExamTools, and nothing here is sensitive —
  no credentials, no candidate data, only timings.
- **Manual runs are excluded.** `TeamPipeline` prefixes user-triggered runs with `Manual`, and the
  exact-name match skips them. Counting one would report a job as freshly run when its timer never
  fired, and push the estimate out by a full interval.
- **Never-run interval jobs report `Unknown`,** not "now + interval". There is nothing to count from,
  and the alternative is fabricating a time.

## Two bugs the tests caught before it shipped

**`Max` over an empty filtered sequence throws.** `g.Where(h => h.Success).Max(h => h.StartedUtc)`
does not return null for a job whose every run failed — it throws `InvalidOperationException`, taking
down the entire page for one perpetually-failing job. The nullable cast has to go **inside** the
`Max` (`Max(h => (DateTime?)h.StartedUtc)`); an outer cast is too late, because the exception happens
before there is anything to cast.

**Advancing an anchored slot by adding hours to a UTC value breaks across DST.** 06:00 ET is 10:00
UTC in summer and 11:00 UTC in winter, so `previousSlotUtc + interval` is an hour off twice a year —
silently, in the direction nobody checks. `DailySlotSchedule.NextSlotUtc` steps the *Eastern*
wall-clock hour and converts afterwards.

The grouped query is additionally pinned against real SQLite (`JobScheduleSqliteTests`), not just
InMemory: it is a `GroupBy` with two aggregates, one filtered, and InMemory cannot vouch for whether
that translates — the same reasoning behind `ActiveCandidateCountSqliteTests`.

## Adding a job

Add a `JobScheduleDescriptor` to `JobSchedules.All`, and have the job read its interval from that
descriptor rather than a literal. `JobName` **must** match the string passed to
`JobRunHistoryLogger.RunAsync` exactly — it is the join key back to `JobRunHistory`, which is where
"when did this last run?" is answered. A job missing from the registry still runs; it just never
appears on the one screen that claims to list everything.

## Tick vs. run, and the shared ULS schedule (2026-08-06)

Three corrections, all reported off the live page within a day of it shipping.

**The page reported session ingestion's timer tick as its cadence.** It said *"Every 5 minutes"* while
System Settings said 60. Both were true of different things: the job wakes every 5 minutes (config),
but each tick only polls a team once `SystemSettings.SessionIngestionIntervalMinutes` has elapsed
since *that team's* last run. Reporting the tick overstated the cadence by 12x — on the one screen
whose entire value is being trusted.

Now the cadence comes from the setting and the tick is stated beneath it, for **every** job rather
than only where they differ: the ticks genuinely vary job to job, and "these two coincide" is itself
worth being able to read off the page. Wording follows the unit of the setting it came from, so 60
minutes reads back as *"Every 60 minutes"* — the number the admin typed, not a converted one.

**The renewal monitor ran once a day while the ULS watcher ran every six hours.** Both read the same
FCC data through the same ExamTools mirror, so a renewal already visible could sit unseen for most of
a day — which is exactly what was noticed. `LicenseWatchJob` now reads the same
`UlsWatcherStartHourEt`/`UlsWatcherIntervalHours` settings, so the two run on one schedule and one
control. The System Settings help text says so where those fields are edited.

**A zero interval crashed the page.** A `SystemSettings` row that was never filled in holds `0`, and
`0` is a `DivideByZero` inside the slot arithmetic — taking down the whole Job Schedule page, not just
misreporting one row, and stopping the anchored jobs too. The admin form's `min="1"` is client-side
only. `JobSchedules.IntervalOrDefault`/`StartHourOrDefault` now coerce at every read site, and
`DailySlotSchedule` throws `ArgumentOutOfRangeException` as a backstop so a bad value names itself
instead of surfacing as arithmetic.

### Separately: a settings help text that described removed behaviour

The Session Ingestion field claimed the interval *"automatically shortens to every few minutes for any
team with a session starting within the next hour"*. That surge existed once and was **deliberately
removed** in favour of the session page's "Refresh candidates" button (see
`IngestionScheduleService`'s own remarks), but the text outlived it — so the page promised a
responsiveness the app no longer had. Corrected to describe the flat cadence and point at the button.
