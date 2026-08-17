# Trigger points — configurable outbound messages

Issue [#401](https://github.com/MikeWills/VeSessionManager/issues/401). **PR1: the engine, with
behaviour frozen. PR2: the admin screen, and the parameters become real. PR3: three new trigger
points.** Discord and the envelope fields follow in PR4.

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

`EmailDefaultsSeeder` seeds the same four rules for teams created later. **Once per team, and it
records that it has** (`Team.MessageRulesSeededUtc`) — unlike the templates beside it, which are
checked row by row and so do come back if deleted. That difference is deliberate and is what makes
deleting a rule stick; see the PR2 section below.

---

# PR2 — the admin screen, and the parameters become real

## Message Rules

`Admin/MessageRules` lists every trigger point, each with its rules; `Admin/MessageRuleEdit` handles
one. Same team-picker/lock as Team Settings and Email Templates, and deliberately no "All teams"
option — this edits one team's configuration.

**Every trigger point renders, including the ones with no rules.** A section that appears only once
something is configured is one nobody discovers, and "we could email people at this moment and
currently do not" is the most useful thing the page has to say. Same reasoning as the alerts bell
rendering empty rather than disappearing (#339).

**Switch off and delete are both offered, and they answer different questions.** Switching off is
"not right now" — the rule keeps its name, hours and place on the screen, and switching it back on
resumes from that moment. Deleting is "we do not do this", and it is a real delete.

The first draft of this screen had only "switch off", because a delete would have been undone: the
seeder asked "does this team have a rule for this trigger?" and added one if not, so a deleted rule
reappeared on the next Worker start. Mike's response was the right one — *you cannot offer disable in
place of delete* — so two things changed rather than the screen being narrowed to fit them:

- **The seeder seeds once per team and records it** (`Team.MessageRulesSeededUtc`). Setting a new team
  up is a one-time act, not an invariant to maintain, and a team that wants nothing sent at a trigger
  point is entitled to have nothing there. Backfilled for existing teams by the `MessageRuleDeletion`
  migration — without that line, the first Worker start after deploy hands every team a second full
  set.
- **`MessageRuleRun.MessageRuleId` became nullable, with `SetNull`.** The record that real people were
  emailed outlives the rule that emailed them, which is what `RuleName` and `Trigger` were snapshotted
  onto the row for in the first place. `Restrict` would also have protected the log — by refusing the
  delete, which makes the log a reason an admin cannot tidy up their own configuration.

An orphaned run guards nothing, and that is correct: it belongs to a rule that no longer exists. So
re-creating a deleted rule starts clean, and **its own `CreatedUtc` is what stops that reaching
anybody whose moment has passed** — "delete and re-add" cannot become a way to re-email everybody.

**Nothing changes a rule's trigger.** The markers that stop a rule firing twice are keyed by rule, and
their `SubjectId` means a candidate for one trigger and a payment for another — so moving a rule
between triggers would reinterpret every marker it already has. Create a second rule instead.

**An edit leaves `CreatedUtc` alone.** Refreshing it would mean a typo corrected an hour later
silently skips everybody whose moment fell in between. The consequence, stated on the edit page
itself: widening a 24-hour reminder to 48 reaches people not yet reminded, never people already
reminded at 24.

`MessageRuleAdminService` refuses four things, and each describes a rule that would look configured
and do nothing: a blank name, a time-relative trigger with no hours (or hours outside 1…8760), a
recipient the trigger cannot address, and a template key that does not exist on that team. The last
two matter most — a registration confirmation addressed to the team's own inbox is a mistake rather
than a configuration, and a rule pointing at a missing template records `Failed` every night with only
a log line to show for it. The list page flags that case on the row if it happens anyway.

## The coupling this PR had to resolve

Two things outside the engine were reading the constants that became per-team data. Both are supposed
to *agree* with what the app does, so both now read the same rows the scanners read, through
`MessageThresholdService`:

- **`ApplicantStatus`'s amber/red "days pending" colours.** These referenced
  `PaymentReminderService.ReminderThresholdDays`/`ExpirationThresholdDays` rather than re-declaring
  them, precisely so they could not drift. Once a team sets its own hours, a constant *is* the drift.
  The page merges teams, so this is resolved per row. **A team with no enabled rule gets no colour at
  all** — nothing is going to happen on any particular day, so there is no boundary to warn about.
- **The payment-expiry write.** It stayed in `PaymentReminderService` (PR1), but a fixed 10 days
  beside a notice a team set to 30 would mean telling somebody their link expired on a day it did
  not. It reads the team's `PaymentUnpaid` hours, falling back to the trigger's default — which is
  the number the constant held — when there is no rule, or the rule is switched off. Expiring is
  bookkeeping and must keep happening either way; that is the whole reason the two were split.

The two lookup methods differ in exactly one respect and it is deliberate: bookkeeping needs a number
regardless (`HoursOrDefaultAsync`), a page must be told "no boundary" rather than handed a default it
would then colour a row on (`ConfiguredHoursAsync`).

## A ceiling the form cannot enforce

Both money triggers are bounded by `PaymentEligibilityWindow` — 30 days from the session start —
which exists so the historical import's backfilled candidates are never chased about payments for
sessions they sat months ago. **A rule set past that simply stops firing**, and the form's own ceiling
is a year.

It is shown as a caution beside the hours field rather than refused, because there is no honest number
to validate against: the real headroom is 30 days *minus* however long the FCC took to enter the
application, which nobody knows in advance. Worth remembering when the window itself is next changed —
it now bounds a value teams can set, not only one the code chose.

## Email Templates stopped describing triggers

`EmailTemplateTriggers` used to describe all seven templates, grouped into three hardcoded phases,
with conditions in prose: "within the next 24 hours", "5 days", "10 days". It lied in two directions
at once — "Pre-session" contained a template only a button sends, and the numbers were one
deployment's defaults presented as the app's behaviour.

The phase grouping is gone. The list now has two groups, read from the rules: **sent automatically**
(with each rule's own schedule on the row) and **not sent by any rule**. What is left in the registry
is only the three on-demand templates, which no rule can describe because a person decides. The
`Retired` set stays — "nothing in the code sends this" is a different fact from "this team has no rule
for it", and only the first is the app's to state.

---

# PR3 — three new trigger points

`CandidateTested`, `LicenseGranted` and `FelonyDisclosureDeclared`. These reproduce nothing: they are
moments the app could not act on before, so **none of them is seeded** and an existing team's outgoing
mail is unchanged until somebody creates a rule. A team set up after PR3 gets the same four rules a
team set up before it has.

## Each one needed a moment it could be bounded by

`MessageRule.CreatedUtc` bounds every scan, so a state trigger is only implementable if something
records *when* the state changed. That question had three different answers here.

**`CandidateTested` needed a new column.** `Candidate.Tested` is a bool written from four places —
marking a session completed, marking one candidate failed, and both branches of the automatic
exam-result sync — so there was no answer to "when did this become true". The nearest existing
candidates were each some other moment wearing this one's name: `ResultMarkedUtc` is only set by a
Session Manager's explicit result, never the automatic path, and the session's own start is hours to
days before grading actually happens. `Candidate.TestedUtc` records it, set through
`Candidate.MarkTested(now)` — one helper rather than two assignments at four sites, because a site
that sets the bool and forgets the timestamp leaves a candidate this trigger can never see and
*nothing fails*. `NoRawTestedAssignmentTests` fails the build if a raw assignment reappears.

It is **deliberately not backfilled**. Everyone already tested holds null and is never returned, which
is the same direction of safety as `CreatedUtc` itself and removes the need for an age window on this
trigger at all.

**`LicenseGranted` uses FCC's own grant date**, which already exists. One consequence worth knowing:
that date is date-only, stamped at UTC midnight, so a rule created at 2pm today will not fire for a
license FCC dated today — its moment reads as this morning. A day of the safe direction, and the
alternative was a second column recording when the watcher noticed.

**`FelonyDisclosureDeclared` uses registration**, because the disclosure arrives with the application
and the answer does not change afterwards.

## The guards each one owes

- **`LicenseGranted` skips an upgrader whose license predates the session** — they did not earn a call
  sign here, and "congratulations on your new call sign" is wrong for them. Checked in memory, since
  `LicenseGrantPredatesSession` needs the session loaded. It also requires a call sign to exist at
  all: `{{CallSign}}` is the reason this trigger exists, and this is the only point at which it
  resolves to anything.
- **`CandidateTested` skips a withdrawn candidate** (`ApplicationStatus.NotTested`), who never sat
  anything whatever a bulk "mark session completed" left on the row. And note what this trigger does
  *not* mean: the exam was sat, not passed — the result is often unknown for days.
- **`FelonyDisclosureDeclared` requires `HasFelonyDisclosure == true`**, not merely non-false. Null
  means ExamTools told us nothing, and telling the wrong person their felony disclosure needs FCC
  paperwork is the mistake worth guarding twice — the button's own handler already does. It carries
  the same recent-session bound as the registration confirmation, because the advice is only useful
  while there is still someone to ask.

## Where this leaves the buttons

The three on-demand sends stay exactly as they are; a rule is an addition, not a replacement.
`EmailTemplateTriggers` still describes each button, but its wording no longer claims exclusivity —
"never sent automatically" stopped being true the moment a team could put `GettingStartedLocally` on
`LicenseGranted`.

**One asymmetry to know before putting the ARRL youth email on a rule:** the button checks that the
session's VEC runs a youth program and refuses otherwise. A rule has no such check, because rules
carry no conditions of their own beyond their trigger (Mike, Q3) — so a rule sends it to everyone
granted a license. The template registry says so where somebody choosing it will read it.

## Still to come

- **PR4** — Discord channel posts (`FanOut = PerSubject`; the unsubscribe and CAN-SPAM footer are
  per-person concepts and must not reach a channel post), and the envelope: `From` constrained to the
  sending domain (SPF/DKIM/DMARC), `Reply-To` resolvable to the session lead, `Cc`/`Bcc` with a
  once-per-run monitoring copy.

Password reset, VE self-service sign-in links and VE email-change confirmations stay **outside** this
model as action-based sends. They carry access tokens, and
[#207](https://github.com/MikeWills/VeSessionManager/issues/207)'s "no monitoring Bcc" guarantee is
structural today precisely because those call sites never populate the field. Bringing any of them in
would turn that into a runtime guarantee.
