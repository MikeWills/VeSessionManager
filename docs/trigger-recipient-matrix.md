# Trigger × recipient matrix

**Status: decided 2026-08-20 by Mike. Not yet built.** This table is the spec for generalizing the
message engine's recipients; #116 is its first concrete case and cannot ship without it.

Every trigger point should be able to send to a candidate, a VE, or the session manager — *"somebody
might be crazy and want an email at every single point along the way to go to themselves."* Today a
trigger owns its recipients (`MessageTriggerDefinitions.LegalRecipients`), which conflates two
independent things:

- **Subject** — what happened and whose data the message is about.
- **Recipient** — who hears about it.

**Ruling on unmarked cells:** *"If I didn't say Y or N, then it's a N."* Everything left blank in the
worksheet is therefore a deliberate no, not an open question.

**Amended 2026-08-25.** `PaymentUnpaid` — the row below — is gone: Mike, "PaymentUnpaid is literally
worthless. If they didn't pay the test session fee, they couldn't test and/or the VEC would not
process it." Its condition (an FCC application entered for a candidate who never paid) cannot
legitimately arise. `PaymentUnpaidBeforeSession` replaced the real need it was reaching for — fires
before the session, while the fee is still unpaid — and was carried forward on this same matrix's
shape: candidate-facing, staff-addressable, no Discord channel, all three fan-outs. The table below is
left as the 2026-08-20 worksheet it records; it is not a live spec.

## The matrix

| Trigger | Subject | Candidate | VEs (Discord) | Session lead / SM | Admin roles | Own address |
|---|---|---|---|---|---|---|
| `CandidateRegistered` | Candidate | ✅ | N | **Y** | **Y** | **Y** |
| `BeforeSessionStart` | Candidate | ✅ | **Y** | **Y** | **Y** | **Y** |
| `FccFeeOutstanding` | Candidate | ✅ | N | **Y** | **Y** | **Y** |
| `PaymentUnpaid` | Payment | ✅ | N | **Y** | ✅ | **Y** |
| `CandidateTested` | Candidate | ✅ | N | **Y** | ✅ | **Y** |
| `LicenseGranted` | Candidate | ✅ | N | **Y** | ✅ | **Y** |
| `FelonyDisclosureDeclared` | Candidate | ✅ | ⛔ never | **Y** | **Y** | **Y** |

✅ = already dispatchable today. ⛔ = ruled out on privacy grounds, see below.

Note `CandidateRegistered`, `FccFeeOutstanding`, `PaymentUnpaid`, `CandidateTested` and
`LicenseGranted` are **N** for the Discord/VE column: those were marked Y for a *channel* in the
worksheet, but see the collapse below — the only VE-facing trigger anybody actually asked for is the
session reminder.

## Fan-out: a per-rule choice, not a per-trigger fact

The worksheet marked several options `Y` on the same trigger. That is **the set of options a rule may
choose from**, not three messages — confirmed by `BeforeSessionStart` excluding one while keeping the
others, which would be meaningless if all three fired.

| Trigger | one per candidate | one per session | one digest per scan |
|---|---|---|---|
| `CandidateRegistered` | Y | Y | Y |
| `BeforeSessionStart` | **N** | Y | Y |
| `FccFeeOutstanding` | Y | Y | Y |
| `PaymentUnpaid` | Y | Y | Y |
| `CandidateTested` | Y | Y | Y |
| `LicenseGranted` | Y | Y | Y |
| `FelonyDisclosureDeclared` | Y | Y | Y |

⚠️ **"One per session" does not exist yet.** `MessageFanOut.SingleDigest` batches everything one scan
returned across *all* the team's sessions into a single message — there is no `GroupBy(SessionId)`.
That gap is exactly why #116 cannot say *"x candidates registered to test at xx:xx"*, and building it
is the largest single item here.

`BeforeSessionStart` being N for per-candidate is consistent with the ask: a VE reminder is about the
session, not about each person on it.

## Decisions

### VEs are reached over Discord, not email

> *"Primarily, an automatic message to Discord would be wanted here. I don't think any automatic
> email to any VE other than the SM as listed."*

This is the biggest simplification in the whole design. It means:

- The engine sends **no automatic email to VEs at all**, so the #191 VE unsubscribe never comes into
  play here. That removes an entire class of risk — a new mail path quietly undoing a
  deliberately-broad opt-out.
- **"Rostered VEs" and "all team VEs" collapse into one thing.** A Discord channel post does not
  address individuals; a channel has whoever it has. The real choice is *which channel*, and that is
  already per-rule (`MessageRule.DiscordChannelId`). Both worksheet columns therefore fold into the
  single Discord column above.

### Session lead = session manager, from ExamTools

> *"Team lead as listed on ET. Team Lead = SM"*

**Cheaper than first assessed.** `Session.TeamLeadCallSign` is already stored from ExamTools' Team
Lead field, and `MessageDispatchService` already resolves it call sign → VE → email address for the
Reply-To feature (`MessageReplyToSource.SessionLead`). The lookup exists and is proven; it simply is
not wired into `ResolveAddress` as a recipient. `CallSign.Normalize` already handles ExamTools'
`<UNKNOWN>` so a session with no named lead is skipped rather than looked up.

### "Own address" means admin roles, not a typed string

> *"I meant team admins or system admins. Could be all SMs as well."*

So this is a **role-based recipient resolved over app users**, not a literal address on the rule.
Better in every way: emails already live in Identity, team scoping already exists, and they are staff
so there is no unsubscribe surface and no PII leaving the team.

**Three role options, decided 2026-08-20** — *"All SMs is a third role option."*

| Option | Resolves to |
|---|---|
| Team admins | app users with `TeamAdmin` on the team |
| System admins | app users with `SystemAdmin` |
| **All session managers** | app users with `SessionManager` on the team |

⚠️ **"All SMs" and "session lead" are different populations, from different systems — do not merge
them.** The session lead comes from **ExamTools** (`Session.TeamLeadCallSign` → VE record → that VE's
email) and **may not have an app account at all**; a VE leading a session is not required to be a
user here. "All SMs" comes from **Identity**, and those users may not be VEs. So *"Team Lead = SM"* is
true of the people and false of the plumbing: the two columns reach different sets by different
lookups, and a rule wanting "whoever is running this session" wants the session-lead column, not this
one.

## Constraints that are not up for a vote

- ⛔ **A digest must never go to a candidate.** One message listing other candidates is
  cross-candidate PII sent outside the team. A *recipient × fan-out* rule, unrelated to which trigger
  fired.
- **Purged candidates must not resurface.** `PiiPurgedUtc` is set and the fields nulled; a message
  about them to anyone regresses the purge.
- **`FelonyDisclosureDeclared` stays off Discord.** A felony disclosure reaching a channel is not the
  same class of mistake as an over-chatty reminder — it is a disclosure about a person to an audience
  with no need for it. Session lead and admin roles are marked Y, which is defensible: they are staff
  who may have to act on it. ⚠️ Worth being explicit that this now reaches **system admins**, which is
  wider than "the SM who has to act."
- **The VE unsubscribe (#191) is untouched** — because the engine sends VEs no email. If that ever
  changes, this constraint comes straight back.

## Why the current `LegalRecipients` is the wrong shape

It restricts by **trigger**, when every real constraint above is about **recipient**, **fan-out**, or
**subject sensitivity**. That is why `BeforeSessionStart` is candidate-only today for no reason
anybody wrote down — and why #116, a VE-facing reminder about an upcoming session, cannot be
expressed at all.

## What building this takes

Roughly in dependency order:

1. **Per-session fan-out** (`GroupBy(SessionId)`) — the largest item, and what #116 is blocked on.
2. **`SessionLead` address resolution** — reuse the Reply-To lookup at `ResolveAddress`. Cheapest.
3. **Admin-role recipients** — resolve over app users by role, team-scoped.
4. **Session-level placeholders for a grouped message** — registered count, session time, and ideally
   the Discord event link (`Session.DiscordEventId` is stored; the URL is built only in
   `Detail.cshtml.cs` and is not a placeholder).
5. **`LegalRecipients` rewritten to say what this table says**, with the digest-to-candidate rule
   enforced independently of it.
6. **Sub-12-hour scheduling** for #116 specifically: the admin form binds days with a half-day step
   (12h minimum) and `MessageRuleJob` runs daily, so a 1-hour reminder needs both to change.

## Related

- **#116** — Discord broadcast reminders to VEs. First concrete case.
- `docs/trigger-points.md` — how the engine works today.
- `MessageTriggerDefinitions.cs` — where `LegalRecipients` lives.
- `MessageDispatchService.ResolveAddress` — returns null for `SessionLead` and `DiscordChannel` today.
