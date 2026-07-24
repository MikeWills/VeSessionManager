# Candidate Ingestion Scheduling: Self-Throttling + Manual Refresh

How this app decides when to poll ExamTools for a given `Team`, and how a Session Manager can force
it early. Two generations of this design — the second replaced the first outright, not layered on
top — kept here together since the reasoning for dropping the first is useful context for the
second.

## Current design (2026-07-23)

`SystemSettings.SessionIngestionIntervalMinutes` (SystemAdmin-configurable, default `60`) is the
flat cadence for every `Team` — `IngestionScheduleService.IsDue` (`VeSessionManager.Core/Ingestion/`,
a plain sync method, no DB/HTTP dependency of its own, registered as a singleton in
`VeSessionManager.Worker`) just compares `Team.LastIngestionRunUtc` against that interval.
`SessionIngestionIntervalMinutes` is read fresh every tick (one trivial extra query per tick, not
per team) rather than once at Worker startup, so an admin's edit takes effect on the very next tick
instead of requiring a restart — a deliberate divergence from the Fcc jobs' own precedent of reading
settings once at startup.

For the case where a Session Manager needs a last-minute registrant pulled in *right now* — a
"Refresh candidates" button on the session detail page (`Pages/SessionManager/Detail.cshtml(.cs)`,
`OnPostRefreshCandidatesAsync`) runs `ManualCandidateRefreshService`
(`VeSessionManager.Core/Ingestion/`), which is the exact same five-step per-team pipeline
`SessionIngestionJob` runs on its own tick — ingestion → VE roster sync → Zoom/Discord scheduling →
Square payment links → registration confirmation emails, same order, same reasoning (so by the time
confirmation emails render, Zoom/payment links have their best chance of already existing) — just
run synchronously in the Razor Pages request instead of a background tick.

- Job names in `JobRunHistory` are prefixed `Manual*` (`ManualSessionIngestion`, etc.) so a
  user-triggered run is distinguishable from the job's own ticks on the ops dashboard.
- Gated on `CanEdit` like every other write action on that page (not `CanView`) since it triggers
  real external calls including emails, not just a display refresh.
- Runs for the *whole team*, not just the session being viewed — same scope as the background job.
- Web now registers the same `IExamToolsClient`/`IZoomClient`/`IDiscordEventClient` stack as the
  Worker (all singletons, `VeSessionManager.Web/Program.cs` mirrors
  `VeSessionManager.Worker/Program.cs`'s registrations) — previously Web only needed Square/Email/
  notification services for its own admin actions.
- Deliberately still runs the whole team's block, not just an ExamTools-only fetch — same "gated
  together" simplicity tradeoff the original design established, so this button's behavior stays
  predictable (it does exactly what the next scheduled poll would have done, just now).

**Open TODO (per explicit user request):** audit the candidate notification email flow this button
(and the background job) triggers — confirm how many emails a candidate actually receives and when,
across registration confirmation/day-before-reminder/payment-reminder paths, before this on-demand
trigger trains Session Managers to expect "one click, one email" if the real behavior turns out to
be noisier. See `docs/email-reference.md` and `TODO.md`'s Deferred section.

## Superseded design (2026-07-21 – 2026-07-23)

The original design polled every team aggressively — every `Jobs:SessionIngestionIntervalSeconds`
tick (effectively every 5 minutes) — for the hour around each of that team's sessions, reusing
`Session.DurationMinutes` as the natural "just after start" boundary, so a last-minute registrant
would still be caught quickly without every team being polled aggressively all day.

Dropped because the user identified it was solving the wrong problem: a Session Manager actively
prepping/running a session already knows in ExamTools when someone new has registered, and would
rather pull them in on demand (see the current design above) than wait on a timer.
