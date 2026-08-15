# Per-team integration switches

Issue #64. Run a real production team, a live-monitoring team, and a dev team pointed at ExamTools'
development environment **in one deployment**, and exercise one integration at a time without the
others emitting anything public or leaving a mess that is awkward to clean up.

## The shape

One master switch per team (`Team.IntegrationOverridesEnabled`, off by default) above four
per-integration switches (`ZoomEnabled`, `DiscordEnabled`, `SquareEnabled`, `EmailEnabled`, each on by
default).

**Master off means the individual switches do not apply at all.** That is not a simplification — it is
the property that makes the feature safe. Without it, a switch left off from an old testing session
stays hidden behind a collapsed panel and silently mutes a team that has since gone into production.
The corollary is the recovery path: turning the master off restores full normal operation in one
action, whatever the individual switches say.

`Team.IsEnabled(TeamIntegration)` is the only place that rule lives.

## Disabled is not unconfigured, and that is the whole design

Every gate sits next to an existing `IsConfigured` check whose established pattern is "skip quietly,
leave the tracking field null, retry next poll". Reusing it for a mute switch would make a disabled
integration re-attempt and re-log forever and never settle.

| | Meaning | Log | Retry next poll? |
|---|---|---|---|
| Unconfigured | admin has not finished setup | one quiet aggregate `INFO` per poll | **yes** — so adding credentials backfills |
| Disabled | deliberate, indefinite | once, on state change | **no** |

`TeamIntegrationState.ShouldCall` is what draws the distinction. It is a **singleton** because the
whole value is remembering across the scoped lifetimes background jobs create per tick; scoped would
log every tick, which is the behaviour it exists to prevent.

It is called **before** the `IsConfigured` check, deliberately: a team that has switched Zoom off
should not also be told it has not finished configuring Zoom. Both are true and only one is useful.

### The settle rule, and why it looks like a documented antipattern

CLAUDE.md warns against writing a gate as `!IsConfigured || succeeded`. The scheduling service now
reads:

```csharp
var zoomSettled = !zoomEnabled || session.ZoomMeetingId is not null;
```

**This is the mirror image of that warning, not an instance of it.** The warning is about
*unconfigured*, where the entire point is to keep retrying until credentials arrive. A deliberate,
indefinite switch must do the opposite: settle, never retry, queue nothing.

That distinction is also what closes **#289**. A team using Zoom but deliberately not Discord could
never satisfy the old "both ids non-null" rule, so the else-branch re-PATCHed every future session on
every poll — roughly 2,880 Zoom calls a day for ten sessions, forever, for data that had not changed.
Making "deliberately not Discord" expressible is what the settle rule needed.

## What each switch covers

Every call the system makes, not just the obvious one.

- **Zoom** — create, update **and delete**.
- **Discord** — create, update **and delete**.
- **Square** — link creation, `CompleteOrderAsync` (session marked complete), and
  `DeletePaymentLinkAsync` (the purge job and the youth-rate swap).
- **Email** — all team-scoped mail, gated once in `CandidateNotificationService.TrySendAsync` (the
  funnel every candidate email passes through), plus the FCC-fee reminder pass, the payment
  expiration notice, and VE session invitations.

**Not switchable, deliberately:** ExamTools ingestion, the ULS watcher, VE roster and exam-result
sync, VEC submission, session/candidate actions, the Square **inbound** webhook, and the PII purge.
All read-only, local-only, or both — and reproducing issues against them is the point of having a dev
team. Password-reset and VE self-service mail use the *deployment* sender, not a team's, so they are
outside this feature entirely.

## Accepted consequences

**A switched-off integration deletes nothing.** Teardown is suppressed along with creation: a
cancelled session's Zoom meeting and Discord event stay put, and the purge job leaves that team's
links alone. Anything created before the switch went off is **orphaned in the real account
permanently** and needs manual cleanup. Safe order when muting a team with live resources is
therefore **clean up first, switch off second**.

**No backlog on re-enable.** Work skipped while a switch was off is never queued.

One place where that rule and "do not write a false timestamp" pull against each other, stated
plainly rather than papered over: the FCC-fee reminder pass is skipped whole while email is muted and
deliberately does **not** stamp `FccFeeReminderSentUtc`. Writing it would claim an email was sent that
was not, and those timestamps are rendered to a Session Manager in the candidate's email history. The
trade is that a candidate still inside the reminder window when the switch goes back on will be
reminded then, rather than never. Muted candidate confirmations settle the other way — `TrySendAsync`
returns "nothing more to do" and the caller stamps — because there the stamp is the only thing
standing between re-enabling and a burst of stale mail.

## Making a muted team recognisable

A muted team's data looks exactly like real data, which is the entire risk. So the muted set is shown
on **Admin → Teams** beside the team name and as a banner on **Team Settings**, styled like the
deployment-wide test-mode banner for the same reason: a state that silently changes what the app does
must be visible without going looking.

**Not yet surfaced:** the session list's Team column and the team pickers. Worth adding — that is
where the confusion would actually happen — but it needs the muted set plumbed into those projections.

## Visibility and authorization

TeamAdmin and SystemAdmin only. As everywhere else in this app, hiding the control is not the
authorization check: `OnPostUpdateIntegrationSwitchesAsync` re-resolves the user and re-checks
`AdminAccessScope.CanManageTeam` server-side, and enforcement reads the master switch from the
database on every call.

The collapse of the individual switches behind the master is progressive enhancement only — the panel
is rendered expanded or collapsed server-side from the saved state, so with JavaScript unavailable the
controls are simply always visible and still submit correctly.

## Migration note

All five columns carry **database-level defaults**, not just C# initializers. The first generated
migration defaulted the four switches to `false` while the initializers said `true` — invisible while
the master is off, and then muting every integration at once the moment an admin turned the master on,
which is the exact opposite of what they just asked for. `IntegrationOverridesEnabled` needs its
default for a different reason: rows are created by raw SQL in the legacy-plaintext migration tests,
and a `NOT NULL` column with no default fails those inserts outright.
