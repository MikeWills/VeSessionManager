# Trigger points — configurable outbound messages

Issue [#401](https://github.com/MikeWills/VeSessionManager/issues/401). **PR1 of four: the engine,
with behaviour frozen.** The admin screen, the new triggers, Discord and the envelope fields follow.

## Why

Every outbound message this app sent had exactly one mode, and the code chose it. Four automatic
emails carried four hardcoded parameters — immediately, 24 hours, 5 days, 10 days — three more were
button-only, one went to the team's admin address through a special case inside
`PaymentReminderService`, and a team that wanted any of it differently had exactly one lever: the
all-or-nothing email mute switch. Needing that lever is the evidence. This is a public repo people
self-host, and "how long before the session do you remind people" is not a decision this codebase
should be making for them.

## The model

A **trigger point** is a moment when a condition first becomes true for a subject. A team defines
zero, one or many **rules** against it, each with its own template, recipient, channel and parameter.

Execution stays **scan-based and idempotent**. Nothing here became event-driven: a trigger is not a
signal that fires once, it is a condition a pass notices has become true, and a marker row is what
keeps noticing it from happening twice. A tick missed while the Worker was down costs nothing.

### `MessageRule` — what a team decided

Per team, and only per team (Mike, Q2). A VEC is a shared reference table here; the thing being
configured is what this team's candidates receive over this team's own SMTP.

The two fields worth explaining:

**`ParameterHours` is hours, never a calendar date.** This is [#220](https://github.com/MikeWills/VeSessionManager/issues/220)
made structural. The day-before reminder used to compare against "tomorrow" as a UTC calendar date.
Sessions run in the evening Eastern, and anything from ~8pm ET onward is already tomorrow in raw UTC —
so a Monday-evening session is stored on Tuesday, "tomorrow in UTC" is the session's own Eastern day,
and the "day before" reminder went out on the day of the session. Hours between two instants has no
calendar date in it, so there is no timezone to get wrong. There is no way to express a date in this
model, which is the point.

**`CreatedUtc` is load-bearing, not bookkeeping.** Every scan is bounded by it: a subject whose
trigger moment fell before the rule existed is never returned. That makes "adding a rule never fires
it for anyone already past the moment" true by construction rather than by somebody remembering
(Mike, Q4). The failure being designed against is his: *"nothing worse than sending out 3000 emails
because you added a new rule."*

How the bound is applied depends on the mechanism:

| Mechanism | Example | Bound |
|---|---|---|
| State | `CandidateRegistered` | `DateRegisteredUtc >= CreatedUtc` |
| Time-relative, anchor in the past | `FccFeeOutstanding`, `PaymentUnpaid` | `anchor >= CreatedUtc - P` |
| Time-relative, anchor in the future | `BeforeSessionStart` | `start >= CreatedUtc + P` |

`TemplateKey` is a string rather than a foreign key, for the same reason
`CandidateEmailSend.TemplateLabel` is one: a template renamed or removed must not take history with
it, and a team writes its own templates ([#144](https://github.com/MikeWills/VeSessionManager/issues/144)),
so the set is not fixed by what the code looks up.

### `MessageRuleRun` — the marker *and* the log

One row per (rule, subject), unique in the database.

It replaces a column and does one thing that column could not.
`Candidate.RegistrationConfirmationSentUtc` conflates three outcomes — sent, suppressed because the
team was muted, and never applicable — into one nullable timestamp, with no way to tell them apart
afterwards. `Outcome` is that distinction, and it is what closes
[#396](https://github.com/MikeWills/VeSessionManager/issues/396).

**Markers are keyed by rule, never by trigger.** "Remind at 7 days" and "remind at 1 day" are two
rules on one trigger; a per-trigger marker would let either mark the other done.

**Only `Sent` and `Suppressed` are terminal.**

| Outcome | Terminal? | Why |
|---|---|---|
| `Sent` | yes | Handed to SMTP without error. |
| `Suppressed` | yes | Email is switched off for the team. The settle-without-doing rule: nothing is queued while it is off, so re-enabling starts fresh rather than flushing a backlog. |
| `NoRecipient` | **no** | There was no address. An address filled in later should still get the message. |
| `Failed` | **no** | The render or the send failed. A failed send has always retried on the next tick, and that had to survive the move onto rules. |

A non-terminal row is still written — it is the log, and a failure nobody can see is what this table
exists to end — and the next attempt **updates it in place** rather than inserting a second one. The
unique index is what forces that; without it a flapping SMTP server would quietly grow a row per tick
per candidate.

**One thing that deliberately does *not* write a marker:** a team whose SMTP is not configured. That
is the optional-integration pattern — one aggregate INFO line, nothing recorded, and everything
waiting goes out on the first tick after credentials are entered. A marker there would turn "setup
unfinished" into "permanently skipped".

### Deliberate departures from the issue text

The issue describes a separate *trigger firing* record with rules hanging off it. **This is one
table.** The two guarantees the firing record was there to provide — no backfill, and per-rule
idempotency — both come from `MessageRule.CreatedUtc` plus the `(RuleId, SubjectId)` marker. A firing
row for a trigger with no rules attached records something nothing reads, and buys a join on every
scan.

The issue also calls manual compose screens "rules with no trigger". **Rules here always have a
trigger.** A nullable trigger puts a null case through every scan and dispatch path to serve a display
grouping; the rules page gets its "No trigger" group in PR2 by listing templates no rule references.

## The engine

`src/VeSessionManager.Core/Messaging/`.

- **`MessageTriggerDefinitions`** — the registry. Per trigger: mechanism, subject type, default
  parameter, legal recipients, placeholder set. One file, the way `Jobs/JobSchedules.cs` is one file
  and for the same reason: those numbers used to be literals at the call site that needed them, so a
  second reader could only restate them and be wrong the first time one changed.
- **`IMessageTriggerScanner`** — one per trigger. Given `(team, rule, now)` it returns the subjects
  whose condition is met, excluding any with a terminal run for that rule, bounded by `CreatedUtc`.
- **`MessageDispatchService`** — the single send path. Renders through the existing
  `EmailTemplateRenderer` and never a second renderer; a hand-rolled `Replace` chain is what shipped
  without HTML-encoding in [#260](https://github.com/MikeWills/VeSessionManager/issues/260).
- **`MessageRuleService`** — one team's pass, optionally narrowed to some triggers.
- **`Worker/MessageRuleJob`** — daily, on `PerTeamDailyJob`. Replaces `DayBeforeReminderJob`.

`TeamPipeline` also runs a pass, **scoped to `CandidateRegistered` alone**. It runs on the ~5-minute
ingestion tick and from the session-detail refresh button, and running the whole rule set there would
quietly move the pre-session reminder off its daily job and onto whenever somebody pressed refresh.

### Every guard the old code had is in a scanner

This is the part where a missed line is a live incident, so each was lifted with its comment:

| Scanner | Guards |
|---|---|
| `CandidateRegisteredScanner` | `PiiPurgedUtc == null`, `Email != null`, team, `Status == Active`, the 24-hour recent-session query bound, and the in-memory `!Session.HasEnded(now)` skip |
| `BeforeSessionStartScanner` | the rolling instant window, never a calendar date |
| `FccFeeOutstandingScanner` | `FccPaymentStatus == PendingVerification`, `TerminalStatuses` exclusion, `PaymentEligibilityWindow.CutoffUtc` |
| `PaymentUnpaidScanner` | `Status == Unpaid`, `PaymentEligibilityWindow.CutoffUtc`, **both** branches of the retest OR |

**`PaymentUnpaidScanner` must not filter on `ExpiredUnpaid`, and that is the sharpest edge in the
change.** The expiry write stayed in `PaymentReminderService` — it is local bookkeeping that has to
keep happening for a team with no rule, or with email switched off, or nothing stops a dead payment
link being treated as live. But that means by the time the rule scans, the flag is normally already
set. Filtering on it would mean the notice silently never went out, and nothing would look wrong: the
flag set, the payment expired, and no email to miss. Idempotency here comes from the marker, which is
the whole point of the marker. `Expiration_FlagAlreadySetButNoRunMarker_StillSendsTheNotice` exists
for exactly this line.

## What changed for a muted team

`TeamIntegrationState.ShouldCall` is checked once, in the dispatcher, and a muted send records
`Suppressed`. Two consequences worth knowing:

- **The FCC-fee reminder now settles when muted.** It used to be the one exception — skipped whole and
  deliberately *not* stamped, so a candidate still inside the window would be reminded once the switch
  went back on. That trade is recorded in `docs/team-integration-switches.md` and no longer holds:
  every muted send settles, uniformly. Confirmed with Mike.
- **The three on-demand buttons now refuse instead of reporting success.** `Resend…`,
  `SendYouthProgram…` and `SendFelonyDisclosure…` returned `Sent` for a muted team and sent nothing,
  because the send path they shared with the jobs had to answer "nothing more to do" for a job's
  benefit. That is right for a poll pass and a lie to somebody standing at a button. With the jobs
  gone from that path, `CandidateEmailSendResult.EmailMuted` is three lines.

## One other behaviour change, and it is a fix

**A team with no SMTP now still expires stale payment links.** The old expiration pass returned early
when a team had no SMTP credentials, so a deployment that never configured email also never expired a
link — the bookkeeping was hostage to the notice. Splitting them fixed that as a side effect.

## Deploying it

Two independent guards stop the first tick after deploy mailing everyone mid-cycle. Either alone would
do; both is deliberate.

1. **The `MessagesRules` migration seeds four rules per existing team with `CreatedUtc` = migration
   time.** Nothing whose moment already passed can fire. The cost, accepted: a candidate who
   registered this morning and has not had their confirmation yet never gets one.
2. **The same migration backfills a `MessageRuleRun` per message already sent** — from
   `RegistrationConfirmationSentUtc`, `DayBeforeReminderSentUtc`, `FccFeeReminderSentUtc`, and from
   `Payment.ExpiredUnpaid` (the expiration notice never had a timestamp column; that flag *was* its
   guard, so the backfilled marker uses the rule's creation time and says so in `Detail`).

Both are raw SQL, invisible to the compiler and to EF InMemory — a backfill that resolves nothing
looks exactly like one with nothing to do. `MessageRuleSqliteTests` drives them against real SQLite,
the same way the `AuditLog.TeamId` backfill is (`docs/audit-log.md`).

`EmailDefaultsSeeder` seeds the same four rules for teams created later, idempotent per
(team, trigger). **A deleted rule is seeded again on the next Worker start** — same as a deleted
template. Nothing in PR1 can delete one, but PR2's admin screen should disable rather than delete, or
this guard needs a tombstone. Pinned by
`ADeletedRule_IsSeededAgain_WhichIsWhyTheAdminScreenShouldDisableRatherThanDelete`.

## Left half-migrated on purpose

`PaymentReminderService.ReminderThresholdDays` / `ExpirationThresholdDays` still exist. The Applicant
Status page colours its "days pending" column on both boundaries, and those colours are meant to
*explain* what the app does. Once the hours are per-team, a page reading a constant shows a red row on
a day nothing happens. PR2 makes the parameters editable and has to resolve that coupling in the same
change — it is the one non-obvious dependency in the whole issue.

## Still to come

- **PR2** — the admin screen (trigger points, each expanding to its rules; `EmailTemplates`' phase
  grouping deleted, since it lies — "Pre-session" contains a button), and `ParameterHours` becomes
  editable.
- **PR3** — `CandidateTested` and `LicenseGranted`, moving `GettingStartedLocally` and the ARRL youth
  email from button-only to optionally automatic. `FelonyDisclosureInstructions` gets a trigger too,
  **defaulting to off**: [#221](https://github.com/MikeWills/VeSessionManager/issues/221) deliberately
  took it off an automatic path because it always arrived after the exam, when the candidate could no
  longer ask anyone — so its trigger is *declaration*, not completion.
- **PR4** — Discord channel posts (`FanOut = PerSubject`; the unsubscribe and CAN-SPAM footer are
  per-person concepts and must not reach a channel post), and the envelope: `From` constrained to the
  sending domain (SPF/DKIM/DMARC), `Reply-To` resolvable to the session lead, `Cc`/`Bcc` with a
  once-per-run monitoring copy.

Password reset, VE self-service sign-in links and VE email-change confirmations stay **outside** this
model as action-based sends. They carry access tokens, and
[#207](https://github.com/MikeWills/VeSessionManager/issues/207)'s "no monitoring Bcc" guarantee is
structural today precisely because those call sites never populate the field. Bringing any of them in
would turn that into a runtime guarantee.
