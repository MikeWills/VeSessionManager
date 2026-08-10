# ExamTools reconciliation (2026-08-10)

A nightly check that ExamTools and this database still agree, and a page listing where they don't.

## Why it exists

Every other job here trusts ingestion to have worked. Nothing checked.

The historical import had been dropping **the last day of every calendar month** since it was
written — an exclusive end bound on ExamTools' closed-session feed meeting an inclusive month-end
from the chunker. Roughly twelve sessions a year, per team, plus their candidates and VE roster
links, going back as far as anyone had imported. Nothing failed at any point: the requests
succeeded, the responses were valid, and the data simply was not there.

It surfaced because HRCC's own Discord bot reads the same ExamTools API directly and disagreed about
whether a VE was still active. A VE had worked on 31 May 2026; this app's directory said his last
session was the previous August. See [`docs/examtools-api.md`](examtools-api.md) for the bound
itself.

**This job is that accident, done deliberately.** The cross-check that found the bug was a person
noticing two systems disagreeing; making it a job means nobody has to notice.

## What it does

Per team, once a day: ask ExamTools for closed sessions over a trailing window (120 days), ask the
database for the same window, and record the differences.

| Finding | Meaning |
|---|---|
| **Missing session** | ExamTools has a closed session this app never ingested. |
| **Candidate count** | The session exists here, but ExamTools reports *more* applicants than we hold. |

Only "remote has more" counts as a candidate discrepancy. Fewer is normal — a withdrawn candidate is
removed at ExamTools and deliberately kept here — and flagging it would fill the page with noise
that is working as designed. A page full of noise gets ignored, which would defeat the point.

**It is read-only.** It records what it sees and repairs nothing. Fixing means re-importing a range,
which is real load on somebody else's API, so the page offers the button and a human presses it. A
monitor that starts fetching months of history on its own is not a monitor.

## Why there is a table and a badge, not just a log line

The sweep could have stopped at a Job History entry reading *"3 sessions missing"*. That was
rejected for reasons this codebase has already paid for once:

- **Job History rotates**, so a finding disappears without being fixed.
- **The row renders green**, because the *job* succeeded — the same shape that had the Worker
  printing `sent 0, failed 1` all day while the dashboard showed success, and cost an evening
  chasing "no emails are being sent". `JobRunHistory.ResultSummary` exists because of that incident;
  it is not enough on its own.
- **A count inside a sentence cannot be acted on.** Detection was the hard part of the original bug;
  the repair was one re-import, and the only real work was translating "31 May is missing" into a
  date range. The findings page does that translation and offers the button.
- **Nobody opens an ops page speculatively.** The nav badge is what makes an unprompted problem
  visible, because "ExamTools has sessions we don't" is not something anyone thinks to go and check.

## How a finding behaves

A finding is a **standing fact, not an event**. The same missing session seen on ten consecutive
nights is one row whose `LastSeenUtc` moves, not ten rows — otherwise the list grows without bound
and its size stops meaning anything. Unique on `(TeamId, Kind, ExamToolsSessionId)`.

When the discrepancy goes away it is stamped `ResolvedUtc` rather than deleted, so "this was wrong
and is now fixed" stays visible. If it returns, the same row reopens.

**A finding that ages out of the window is left alone, never resolved.** Nothing was fixed — we
stopped looking — and silently clearing it would be the most misleading thing this job could do.
That has its own test.

On an open finding the page shows `last seen`, because a stale date there means **the sweep stopped
running**, not that the problem went away. An empty list and a dead job look identical otherwise,
which is why the empty state points at Job Schedule.

## Limits worth knowing

- **It compares against the remote feed, so a bug shared by both sides stays invisible.** It would
  have caught the date-bound bug regardless, because the sweep's window is wider than any single
  import chunk — but that is luck as much as design.
- **It cannot be a test.** It needs live credentials and a real network call, so it can't gate a PR.
  `ReconciliationServiceTests` covers the bookkeeping — noticed once, refreshed while it persists,
  resolved when fixed, reopened if it returns — against a fake. Whether ExamTools agrees with our
  assumptions is a question only the live feed answers, and the original bug had a full green suite
  precisely because the fakes shared the wrong assumption.
- **The window is 120 days.** Long enough that a gap has several chances to be seen before ageing
  out, short enough to stay one cheap call per team per night. Anything older needs a wider manual
  check.
