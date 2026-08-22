# Trigger points — configurable outbound messages

Issue [#401](https://github.com/MikeWills/VeSessionManager/issues/401). **PR1: the engine, with
behaviour frozen. PR2: the admin screen, and the parameters become real. PR3: three new trigger
points. PR4: Discord channel posts, and the envelope.** That completes the issue.

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

**The form asks for days; the column still stores hours.** Nobody sets a reminder in hours — they set
it "a day before" or "five days after the FCC got it" — and 120 is a number a team should not have to
work out. `MessageDelay` is the single place the ×24 happens, converting at the page boundary and
nowhere else, so the model the scanners compare instants in never changes unit. Two things about that
field are deliberate. **Halves are allowed** (`step="0.5"`, minimum half a day): a whole-numbers-only
day field would have quietly removed "12 hours before the session", which the hours field could always
express and which is a real thing to want. **Anything finer is refused, not rounded** — 0.3 days is
7.2 hours, and a form that silently stores 7 has moved somebody's rule to a moment they did not choose
while reading back as though they had. A stored value that is not a whole number of half-days can only
predate this field; it renders in hours in the list rather than being shown as a decimal the list is
not entitled to invent.

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

## A completion-only Tested is an assertion, not evidence (#419)

"Mark session completed" flips every non-terminal candidate on the roster to Tested — including a
no-show whose ExamTools removal has not ingested yet, which the app cannot know is coming. Found live
the first night on `v0.13.0`: a no-show was marked Tested by the completion click, then deleted in
ExamTools and re-registered on a new session, and the old row was stranded — `Tested + Unmatched`,
immune to withdrawal (`!c.Tested`), immune to Delete (same refusal), permanently on the Pending FCC
grant list beside the real row on the session they actually sat. Left alone it would eventually have
become a **phantom grant**: the ULS watcher matches by FRN, both rows march to Granted together, and
the purge scrubs both.

`Candidate.TestedWithEvidence` is the distinction both guards now use: a graded result
(`NewLicenseClass`), a terminal verdict, or a human marking **this specific candidate**
(`ResultMarkedByUserId`). A Tested with none of those came only from completion — an assertion about
the roster, not about a person — and the feed removing that person is exactly the correction it is
entitled to make. Withdrawal and Delete both proceed for such a row and undo the Tested mark
(`UndoCompletionTested`, in the one file allowed to assign `Tested`), because a NotTested row still
reading Tested would haunt every Tested-keyed list and trigger.

Two boundaries deliberately kept:

- **A graded candidate is still never withdrawn or deleted.** The evidence predicate is the fence;
  weakening it re-opens the original data-loss risk the `!c.Tested` guard existed for.
- **An already-stranded row needs the Delete button, not a poll.** A completed-and-closed session's
  roster is never synced again, so ingestion cannot repair rows stranded before this fix — the row's
  own Delete action, which now accepts completion-only Tested, is the repair.

### The ruling that followed (same day)

> "If it's not from the feed, it's not truth. Only from the feed is truth."

"Mark session completed" no longer marks anyone Tested at all. It marks the **session** —
`TestingCompletedUtc` — and nothing about any person; `Tested` comes from ExamTools' graded results
(`ExamResultSyncService`) or a human marking a specific candidate, never from a bulk assertion about
whoever happened to still be on the roster when the button was pressed. The completion outcome message
stopped claiming "N candidate(s) tested" for the same reason, and the `CandidateTested` trigger blurb
names the graded result as its only source.

`TestedWithEvidence` and the withdrawal/Delete correction stay: rows marked by completion before this
change exist in real data, and they are exactly what those guards repair.

## One dispatcher, several kinds of trigger (#417)

#415 fixed the history but left the cause: a candidate-facing email could be sent from two unrelated
code paths that recorded it two different ways. The formatter needed per-column fallbacks and a dedup
rule because the felony column had **two writers** and the youth column had one the engine knew
nothing about.

This should have been part of #401. PR1's plan listed the hand-sends as *untouched* to keep that PR
behaviour-frozen, and #396 was then answered by bolting a mute check onto each of them rather than
moving them. That was defensible for PR1; not following through in PR4 is what cost #415.

**Scanning and dispatching are separable, and only scanning is scan-shaped.** A button press is a
perfectly good trigger — it is just not a scheduled one. `CandidateNotificationService.TrySendAsync`
was already the single funnel for every templated candidate email that service sends, so it is the
one place that changed: on a successful send it now writes a `MessageRuleRun` with
`MessageRuleId = null`, the same nullable column that lets a run outlive a deleted rule.

Three things worth carrying forward:

- **`MessageTrigger.SentByHand = 100` is not a trigger point.** It is a note on a run saying a person
  pressed a button. Numbered clear of the scan triggers and deliberately absent from
  `MessageTriggerDefinitions.All`, so the admin screens — which iterate that list — can never offer it
  as something configurable. `For()` throws for it because nothing should ask, and
  `MessageRuleAdminService` now refuses it as validation rather than letting that throw become a 500.
- **A hand-send records the real trigger where one exists.** A resent confirmation is a
  `CandidateRegistered` message however it was set off, and the felony instructions record
  `FelonyDisclosureDeclared` whether the button or the scanner sent them — which is what makes the two
  paths indistinguishable to everything downstream. Only the youth instructions, which no trigger can
  send, carry the marker.
- **The run is written after the send, inside the funnel.** A missing template leaves no trace claiming
  otherwise — the same property #396 was about, arrived at from the other direction.

The backfill migration covers rows written before this, guarded by `NOT EXISTS` so a felony email a
rule already recorded is not listed a second time under a different name. **It is a no-op on the
current beta data** — all three columns are empty there — so it was verified against synthesized rows
on a copy of that database rather than trusted to run clean.

The legacy columns are still written and now have no readers. Dropping them is a separate change,
once that has been confirmed; leaving them means a rollback here is a code revert.

## Email history reads the run log (#415)

A candidate's Email history was built from the legacy `Candidate.*SentUtc` columns. It kept working
after #401 only because the dispatcher still stamps those columns for the four migrated triggers —
and it failed the moment a team used anything #401 added. Three symptoms, one cause:

- a rule on `CandidateTested` or `LicenseGranted` has **no column at all**, so its mail was sent and
  the candidate's page showed nothing;
- **two rules on one trigger share one column**, so "remind at seven days" and "remind at one day"
  collapsed into a single line carrying whichever timestamp landed last — a team configured two sends
  and could see one;
- the FCC fee reminder stamps `Candidate.FccFeeReminderSentUtc`, which this list **never read**, so it
  has never appeared at all.

It reads `MessageRuleRun` now, labelled with the rule's own name — "Reminder 24 hours before the
session" rather than "Reminder email", which says *which* rule. Four things about the filter:

- **Only `Sent`.** A `Suppressed` or `Failed` row is real history, but this list answers "what has this
  person received", and listing either as received is the same lie #396 was about.
- **Only mail addressed to the candidate, over email.** `CandidateTested` and `LicenseGranted` may both
  address the team's own inbox — a message *about* someone, which they never saw — and a Discord rule
  reaches a room rather than a person.
- **`PaymentUnpaid` needs no exclusion**, because its subject is the *payment*, not the candidate, so
  it never matches. Structural rather than a filter somebody has to remember.
- **A run whose rule was deleted is kept.** `MessageRuleId` is nullable precisely so history outlives
  the rule; dropping those would undo that on the page where it matters most.

Three sends still come from a column, because no run exists for them: the Youth Program instructions
(no trigger can send it), the payment reminder (whose column has no writer left at all — purely
historical), and the felony instructions when sent by hand. That last is written by **both** the
button and `FelonyDisclosureDeclaredScanner`, so the column is shown only when no run covers it.

The loader is batched (`CandidateRuleSends.LoadAsync`) because the session Detail page renders a row
per candidate; per-row lookup would be an N+1 across a full roster.

**Once nothing reads them, the four legacy `...SentUtc` columns can be dropped** — but not in the same
change, and only after confirming nothing else does.

## Attaching a rule from the template (#409)

Two objects — a template holds the wording, a rule holds the timing — read as two *steps*, and the
step people got stuck on was the second one. Mike, on first use:

> "Rather than treat a template go to a whole new page. Find that template in a list of 30 of them and
> create the rule."

The model stays. It is what lets one message go out at five days and again the day before, and what
lets a template exist with no schedule at all for hand-sending. What changed is that the template is
now where you attach from: **Add a rule** on both the list and the editor, carrying the template into
the create form, and present whether the template has no rules or three — before this it appeared only
at zero, so there was no path to a second rule that did not go through Message Rules and a search.

The one-rule case on the editor is untouched. That schedule panel is editable in place, and it is the
case where the wording and its timing genuinely are one job.

**The link carries the template's `Id`, not its `Key`.** The generated `Custom.<slug>` key is
deliberately never rendered — it is the mechanism keeping team-defined templates from colliding with
the ones code looks up by name — and a query string is as public as a table cell. A test asserting
that the key never reaches the page is what caught the first attempt.

### A rule can only send a candidate template

`ValidateAsync` checked that a template existed on the team and nothing else, so a
`VolunteerExaminers` template could be attached to a candidate trigger. Every scanner's subject is a
candidate or a payment, so `MessageSubject.Placeholders` only ever carries candidate tokens: the
message would render with every one of its VE tokens blank and **send successfully**. Now
`TemplateAudienceMismatch`, refused in the service and not offered in either picker, with no "Add a
rule" on a VE template at all — an affordance leading straight to a refusal is worse than none.

Refused rather than supported. A rule addressing the session lead with VE wording is a coherent thing
to want, but nothing renders those placeholders yet, and building it here would have been a second
feature hiding inside a wiring change.

### One Razor trap worth keeping

`@(cond ? "Add a rule…" : "Add another rule…")` renders the ellipsis as `&#x2026;` — Razor
HTML-encodes the *result of an expression*, while the same character sitting in literal markup is
written through untouched. Harmless in a browser and invisible to a reader, but it silently breaks any
test asserting on the text, which is how it was found. Two literal branches instead of a ternary.

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

---

# PR4 — Discord channel posts, and the envelope

## Posting to Discord

A rule's `Channel` can be `Discord`, with its own `DiscordChannelId` — per rule rather than per team,
so session reminders can go to #announcements and new-licensee congratulations to #general. The guild
is still the team's; the bot is only in one per team.

**Nothing per-person can reach a channel post, structurally.** The plan for this PR named the risk —
the unsubscribe link and CAN-SPAM footer are properties of writing to one person, and a room full of
people is not that. The answer is that the Discord path builds no `EmailMessage` at all, so there is
no field to put them in, rather than a check somebody has to remember. It writes no
`Candidate.…SentUtc` either: those columns mean "this candidate was emailed".

**`MessageFanOut.PerSubject` was renamed to `SingleDigest`** (value unchanged, so nothing stored
moves). The old name read as "one per candidate", which is the opposite of what it selects and
precisely the forty-posts mistake the field exists to prevent. A digest renders the template once
against `{{Count}}` and `{{Subjects}}`; per-candidate tokens have no answer there and render blank,
which the form says.

**Markers stay per subject even for a digest.** One post covering twelve candidates writes twelve
rows — a single marker keyed to the post would leave eleven looking unsent, and the thirteenth
candidate to arrive would re-announce the first twelve. A failed digest marks *nobody*: recording
some as sent would record that a post said something it never said.

`DiscordMessageText` converts the rendered HTML to something readable in a chat window. Two decisions
in it are worth knowing:

- **It does not escape Discord markdown.** The obvious next step — backslash every `_`, `~`, `` ` ``
  so a candidate's name cannot italicise the post — breaks the thing these posts are mostly for: an
  underscore inside a URL is common, and escaping it leaves a visible backslash and a dead link.
  Markdown is not executable, so unlike HTML (#260) there is nothing to inject.
- **The one real risk is handled at the API instead.** A candidate calling themselves `@everyone`
  would ping the server, so posts go with `AllowedMentions.None` and no mention resolves whatever the
  text says. A control at the boundary beats string-mangling that has to anticipate every syntax.

## The envelope

Per rule: `ReplyToSource` (`EmailSettings` / `SessionLead` / `Custom`), `ReplyToOverride`,
`CcAddress`, `BccAddress`, `MonitoringCopyOncePerRun`.

**There is no From, and its absence is the design.** Changing the From address means SPF, DKIM and
DMARC on a domain this app does not control; get it wrong and the mail is silently classed as spam,
which is the worst outcome for a reminder nobody knows to expect. Reply-To carries none of that risk
and is what "can it come from the session lead" actually means — somebody wants the answer to reach
the right person, not the envelope to lie about the sender. A test asserts the From is always the
team's, because an absence is otherwise nobody's job to keep.

**`SessionLead` resolves through `CallSign.Normalize`, not a raw lookup.** ExamTools puts a literal
`<UNKNOWN>` in that field, which once fused two people into a single VE record. Every way of failing
to resolve — no call sign, a placeholder, no matching VE, a VE with no email — falls back to the
team's address and logs once: a reply reaching the team is worse than one reaching the lead, and a
reply reaching nobody is worse than both.

**A Cc is refused on candidate-facing rules.** The person copied cannot unsubscribe — the footer's
link belongs to the To recipient — so it is a standing visible copy nobody can stop, and it discloses
that address to every candidate. Bcc is fine, and allowed; a Cc on an internal notice to the team's
own inbox is fine too, since nobody is being disclosed to a candidate.

**`MonitoringCopyOncePerRun` defaults to true.** Forty candidates on a fan-out otherwise means forty
identical copies into one inbox, which stops being monitoring and becomes a folder somebody filters —
at which point nobody is watching at all.

**The team-wide `EmailSettings.BccAddress` is deliberately untouched** and still goes on every
candidate-facing message, as it has since #207. Making that once-per-run too is a defensible change
and a different decision — about what monitoring is for — so it is not made here in passing.

## Still to come

Nothing in #401. Worth doing separately: **`PaymentReminderService` no longer sends anything**, so its
name and its `PaymentReminder` job key both describe something it does not do. Renaming the key
orphans its `JobRunHistory` rows, which is why it is its own change rather than a tidy-up here.

Password reset, VE self-service sign-in links and VE email-change confirmations stay **outside** this
model as action-based sends. They carry access tokens, and
[#207](https://github.com/MikeWills/VeSessionManager/issues/207)'s "no monitoring Bcc" guarantee is
structural today precisely because those call sites never populate the field. Bringing any of them in
would turn that into a runtime guarantee.

## Per-session fan-out (2026-08-20)

`MessageFanOut` has a third value, `PerSession`: **one message per session**, covering that session's
subjects.

`SingleDigest` batches everything one scan returned across **all** of a team's sessions into a single
message. That is fine for "3 new registrations" and useless for anything that names a session — which
is why [#116](https://github.com/MikeWills/VeSessionManager/issues/116) could not ask for *"x
candidates registered to test at xx:xx"*: there was no single session for the sentence to be about.

Grouping brings the session's own tokens with it, available **only** on `PerSession` because a batch
spanning several sessions cannot answer them:

| Token | Renders |
|---|---|
| `{{SessionTitle}}` | The session's name |
| `{{SessionDate}}` | Start time via `SessionTimeFormatter.ForCandidate` — Eastern, like every screen |
| `{{RegisteredCount}}` | **Candidates registered on the session** |
| `{{Count}}` | How many subjects this rule is firing for — *not* the same number |
| `{{Subjects}}` | That session's people only |

⚠️ **`{{Count}}` and `{{RegisteredCount}}` differ constantly and the difference matters.** Subjects
are filtered by having an email, not being purged, and not already having a terminal run for this
rule. "x candidates registered to test" is `{{RegisteredCount}}`; "x people this rule is about right
now" is `{{Count}}`. Reaching for the wrong one produces a number that is quietly wrong rather than
obviously broken.

Two smaller decisions:

- **`SessionDate` uses `ForCandidate` even though a channel post is not a candidate.** It is the one
  formatter that renders Eastern, which is what was asked for. Never `EasternTimeFormatter` — that
  lives in the Web project and is unreachable from Core, which is how candidate email spent months
  rendering UTC (#205).
- **Subjects with no session are grouped together and rendered without the session tokens**, rather
  than dropped. A payment-subject rule set to `PerSession` should still say something.

Markers stay per subject, exactly as for a digest: one post covering twelve candidates writes twelve
rows, or the next tick would re-announce eleven of them.

## Sub-day delays: the unit moved, not the precision (2026-08-20)

A delay is now a number **and a unit** — days or hours.

The field was days with a half-day step, which put a **12-hour floor** under everything a team could
set. That was deliberate, and `MessageDelay`'s own remarks say why: an odd number of hours "cannot be
written in this unit without lying about it, and a form that silently turns 0.3 into 7 hours is worse
than one that says no". Both still hold — so **hours became sayable rather than days becoming**
**vaguer**. A fractional hour is still refused rather than rounded, for the same reason.

`MessageDelay.ForDisplay` reopens a stored value in the unit that reads naturally: whole or half days
as days, anything else as hours. ⚠️ **The edit screen must use it** — otherwise a rule saved as 1 hour
reopens as the nearest half-day and saving the form silently moves it.

### The scan had to keep up

The message-rule job ran **once a day**, so a 1-hour rule could only fire by luck. It is hourly now
(`Jobs:DayBeforeReminderIntervalHours`, default `1`).

Every time-relative trigger gets more precise with it — a fee chaser set to five days could previously
go out most of a day late. Affordable because a scan is **database-only**: it touches no external API,
and finds nothing to do on almost every tick. The trigger machinery is scan-based precisely so an
extra tick is a no-op and a missed one catches up.

⚠️ **Hourly is the floor of what a rule can mean.** A rule set to 1 hour fires at the first scan where
the session is inside the window, so somewhere between 0 and 60 minutes before. Finer would need the
job timer to move off hours, which nothing has asked for.

## Mentionable roles, per team (2026-08-20)

`Team.DiscordMentionableRoleIds` names the roles a team's channel posts may ping. Blank — the default,
and what every existing team has — means **nothing resolves**, exactly as before.

⚠️ **An allow-list, deliberately, and not a switch.** Every post has always gone out with
`AllowedMentions.None`, and that is what makes `DiscordMessageText`'s decision *not* to escape markdown
safe: a candidate whose name is `@everyone` cannot ping the server because no mention resolves at all.
A boolean "allow mentions for this team" hands that guarantee back wholesale — and **candidate names
reach a channel post through `{{Subjects}}`**, so the hostile string is the ordinary path, not a
hypothetical.

Naming the roles grants the ask while keeping the property:

- only ids the team listed resolve;
- `@everyone` and `@here` are a separate `AllowedMentionTypes` flag that is **never set**, whatever the
  message says;
- user mentions never resolve either.

`ACandidateNamedEveryone_StillCannotPingTheServer` pins it end to end: the text is not mangled, and it
resolves to nothing.

**Verified against the installed package rather than assumed** — `AllowedMentions`'s own documentation
states that when `AllowedTypes` is null, "only the ids specified in `UserIds` and `RoleIds` will be
mentioned". Leaving it null is the mechanism, not an oversight; setting any flag would widen it.

Input accepts a bare snowflake (Discord's own *Copy ID*) or the `<@&id>` mention form, comma-, space-
or newline-separated. **Anything unparseable is dropped rather than guessed at** — a malformed entry
that silently became some other id would ping the wrong room of people — and one bad entry does not
discard the good ones beside it. What is stored is normalised to the ids that parsed, so a team that
typed `@everyone` sees it disappear rather than believing it took.

## A message owns its own words (2026-08-21)

`MessageRule` carries `Subject` and `Body`. `TemplateKey` is gone, and so is the idea that a rule
points at a template authored somewhere else.

### Why

**The available tags depend on the trigger, and a template had none.** So the template editor could
not say which placeholders were available — not a missing affordance, an unanswerable question. Mike,
who built the app, could not tell from the screen which tags a template could use:

> *"How do I know what tags are available to me if I'm sending an email based on FCC side? I have
> different tags available than if I am sending it prior to the test session ... there's no way
> currently that you can link up a template to the correct rule so that a person can have the right
> tags available to them."*

The reuse the split bought — one body, several schedules — is better served by **copying a message**
and changing its timing, which `DuplicateAsync` already did. Reuse across *triggers* was the part that
could never work, because the tags differ.

### Manual sends are trigger points too

The insight that made it collapse cleanly. A hand-composed email is a message whose mechanism is
"somebody pressed a button" rather than a scan or a clock, so `MessageTriggerMechanism.Manual` joins
`State` and `TimeRelative`, and four triggers come with it:

| Trigger | Offered on |
|---|---|
| `ManualToCandidate` | Session Detail → Email candidates |
| `ManualToVe` | VE Directory → message |
| `ManualFelonyDisclosureInstructions` | the per-candidate button |
| `ManualYouthProgramInstructions` | the per-candidate button |

Once every message has a trigger, **the tag list is answerable everywhere**. Their placeholders are
taken from the same `Names` lists the send paths already supply — `CandidatePlaceholderValues` and
`VolunteerExaminerPlaceholderValues` — rather than a second list written out beside them, because two
lists of the same thing is how a tag comes to be offered that renders blank.

A manual trigger has **no delay and no recipient**: a person chose the moment and picks the people at
send time. `LegalRecipients` is empty for them, and that means "addressed at send time", not "nobody
may receive this".

### Tags are clickable

The editor lists the trigger's tags as chips that insert at the cursor. A hand-typed
`{{CandidateFirstName}}` that is a letter out renders blank and **nothing anywhere says so** — the
send succeeds with a hole in it.

⚠️ The handler lives in `app.js`, never `onclick=`. The CSP is `script-src 'self'`, so an inline
handler is silently dropped: the control renders, nothing happens, and only the console says so.

### What went away, and why nothing replaced it

Two refusals were deleted rather than reworded, because they described consequences of the split:

- **`TemplateNotFound`** — a rule pointing at a key that did not exist, recording Failed on every tick
  forever with only a log line.
- **`TemplateAudienceMismatch`** — a VE-worded template rendered through a candidate-subject scanner,
  every token blank, and the send *succeeding*.

Neither is expressible now. `MessageRequired` replaces both: a message must have words, because an
empty one sends a blank email. Five tests covering the old failures were removed with notes saying
why no replacement stands in their place.

### The migration deletes rather than converts

⚠️ Existing rules are **dropped**. Their `TemplateKey` named a template whose words live in another
table, so renaming the column would leave every rule with a key like `DayBeforeReminder` as its
subject and an empty body — nonsense that looks like data. Mike, who owns the only live deployment:
*"I have no emails that are important and I have no problems losing [them] ... delete it all and
re-create it all."*

History survives: `MessageRuleRun.MessageRuleId` is `SetNull` and the row snapshots the rule name and
trigger, so what was already sent outlives the rules that sent it.

Seeded examples arrive **switched off** — examples of what a team can set up, not mail a new team
starts sending to real people before anybody has read it. (Amended in the same change: the
*hand-sent* ones arrive **on**. See below.)

## The compose screens pick a message (2026-08-21)

Second half of the same change. The two hand-compose screens — Session Detail's *Email candidates*
and the VE Directory's *message* — used to offer a list of **template keys**. They offer this team's
messages on the matching manual trigger now, and post an `int` id rather than a string key.

`ComposableMessages` is the one place that list is built, because two screens ask the same question:
the compose screen itself, and the session menu that offers shortcuts straight into it. Two copies
would drift the moment a message was added to one.

⚠️ **The automatic messages are deliberately no longer offered as starting text.** The old list
included the registration confirmation and the day-before reminder, which reads as a convenience —
but their bodies are written around tags the manual path does not supply (`{{ZoomJoinUrl}}`,
`{{PaymentLinkUrl}}`). Starting from one produced a draft whose tags render blank and whose send
*succeeds*, which is precisely the class of failure this whole change exists to remove. A manual
message is written against a manual trigger, and the tags shown while writing it are the ones that
will resolve.

For a manual message, **off means "not offered"** rather than "not scanned" — there is no scan to
stop. That is the whole difference in what the flag means on those four triggers.

## `EmailTemplate` is gone (2026-08-21)

Third and last part. The entity, its table, its four admin pages, `EmailTemplateAdminService` and
the `EmailTemplates` DbSet are all deleted, and `Admin → Email Templates` no longer exists. What was
two screens describing two halves of one thing is one screen: **Messages**.

### The seeder seeds messages

`EmailDefaultsSeeder` used to seed seven templates and then four rules pointing at four of them by
key — the copy step existed only so both models could hold the same words while both existed. It
seeds the same seven pieces of text once now, each on the trigger that sends it, from one `Seeds`
list.

⚠️ **Automatic messages arrive off; hand-sent ones arrive on.** The risk being avoided is unread mail
going out by itself, and a message nothing sends until somebody presses a button is not that. Seeding
the hand-sent ones off would leave the felony-disclosure and youth-program buttons silently doing
nothing, which reads as broken rather than as safe.

The `Team.MessageRulesSeededUtc` tombstone is unchanged and still load-bearing: a per-message
"does this team have one for this trigger?" check re-adds a message somebody deleted on the next
Worker start, quietly resuming a send they had stopped.

### The page is called Messages

Grouped by trigger, as it was. Manual triggers sit under their own **Sent by hand** heading, because
"when" and "to" are empty for them and mixing them in among the scheduled ones invites reading a
blank delay as a bug. Row actions are unchanged: edit, copy, switch on/off, delete.

Plain words throughout, at Mike's instruction — he could not read his own form:

> *"Do not over complicate the user interface ... just call it carbon copy and blind carbon copy, or
> C.C. or BCC. You had a big long explanation about what it was and even I was confused, and I built
> it ... there's also something about a per cycle or tick or something and I found that confusing
> myself."*

So: **Cc** and **Bcc** with one short line each, and *"Only send one copy, even when the message goes
to many people"* where the fan-out control used to explain itself in a paragraph. The reasoning moved
into code comments, which is where it was useful anyway.

### ⚠️ A manual message must be invisible to the scan

Found running the Worker, not by a test (2026-08-21). Manual triggers have no scanner by design —
nothing scans a button press — but `MessageRuleService` loaded every *enabled* rule and looked one up
per trigger. The three hand-sent messages seeded switched on therefore produced
`No scanner is registered…` at **ERROR**, for every team, on every tick: nine per pass on a
three-team deployment.

The error itself is right and stays. A rule somebody created and can see enabled on screen doing
nothing at all is indistinguishable from working, and that deserves to shout. What was wrong was
asking the question of a message whose whole point is that a person sends it.

`ManualTriggers` is built from `MessageTriggerDefinitions.All` and excluded in the query.

⚠️ **Built from the list rather than by calling `For(rule.Trigger)` per rule**, which *throws* for
anything outside it — `SentByHand` is in the enum and deliberately absent from `All`. Asking would
have turned a correct, quiet error into a crashed tick.

This is the failure mode the optional-integration rule in CLAUDE.md names directly: a repeating
ERROR for an ordinary state teaches people to stop reading the log, and the next real error goes
with it.
### ⚠️ The upgrade path nearly left every existing team silent

Found while writing the local test steps, not by a test. `MessagesOwnTheirContent` deletes every
rule; `Team.MessageRulesSeededUtc` says "already set up, never seed again". Each is right on its own,
and together they leave **every team that already existed with no messages at all, permanently** —
while a brand-new team gets the seven examples.

Nothing fails and nothing logs. The Messages page just says "No messages" everywhere, the two
per-candidate buttons silently do nothing, and it reads as broken rather than as a fresh start.

`ReseedMessagesForTeamsLeftWithNone` clears the tombstone **only for teams with no messages** — a
team created in the window between the two migrations already has its seven, and clearing its
tombstone would give it fourteen.

One case is knowingly swept up: a team that deleted every message *on purpose* gets the examples
back, because nothing records the difference between "we send nothing deliberately" and "the
migration took them". The cost is one team switching seven examples off again; the alternative was
every existing team sending nothing forever. If that ever matters, the fix is a column recording why
the tombstone was cleared, not a cleverer predicate.
