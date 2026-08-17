# Worker job tests

*Issue [#325](https://github.com/MikeWills/VeSessionManager/issues/325), 2026-08-11.*

The Worker had **no test project at all** until the 2026-08-11 audit. Nine background jobs run
unattended on the deploy box, and nothing had ever executed one.

## The shape

Each job's timer loop is three lines of framework usage. Every bug any of these jobs has had lived in
the body, so the body was extracted:

```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    using var timer = new PeriodicTimer(...);
    do
    {
        await JobTick.GuardedAsync(logger, "Name", () => RunTickAsync(stoppingToken));
    }
    while (await timer.WaitForNextTickAsync(stoppingToken));
}

internal async Task RunTickAsync(CancellationToken stoppingToken) { ... }
```

`WorkerTickHarness` builds a real `IServiceScopeFactory` over a throwaway SQLite database, so a test
drives exactly one tick and then reads the database.

**Real SQLite, not EF InMemory**, for three reasons that all apply: `ExecuteUpdateAsync` is
unsupported on InMemory outright, InMemory ignores transactions, and these tests are largely *about*
scope and change-tracker behaviour — a provider that fakes persistence proves nothing about either.
One connection is held open for the fixture's lifetime and every scope gets a context over it, which
is what makes "a scope per team" observable at all.

The harness registers **only** the collaborators the tick under test actually touches, rather than
copying `Program.cs`. A drifting copy of the real registration would be worse than none, and making
each test name its own dependencies is how you notice a tick reaching for something it shouldn't.

## What is covered, and what each test is actually for

| Job | Tests | The property |
|---|---|---|
| `SessionIngestionJob` | `IngestionStampTests` | The throttle stamp survives a failing team |
| `PerTeamDailyJob` (base) | `PerTeamScopeIsolationTests` | A scope per team, not per tick (#292) |
| `LicenseWatchJob` | `LicenseWatchSlotGuardTests` | The two-sweep slot guard (#288) |
| `UlsWatcherJob` | `UlsWatcherSlotGuardTests` | Its *own* slot guard, plus settings precedence |
| `ReconciliationJob` | `ReconciliationJobTests` | Per-team rows, the run summary, a team deleted mid-tick |
| `HistoricalImportJob` | `QueueDrainAndPurgeJobTests` | Peek before logging; stale-`Running` resumption |
| `PiiPurgeJob` | `QueueDrainAndPurgeJobTests` | One global run per tick, with a summary |
| `MessageRuleJob`, `PaymentReminderJob`, `SquareLinkPurgeJob` | `PerTeamDailyJobWiringTests` | Which schedule key and which service each subclass wires |
| all | `JobRegistrationTests` | Class ⇄ registration ⇄ descriptor |
| all | `JobCoverageCompletenessTests` | A new job cannot be added without a test that runs it |
| — | `JobTickTests` | The guard that stops a throw taking down the host |

### Three findings worth stating

**`UlsWatcherJob` needed its own tests despite sharing a schedule with `LicenseWatchJob`.** They share
`DailySlotSchedule` and the 08:00/20:00 ET anchor and *nothing else* — each has its own copy of the
"has this slot already run?" query. #288 was exactly that: the copies diverged, one was wrong, and the
shared schedule made it look as though testing one covered the other.

**The wrong schedule key on a `PerTeamDailyJob` subclass fails three ways at once and none of them are
loud:** history rows are filed under another job's name, the Job Schedule page reports the wrong
cadence for both, and the timer reads the wrong `Jobs:*` config key, so changing an interval in
configuration silently adjusts a different job. `JobRegistrationTests` cannot see it — every key
involved is a real key with a real descriptor and a real registration.

**`ReconciliationJob`'s overload resolution is load-bearing, and now asserted.** `JobRunHistoryLogger.RunAsync`
has a result-returning overload and a void one; only the first records `ResultSummary`. Binding a
method group to the wrong one compiles cleanly and leaves every summary null — the job stays green and
a *monitor* stops reporting what it found, which is its entire output.

## Every test was checked by breaking the thing it guards

A guard test never seen to fail proves nothing. Each fix or property below was reverted in the
production code and the suite re-run; each mutation failed **exactly one** test:

| Mutation | Test that caught it |
|---|---|
| Drop `h.Success` from the ULS slot guard | `AFailedRunThisSlot_TheTickRunsAgain` |
| Drop the job-name scope from that guard | `AnotherJobsSuccessInThisSlot_DoesNotSatisfyThisGuard` |
| Ignore `SystemSettings`, always use defaults | `ASuccessBeforeTheConfiguredSlot_DoesNotSuppressTheTick` |
| Bind reconciliation to the void `RunAsync` | `TheRunSummaryReachesTheHistoryRow` |
| Remove the deleted-team guard | `ATeamDeletedBetweenTheListAndTheReRead_…` |
| Remove `HistoricalImportJob`'s peek | `AnEmptyQueue_WritesNoHistoryRowAtAll` |
| Give `MessageRuleJob` the payment-reminder key | `MessageRule_FilesItsRunUnderItsOwnScheduleKey` |

### The one that did not discriminate at first

The deleted-team test originally deleted the team **before** the tick. It passed with the guard
removed, because a team deleted before the tick never enters the list at all — the guard covers a
delete landing *between* the two queries. The delete had to be fired from inside the first team's API
call, which is the only point that lies between them.

Two things came out of fixing it. The interleave must target the team **not** currently being
processed: deleting the one in flight fails on `JobRunHistory.TeamId`'s `Restrict` foreign key, which
proves that the FK works and nothing about the guard. And because the loop's ordering is an
implementation detail, the callback picks its victim from whichever team it was just asked about
rather than assuming which comes first.

## The completeness guard

`JobCoverageCompletenessTests` reflects over every concrete `BackgroundService` in the Worker and
fails if no test file mentions it. It asks for very little and does not pretend otherwise — it cannot
judge whether a test is any good. What it prevents is the specific thing that actually happened: a job
existing for months with nothing having ever run it. A checklist in a document would have gone stale
the first time someone added a job in a hurry; this fails the build instead.

It excludes its own file from the scan, since that file names every job type in its failure message
and would otherwise satisfy itself. `PerTeamDailyJob` is excluded from the count as the abstract base
three jobs derive from — it never runs on its own.
