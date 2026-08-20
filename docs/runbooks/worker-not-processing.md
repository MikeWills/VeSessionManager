# Runbook — The Worker has stopped doing things

**When:** sessions stop appearing, emails stop going out, payment links stop being created, or
Job Run History has gone quiet.

**Why it works this way:** [`docs/worker-resilience.md`](../worker-resilience.md),
[`docs/job-run-history.md`](../job-run-history.md),
[`docs/job-schedule.md`](../job-schedule.md).

---

## First: is it actually stopped, or just quiet?

Silence is meaningful here. Every job is **scan-based and idempotent** — it diffs stored state
against a feed or a date threshold on each tick and writes nothing when there is nothing to do. A
job that finds no work deliberately leaves no `JobRunHistory` row, so an empty dashboard can mean
"nothing to do" rather than "broken".

1. **Admin → Job Run History.** Recent rows, and what their summaries say. A run recorded as
   `Success` can still have done nothing useful — read the job's own summary text, not just the
   status.
2. **Admin → Job Schedule.** Confirms when each job is expected to run.

## Is the process alive?

```bash
systemctl status vesessionmanager-worker
sudo journalctl -u vesessionmanager-worker -n 200 --no-pager
```

Also the file sink — Serilog writes to a **relative** `logs/` path, which resolves under the unit's
`WorkingDirectory`:

```bash
sudo ls -lt /opt/vesessionmanager/worker/logs/
```

(That directory is excluded from `rsync --delete` on purpose, so log history survives a deploy —
the change window is exactly when it is most wanted.)

## If the whole host died

.NET's default `BackgroundServiceExceptionBehavior` is `StopHost` — **one unhandled exception stops
every job**, not just the one that threw. Two known shapes:

- **A constructor throw** in an API client resolved inside a `BackgroundService`. This is why
  `ExamToolsClient` / `ZoomClient` / `DiscordEventClient` / `SquareClient` all defer credential
  checks to first *use*. If a new client validates in its constructor, that is the bug.
- **Anything thrown by a tick's own work outside `JobRunHistoryLogger`** — settings loads, queue
  peeks, `LastIngestionRunUtc` stamps. Web and Worker share one SQLite file, so a transient
  `database is locked` is enough. Every tick body is wrapped in `JobTick.GuardedAsync`; a new job's
  timer loop must use it too.

Look for the last exception before the host exited:

```bash
sudo journalctl -u vesessionmanager-worker --no-pager | grep -iE 'unhandled|stopping|fatal' | tail -20
```

## If the process is up but a job does nothing

| Check | What it means |
|---|---|
| Startup log has the **key ring verified** line | If it is missing or the guard threw, go to [`key-ring-problems.md`](key-ring-problems.md) — every credential may be an undecryptable blob |
| The integration is **unconfigured** | Zoom/Discord/Square/SMTP skip quietly with one aggregate INFO line and leave their tracking field null, so the next poll retries automatically. Set the credential; no backfill step is needed |
| One aggregate INFO line, repeating | That is the optional-integration pattern working as designed, not an error |
| Ingestion is throttled | The per-team refresh throttle is deliberate (see `TeamRefreshThrottle`'s own remarks) |
| A run reports success and changed nothing | Classic `ChangeTracker.Clear()` signature — a failed row detaching the rest of the batch. Detach the failing entity instead |

## If it was started by hand and behaves strangely

Two halves of the same trap:

- The Worker is a plain generic Host and reads **`DOTNET_ENVIRONMENT`**, not `ASPNETCORE_ENVIRONMENT`.
  Its default when neither is set is `Production`.
- The content root is the **current directory**, not the DLL's. Run the published DLL from anywhere
  but the app folder and it finds **no `appsettings` file at all**; the connection string is null,
  SQLite opens an anonymous temporary database, and the first symptom is `no such table: Teams` —
  which reads as a damaged database when the real one was never opened.

Always:

```bash
sudo -u vesessionmanager sh -c 'cd /opt/vesessionmanager/worker && exec dotnet ./VeSessionManager.Worker.dll'
```

Locally, use `dotnet run --project src/VeSessionManager.Worker`, never the raw `.dll`.

## On-demand switches

```bash
--verify-keyring          # read-only key-ring check; does NOT start the background jobs
--migrate-team-secrets    # encrypt still-plaintext credentials; idempotent
--run-uls                 # run the ULS watcher once, on demand
```

⚠️ `--verify-keyring` is the one that is safe against restored data. The others run real work
against whatever credentials the database holds.

## Escalation

If the host is stopping repeatedly and the cause is not one of the above, roll back to the last
known-good tag rather than leaving polling down — [`roll-back-a-release.md`](roll-back-a-release.md).
Ingestion being down is what everything else depends on.
