# The alert bell (2026-08-16)

One place in the chassis that answers "is anything wrong right now", and takes you to the row it is
about. Issue [#339](https://github.com/MikeWills/VeSessionManager/issues/339).

## Why a bell rather than another badge

This app already counts outstanding work: `NavBadgeCountService` puts a number beside Sessions,
Applicants, Unmatched Payments and Reconciliation. That works because each of those numbers sits on
the link to the page it describes — right up until the page is *inside a closed dropdown*.

The reconciliation badge is the worked example. It lives on an item in the Settings menu, so it is
invisible until you open a menu you had no reason to open, to check a page you had no reason to
suspect. And reconciliation findings are precisely the class of problem nobody thinks to go looking
for: the sweep exists because a session went missing for months while every screen looked fine.

A badge says *how many*. An alert says *what, and where* — it carries its own destination.

## Shape

```
AlertFeedService (Core/Navigation)  → AlertFeed { Items, TotalCount }
        ↓ (cached 30s, keyed by role + team ids)
AlertFeedCache (Web)
        ↓
_AlertBell.cshtml  → one .kebab/.menu, same component as the user and help menus
        ↓ click
/Admin/Reconciliation?highlight=42  → row marked, scrolled into view
```

`AlertItem` is deliberately small: category, title, detail, team, when it was first noticed, the
Razor page it lives on, and the id of the row to highlight there. Nothing in it is re-worded — the
detail string is whatever the source already prints on its own page, because two phrasings of one
fact drift apart.

## Decisions worth keeping

**The role gate lives in the feed, not only in the partial.** Every alert renders as a link to an
authorized page, so a feed that hands a SessionManager a reconciliation alert has built a 403 —
exactly the bug the nav's own role gates were added to fix. `AlertFeedService` therefore answers
"which roles may see this source" itself, and `AlertPageRoleGateTests` checks that answer against
each target page's real `[Authorize]` metadata. That test earns its place because the two copies of
the rule cannot be merged: `RoleGroups` is a Web type and the service is in Core.

**The bell renders for every signed-in role, even the ones with no possible alerts today.** A
control that appears and disappears by role is one nobody learns to look at. The empty state says
"Nothing needs your attention" rather than the bell vanishing.

**The highlight marks, it does not filter.** The reader arrived to look at one row, but hiding the
others would answer a narrower question than the one asked — and "is this the only one?" is usually
the next question. A stale or foreign `highlight` id simply matches no row; nothing is ever looked
up by it, which is why it needs no authorization check of its own.

**Scrolled with `scrollIntoView`, not an `#id` fragment.** A fragment jump lands the row at the top
of the viewport under the chassis header, and it fires before the sortable-table script has finished
reordering the rows it is scrolling to. The marker itself is server-rendered, so the row is still
picked out with JavaScript unavailable.

**Cached like its neighbours.** Third cache of this shape on this layout, after `IngestionHealthCache`
and `NavBadgeCountCache` — the partial renders on every authenticated page request. The key includes
the role, unlike the badge cache: the feed is role-gated at source, so serving one role's entry to
another would be a permissions bug rather than a stale number. The 30-second window means a resolved
finding can linger in the bell briefly; these alerts come from a nightly sweep, so that is well below
the resolution of the data itself.

## An alert has to end when its cause does

**Found live 2026-08-17.** Reconciliation flagged a candidate-count mismatch on an HRCC session,
Mike pressed **Refresh candidates**, the roster came back with all three — and the alert stayed lit.

`ReconciliationFinding.ResolvedUtc` was stamped in exactly one place, `ReconciliationService.RunAsync`,
which is on a 24-hour cadence. `TeamPipeline` — what the refresh button runs — has no reconciliation
step, by design: the job describes itself as *"Read-only — it reports, it never repairs."* That is the
right split. The gap was its unwritten converse: **the repair never closed the report.**

This is worse here than on most screens. Reconciliation is the one job whose entire purpose is to be
believed when it disagrees with the database, and an alert that survives the fix teaches the reader
that the bell is not worth opening — which is the exact failure the bell replaced.

Ingestion now closes what it fixes (`SessionIngestionService.ResolveCountMismatchAsync`). Three things
about the shape:

- **It tests the negation of the condition that raises the finding**, not "did I sync this session".
  Reconciliation flags only when remote has *more* than local, so this closes only when remote no
  longer exceeds local. Closing on "I synced it" would silence the finding on every poll and it would
  never be seen again — there is a test pinning that.
- **It only runs when the sync actually added somebody.** A count that did not move cannot have closed
  a gap, so a steady-state tick asks nothing of the database.
- **Being wrong is cheap, in the safe direction.** `RecordAsync` sets `ResolvedUtc` back to null when a
  finding is seen again, so a premature close reappears within the day rather than hiding something.

The general rule for any future source: whatever repairs the condition should close the alert, and the
close should re-test the condition rather than assume the repair worked.

## Adding a second source

1. Query it in `AlertFeedService.GetAsync`, returning `AlertItem`s with the target page and row id.
2. Gate it by role there, next to the existing gate, and add the target page to the role-gate test's
   reach (it walks whatever the feed returns, so this is automatic once the alert exists).
3. Give the target page a `Highlight` bind property and `id="…-@id"` + `row-highlight` on its rows.
4. Replace the menu's "View all N alerts" link. It points at `/Admin/Reconciliation` because that is
   currently the only source; a second source is what earns a real `/Alerts` page.

The cap (`AlertFeedService.MaxItems`, 8) bounds the menu, never the count — a bell reading "5" over a
page listing forty is worse than no bell.

## Third source: sessions skipped for missing configuration (#440, 2026-08-20)

Split out of [#402](https://github.com/MikeWills/VeSessionManager/issues/402), where it was diagnosed
and then buried in a comment for three days.

`SessionIngestionService` refuses to create a session it cannot configure — no `Vec` matches the
ExamTools code, or the VEC matched but has no `FeeConfiguration` in effect. Both sites logged a
`[WRN]` and bumped a counter that lands inside a run summary whose status is **`Success`**. On beta
that ran for **five days**, and surfaced only because a Session Manager noticed a colleague's session
had never appeared.

**It is hard to notice by construction.** The config check runs only on create, so every session
already in the table keeps updating normally. The app looks healthy; only *new* sessions vanish.

Four things worth carrying forward:

- **A counter cannot become an alert.** It has nowhere to point and nothing to name. `SkippedSession`
  is the durable row that lets the bell say *"W9NB Tacos and Testing Tuesday (8/19/2026) was skipped:
  no VEC is configured with the ExamTools code 'arrl'"* — which states the fix, not the symptom. The
  quoted code is the exact string somebody types into the fix page.
- **Nobody dismisses these.** The row clears when the session ingests and is swept when the feed stops
  reporting it. There is deliberately no dismiss, because it describes the *current* configuration —
  a dismiss button would let somebody silence a live misconfiguration.
- **Oldest first, unlike the other two sources.** A skip refused for five days is more urgent than one
  first seen an hour ago, not less: it is a standing fault, and every poll since has dropped another
  session. `OccurredUtc` is first-seen for the same reason — last-seen resets every poll and would
  make a week-old fault look like it started this morning.
- ⚠️ **SystemAdmin only, and that includes not TeamAdmin.** Both destinations
  (`/Admin/Vecs`, `/Admin/FeeConfigurations`) carry `RoleGroups.SystemAdminOnly`.
  `AlertPageRoleGateTests` caught this when the source first shipped gated admin-wide — the guard
  working exactly as designed. **The cost is real:** a TeamAdmin whose team's sessions are silently
  vanishing does not see this alert. Both fixes genuinely belong to a SystemAdmin, and an alert
  linking somewhere the reader cannot open is worse than none — but if TeamAdmins should be told, the
  answer is a page they can open, not a wider gate.

The `HighlightId` is `0`: nothing on either page corresponds to the row, because the *missing*
configuration is the problem. Harmless by design — the highlight marks, it never filters.

