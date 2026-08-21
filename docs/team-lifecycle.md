# Ending a team: deactivate, or delete

Two answers to "we are done with this team", and they are not the same question. Both live on the
row menu at **Admin → Teams**.

| | Deactivate | Delete |
|---|---|---|
| What happens | the app stops polling and sending for the team | the team and everything it owns is removed |
| Reversible | yes, one click | **no** |
| History | kept and readable | gone |
| Guard | none — it is reversible | type the team's name |
| Who | SystemAdmin | SystemAdmin |

**Deactivate is the usual answer.** Most "we're finished with this team" cases are a club that has
stopped running sessions, and their history is the thing somebody will want in two years when a
candidate asks what happened at their exam. Delete is for a team that should never have existed, or
whose data is genuinely unwanted.

## Deactivate (#448)

`Team.DeactivatedUtc`, null when active. The two jobs that enumerate teams —
`SessionIngestionJob` and `PerTeamDailyJob` — share one `Team.IsActiveExpression` so there is a
single definition of "the app works on this team", rather than two predicates that drift.

A deactivated team **stays on the Teams list**, deliberately. It is not a soft delete and hiding it
would make it undiscoverable, which is the one thing worse than showing it: somebody would create a
second team with the same name.

## Delete (2026-08-21)

Mike: *"delete which would delete everything related to that team, sessions, VEs, history,
everything. A warning should be included and confirm deletion before it deletes."*

`TeamDeletionService`. Everything below is either a decision that was made deliberately or a trap
that is easy to fall into a second time.

### Order is the whole difficulty

Thirteen of a team's child tables are `Restrict`, so nothing is removed for us — each has to go
explicitly, leaves first, or `SaveChangesAsync` throws partway through the one action nobody can
retry. The order is in the service, derived from the model.

⚠️ **`TeamDeletionCoverageTests` is what keeps it correct.** It reads the EF model for every table
with a foreign key into `Team` and fails if the service never mentions it. That is the realistic way
this breaks: a delete is written once, and the tables it has to walk keep arriving. A hand-written
list of assertions cannot notice the seventeenth table.

The first version of that guard pluralised CLR type names to guess DbSet names, decided
`JobRunHistories` was unhandled, and reported a table that was handled all along. It reads the
context's own `DbSet` properties now — **a guard that cries wolf gets muted, which is worse than not
having one.**

### Tracked removals, not `ExecuteDelete`

`ExecuteDeleteAsync` issues its own statement immediately, so a failure partway through would leave
the team half-deleted with no way back. Tracked `RemoveRange` all lands in one `SaveChangesAsync`,
which SQLite wraps in a transaction. It also keeps the `Cascade` relationships working, which
`ExecuteDelete` bypasses.

The volumes make it affordable: a team's whole history is thousands of rows, and this runs once in a
team's lifetime.

### What survives

- **`Vec` and `FeeConfiguration`** — parents of a team, not children. The hierarchy is
  VEC ⇒ Team ⇒ VE (`docs/multi-team.md`), and `Session.VecId` points *up*.
- **User accounts** — people, not team property. They lose their `UserTeam` row.
- **Square's and ARRL's own records** — untouchable, and the confirmation says so rather than
  implying this erases the transaction.

### VEs: a person, not team property

A VE whose only membership is the deleted team is deleted. One who examines for another club keeps
existing and loses only the membership.

Three exclusions, each of which would otherwise throw on save or strand something:

- a VE linked to a **surviving user account** (`User.VolunteerExaminerId`, Restrict with a unique
  index);
- a VE that another VE record was **merged into** (a Restrict self-reference, cross-team by nature);
- ⚠️ a VE still on **another team's session roster**. Memberships and roster rows are established
  independently, so "only member of this team" does not imply "worked only this team's sessions".
  This is the one that would be easy to miss, and it fails as a foreign key violation rather than as
  anything legible.

### Files on disk go before rows

ARRL archives are files, and nothing rolls a file back. They are deleted **first**, on purpose:

- a file left behind after the row naming it is gone is unreachable forever — nothing knows its path;
- a file deleted before a save that then fails is the same team, still deletable, missing an archive
  it was about to lose anyway.

The two failure modes are not symmetric, so the order is not arbitrary.

⚠️ Deleted **file by file from the stored paths, never by removing the team's directory**. The
archive tree is keyed on `ExamToolsTeamCode`, a free-text field nothing stops two teams from sharing,
and a recursive delete on a shared code would take another team's evidence with it.

**The `ArrlVecSubmission` rows go too**, which is a deliberate reversal of that entity's original
design — it was built with a nullable `TeamId` specifically so a filing record could outlive its
team. Mike's call, asked explicitly: a receipt pointing at a team that no longer exists, for files
that no longer exist, is not a record of anything.

### The audit entry that must not delete itself

Mike: *"Log the team delete, but delete the audit logs."*

The deletion's own entry is written with **`teamId: null`**. Attributed to the team it describes, it
would be caught by the same sweep that clears the team's audit rows — the one record that must
survive would delete itself. `TheDeletionIsAudited_AndThatEntryOutlivesTheTeam` pins exactly that.

⚠️ **Only rows the team is *attributed* to are identifiable.** `AuditLog.TeamId` is populated on
background-job writes and left null on user-attributed ones, which scope through the acting user's
memberships instead. Those rows stay, describing entities that no longer exist. Said plainly rather
than papered over: a fuller sweep would have to guess from `EntityType`/`EntityId` and would
eventually delete another team's history by collision.

`TeamDeletionService.cs` is on `AuditLogAppendOnlyTests`' `SanctionedDeleteFiles` list — the audit
log is append-only apart from named exceptions, and this is now the third.

### The typed name, and why it is not a second "are you sure"

A confirm dialog is answered reflexively. The mistake this action actually invites is pressing delete
on the right-looking row of the **wrong team**, and a dialog does not catch that. Typing the name
does.

Checked on the server with an `Ordinal` comparison, not only in the browser — **a modal is not a
permission**, and a hand-built POST never opens the dialog at all. `TeamDeletePageTests` posts
directly to prove it.

The confirmation shows the session and candidate counts, because a number somebody can check against
what they expect is the last chance to notice the wrong team.

## Testing

- `TeamDeletionSqliteTests` — real SQLite, and it has to be. EF's in-memory provider does not enforce
  foreign keys at all, so a test on it would pass with the deletion order wrong and prove nothing.
- `TeamDeletionCoverageTests` — the model-driven guard described above.
- `TeamDeletePageTests` — authorization and the typed-name guard, over HTTP.

⚠️ One trap in the Web tests: `CreateClientAs(UserRole.TeamAdmin)` sets the **claims** role, but
`CanCreateTeam` reads `User.Role` off the database row, which the harness seeds as SystemAdmin. The
header alone leaves the acting user a SystemAdmin and quietly proves nothing — the row has to be
demoted too.
