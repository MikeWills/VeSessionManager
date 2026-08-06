# Job Run History — what a run actually did

`JobRunHistory` originally recorded only `Success` and `ErrorMessage`. That made three very
different outcomes **identical** on Admin → Job History:

1. sent five emails,
2. sent none because nothing qualified,
3. **sent none because every attempt failed.**

The third is the dangerous one, and it rendered as a green `Success` chip like the other two.

## Why "Success" is right, and still misleading

A job is marked successful when the *job* completes. Per-item failures are caught **inside** the job
on purpose — one bad address must not abort a batch of fifty — so a run where every single send threw
still completes normally and still records `Success`.

That is the correct behaviour for the job. It was simply invisible on the dashboard.

**This cost a real evening on 2026-08-05.** SMTP had been configured with `smtp.mailgun.com` instead
of `smtp.mailgun.org`; every send died in the TLS handshake on a certificate name mismatch. Job
History showed an unbroken wall of green across both teams, all day. The Worker log had said
`sent 0, failed 1` the whole time — the information existed, it just never reached the database, and
the beta box's log is behind Tailscale where it could not casually be read.

## The fix: record the job's own summary

`JobRunHistory.ResultSummary` holds the result object's `ToString()`.

Nearly every result type in this codebase already overrides `ToString()` to produce the one-line
summary the Worker log prints — `"sent 0, failed 1"`, `"reminders sent 0, expirations processed 0"`,
`"1/1 checked, 0 lookup failure(s)"`. So this captures **text that already existed**, rather than
inventing a second description that would drift from the first.

`JobRunHistoryLogger` gained a generic overload:

```csharp
public Task RunAsync<TResult>(string jobName, Func<CancellationToken, Task<TResult>> job, …)
public Task RunAsync(string jobName, Func<CancellationToken, Task> job, …)
```

Jobs whose work returns a result get a summary with **no call-site change at all**; jobs that return
nothing leave it null rather than inventing one.

> **The trap, and why there is a test named after it.** Every real call site passes a *method group*
> — `purgeService.RunAsync` — not a lambda. A method returning `Task<T>` converts to **both**
> `Func<CT, Task>` and `Func<CT, Task<T>>`. Had overload resolution preferred the void overload,
> every summary would have silently stayed null: it would compile, the other tests would still pass,
> and the dashboard would be exactly as uninformative as before.
> `RunAsync_MethodGroupCallSite_BindsToTheResultOverload` writes the call the way the real jobs write
> it and asserts the summary is captured.

The summary is capped at 500 characters. These are one-liners today, but a future result type is one
careless interpolation away from putting a wall of text in every row.

## What it does not do

It does not turn a run with per-item failures red. Deciding that generically would mean parsing
free-text summaries for the word "failed", which is exactly the kind of fragile inference that breaks
the first time a result type is reworded. A human reading `sent 0, failed 1` needs no classifier.

If a stronger signal is ever wanted, the honest way is for result types to expose a `HasFailures`
property the logger can read — a real contract rather than string-sniffing.
