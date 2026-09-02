# Discord tag sync

Reading a team's Discord server to keep VE tags in step with Discord roles ([#519]).

Most VEs carry their call sign in their Discord display name, and teams already manage "who is a full
member / auditioning / a session manager" with Discord roles. That is a second, hand-maintained copy
of something this app also stores as [`VeTag`](../src/VeSessionManager.Core/Entities/VeTag.cs)
assignments, and the two drift. This closes that gap in one direction only.

**Status: steps 1-3 of 4 are built** — the map, the check, and applying it by hand. Only the scheduled
run is left. See [Build order](#build-order).

## The rule

Discord is authoritative, one-directional, and applies only to matched VEs and mapped tags. Two
independent filters decide whether a VE is in scope at all:

- **Not in the Discord server, or not matched to a VE record on that team → do nothing.** No tags
  added, none removed. A VE who never joined Discord is untouched forever, not stripped.
- **In Discord but not on the team's VE list → do nothing.** The sync never creates a VE record or a
  team membership. ExamTools and the admin still own who is a VE.

For a VE that passes both, and only for tags carrying a `DiscordRoleId`:

| Discord role | App tag | Action |
|---|---|---|
| has | has | nothing |
| has | missing | **add the tag** |
| missing | has | **remove the tag** |

A tag with no role mapped is never read or written — hand-managed exactly as before. Mapping a tag is
therefore the whole opt-in: it hands that tag to Discord in both directions at once.

**A hand edit to a mapped tag does not stick, in either direction** (Mike, 2026-09-01: "if it's
removed from the app, re-add it if Discord has it"). Taking a mapped tag off a matched VE by hand puts
the app in the "missing tag, has role" row, so the next sync adds it straight back; adding one by hand
that Discord does not back puts it in "has tag, missing role", so the next sync removes it. That is
the rule working, not a bug to file — the place to make the change stick is Discord, which is what
"Discord is authoritative" means in practice. The only way to hand-manage a tag again is to unmap it.

There is deliberately **no in-app marker** saying which tags are Discord-managed beyond the mapping
shown on the VE Tags screen itself (asked and declined, same day). Worth revisiting only if someone is
actually surprised by a tag reappearing.

### Why removal is in scope

Adding only would have been the safer-sounding half, and it would also have been useless: the state a
team actually wants reflected is "this person is no longer a full member," which is expressed in
Discord by *taking the role away*. A sync that never removes cannot represent it, so the app's copy
would keep drifting in the one direction that matters.

The cost is that a hand-set tag on a matched VE disappears when Discord disagrees — the same shape as
the in-app VE roster editing that was removed (see CLAUDE.md's Known Constraints: an edit was reverted
by the next sync precisely when it disagreed, i.e. exactly when it mattered). That is accepted here,
because unlike the roster case it is opt-in per tag and per matched person, and because the preview
step below means the first disagreement is *shown* rather than applied.

### What this never does

- **Never writes to Discord.** No role is granted or revoked, no nickname changed, no permission
  touched. Roles are managed in Discord; this is a read. `IDiscordGuildClient` says so in its own
  remarks, and if pushing a role is ever wanted that is a new decision and a new interface.
- **Never grants access in this app.** A Discord role is not an authorization signal here, any more
  than the tag it sets is — `VeTagsGrantNoAccessTests` asserts no authorization code reads tags at
  all, and a role reaching a tag does not change that.
- **Never creates or deletes people.** Neither VE records nor team memberships.

## Exceptions, in both directions

Doing nothing is the right *action* for both no-op cases above, but it is also how a mapping mistake
hides: a VE whose display name stops carrying their call sign simply drops out of the sync, with no
error anywhere. So a run reports:

- **Discord members with no VE match** — filtered to members holding a mapped role. A team's server is
  full of candidates, club members and bots; an unfiltered list would be mostly noise, and a noisy
  list stops being read. Someone holding a mapped role is exactly the person whose tags *would* have
  synced had they matched, which makes their absence worth a line.
- **VEs with no Discord match** — unfiltered, since it is bounded by the team's own roster.

Neither list changes data. It exists to be read.

## Matching

In the order identity is trusted, first hit wins:

1. **A stored `VolunteerExaminer.DiscordUserId`.** Ends the question — including for someone who has
   since dropped their call sign from their nickname, which is exactly when guessing fails.
2. **A hand-entered `DiscordUsername`** equal to the member's account name. A person typed it
   deliberately, so it outranks anything inferred.
3. **A call sign in the display name**, current or former (`VeCallSignHistory`) — a vanity call comes
   through and a server nickname lags for months, and the person is the same person.

`DiscordCallSignParser` produces *candidates*, not matches: the shape test is loose ("Ham2" is
call-sign-shaped and is nobody's call) because the filter that actually decides is the team's own
roster. A portable suffix is split as well as kept, so "WX0MIK/M" still finds `WX0MIK`.

**Two call signs in one name resolves to nothing.** Both come back, the member is reported as
ambiguous, and no tag moves — taking the first would assign one person's tags to another by string
order.

Only **active** memberships are in scope. Someone retired from the team is neither synced nor reported
as missing from Discord: they are not expected to be there.

### Recognising someone is not a tag change

The first successful match produces an *identity link* — a `DiscordUserId` to store — which is listed
separately from tag changes. Folding it in would make "3 changes" mean something different depending
on whether anyone had been matched before, and the steady state (linked, correct) has to be able to
report nothing at all.

## A failed fetch is not "no roles"

An empty or errored member fetch looks identical to "nobody holds any role", and under the rule above
that would remove every mapped tag from every matched VE. No data means no run: nothing added, nothing
removed, nothing reported. This is the same shape as the aggregate-settled gotcha in CLAUDE.md — a
piece that is unconfigured must never *contribute* to a conclusion, only fail to.

## Identity

`VolunteerExaminer.DiscordUserId` (added in step 2) is the link: a snowflake, stable across every
rename. `DiscordUsername` stays as a hand-editable display value, refreshed when a match is confirmed,
because a VE detail page showing an 18-digit number instead of a name is worse than one showing a
slightly stale name.

The same split applies to the map itself: `VeTag.DiscordRoleId` is the link and
`VeTag.DiscordRoleName` is a display snapshot, stored rather than fetched so the tag screen can still
say which role a tag is mapped to when Discord is unreachable.

## The map

One Discord role means one tag, per team — enforced by a unique index on `(TeamId, DiscordRoleId)` and
by `VolunteerExaminerManagementService`. Two tags on one role is well defined to *run* ("both apply")
and impossible to *read* off the tag screen, which is the wrong trade for a mapping an admin has to
trust. Per team for the same reason tag names are: two teams can share one Discord server and map its
roles to their own vocabularies.

The index is deliberately unfiltered. SQLite treats NULLs in a unique index as distinct, so any number
of tags stay unmapped — the normal case. EF InMemory enforces no unique index at all, so
`VeTagDiscordRoleTests` pins both halves against real SQLite, per CLAUDE.md's rule about
provider-dependent behaviour.

**The map lives on `VeTag` rather than in a table of its own.** A tag is already a team's own
vocabulary with its own screen, and "which Discord role means this tag" is a property of that entry; a
separate entity would need its own team scoping, its own uniqueness rules and its own screen to say
the same thing.

### The role picker

`IDiscordGuildClient.ListRolesAsync` backs a `<select>` on the VE Tags screen, so an admin picks
"Team Member" rather than copying an 18-digit id out of Developer Mode. `@everyone` is excluded: every
member holds it, so a tag mapped to it could be added to the whole roster and never removed from
anyone — a mapping with no meaning is one somebody eventually picks by mistake.

An empty role list falls back to a typed id box rather than erroring the page, and every failure
collapses to that one path: no bot token, no `DiscordGuildId` on the team, the bot not in the server,
or the lookup throwing. Same pattern as the message rule channel picker ([#503]). A team that does not
use Discord must not lose the ability to edit tag names because of it.

Two details worth keeping:

- Roles are fetched in `OnGetAsync`, not in the `LoadAsync` both verbs call. A POST always redirects
  to a fresh GET, so fetching on the way in would be a wasted round trip on every save. The one POST
  that does ask Discord is a tag being mapped to a role it did not hold before — there is nothing else
  to name it with. An unchanged mapping keeps its stored name and asks nothing.
- A tag mapped to a role that is no longer in the list still renders as a selected option, marked
  *(not in this server)*. Without it, saving the row for an unrelated reason — a rename, a reorder —
  would silently unmap the tag.

## Applying

One button, everything in the plan. There is no per-row selection: the preview is where a wrong row is
caught, and the fix for one is in Discord or in the tag map — a skip that nothing remembers would
silently return on the next run, and would be indistinguishable from a row nobody looked at.

**Apply rebuilds the plan from Discord and writes that**, rather than replaying what the screen showed.
A preview is a photograph: a role revoked in the seconds between looking and clicking would otherwise
be applied as though it were still held. The previewed `Fingerprint` travels with the form and is
compared against the fresh plan purely to *report* that the picture was out of date — it never blocks
the write, since the fresh answer is the correct one either way and refusing would only mean looking at
the same screen again.

The fingerprint deliberately covers only the writes. Exception lists shift on their own (somebody
joins the server, somebody fixes their nickname) without changing what applying does, and reporting
that as "this differs from what you saw" would cry wolf.

Everything lands in one transaction. A handful of rows from one button press is easier to reason about
as all-or-nothing than half-applied — different from the scan-based jobs, which save per item precisely
because they run unattended across hundreds of rows and must never lose progress already made. Every
tag change and every account match is written to the audit log against the person who clicked.

### The username follows the id

On a confirmed match, `DiscordUsername` is overwritten with the account's real name. It is a label on a
link that is now established, and leaving a hand-typed guess beside a confirmed account would make the
screen disagree with itself.

### PII purge clears the match

`VolunteerExaminerPiiFields.Clear` nulls `DiscordUserId` alongside `DiscordUsername`. They are the same
fact, and the id is the stronger form of it — a snowflake never changes, so keeping it while clearing
the label would leave a permanent handle on someone's Discord account after their contact details were
supposed to have aged out. The cost is accepted: a purged VE still in the server is matched by call
sign again on the next check, exactly as they were the first time.

## Ops prerequisite: the privileged intent

`GET /guilds/{id}/members` is gated behind the **`GUILD_MEMBERS` privileged intent**, which has to be
enabled for the bot application in the Discord developer portal. It is a checkbox for a bot in fewer
than 100 servers, with no verification process, but the member list is empty until it is on.

Listing *roles* needs no such intent — they come off the guild object the app already fetches — so the
map can be configured before the intent is enabled. That ordering is deliberate: the configuration
screen works on day one, and the sync is what waits.

## Build order

1. **The map, and the role picker that sets it.** ← built
2. **Matching, and the preview + exceptions report.** ← built — `DiscordTagSyncService.BuildPreviewAsync`
   and the Discord Tags screen. Read-only; writes nothing, to the database or to Discord.
3. **Apply, audit-logged.** ← built — `DiscordTagSyncService.ApplyAsync`, one button on the same screen.
4. A scheduled run on `TeamPipeline` — only once (2) and (3) have been used against real data.

Steps 2 and 3 are split from 4 on purpose. Removals are in scope, so an unattended bad match strips a
real tag; the manual preview is what makes the first runs inspectable. Same report-then-act shape as
[#88]'s `--report-historical-imports`.

[#88]: https://github.com/MikeWills/VeSessionManager/issues/88
[#503]: https://github.com/MikeWills/VeSessionManager/issues/503
[#519]: https://github.com/MikeWills/VeSessionManager/issues/519
