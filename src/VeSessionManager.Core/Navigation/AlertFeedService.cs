using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Navigation;

/// <summary>
/// What is currently wrong and wants a person, gathered into one list for the nav's alert bell
/// (#339). See docs/alerts.md.
///
/// <para><b>Why this is not just another badge.</b> <see cref="NavBadgeCountService"/> answers "how
/// many are outstanding" beside the page they live on, which works right up until the page is inside
/// a closed dropdown — the reconciliation badge is three clicks from being seen, and the findings it
/// counts are precisely the ones nobody thinks to go looking for. An alert carries its own
/// destination instead: the row it is about, not the list it is in.</para>
///
/// <para><b>The role gate is here, not only in the partial.</b> Every alert renders as a link
/// straight to an authorized page, so a feed that returns an item the reader cannot open has built a
/// 403. Deciding that at the source means a second alert source cannot be added to the bell without
/// answering the question — and <c>AlertPageRoleGateTests</c> (Web) checks the answer against each
/// target page's real <c>[Authorize]</c> metadata rather than trusting the comment.</para>
///
/// <para><b>teamIds semantics are <see cref="NavBadgeCountService"/>'s</b>: null means "every team"
/// (SystemAdmin), an empty list means no teams at all.</para>
/// </summary>
public class AlertFeedService(AppDbContext dbContext)
{
    /// <summary>
    /// How many alerts the menu itself lists. The badge still counts every one — see
    /// <see cref="AlertFeed.TotalCount"/> — because a bell reading "5" over a page listing forty is
    /// worse than no bell at all.
    /// </summary>
    public const int MaxItems = 8;

    public async Task<AlertFeed> GetAsync(UserRole role, IReadOnlyList<int>? teamIds, CancellationToken cancellationToken)
    {
        // The gate is per source now that there are two, and they differ. Reconciliation is
        // admin-only; an unconfirmed ARRL submission points at session detail, which every role can
        // open. A single gate at the top would have silently hidden the second source from the
        // Session Managers who actually press the button.
        var isAdmin = role is UserRole.SystemAdmin or UserRole.TeamAdmin;

        var (reconciliationItems, reconciliationTotal) = isAdmin
            ? await GetReconciliationAlertsAsync(teamIds, cancellationToken)
            : ([], 0);

        // Both admin roles, per Mike's ruling (2026-08-20): if it is team-related, a TeamAdmin sees
        // it — their team's sessions are the ones going missing.
        //
        // That does not weaken the no-403 rule, it routes around it. Neither fix page admits a
        // TeamAdmin, so the DESTINATION varies by role rather than the gate excluding them: the first
        // cut protected the rule by withholding the information, which was the wrong half to give up.
        var (skippedItems, skippedTotal) = isAdmin
            ? await GetSkippedSessionAlertsAsync(role, teamIds, cancellationToken)
            : ([], 0);

        var (submissionItems, submissionTotal) = await GetUnconfirmedSubmissionAlertsAsync(teamIds, cancellationToken);

        var items = submissionItems
            .Concat(reconciliationItems)
            .Concat(skippedItems)
            .OrderByDescending(i => i.OccurredUtc)
            .Take(MaxItems)
            .ToList();

        return items.Count == 0 ? AlertFeed.Empty : new AlertFeed(items, reconciliationTotal + submissionTotal + skippedTotal);
    }

    /// <summary>
    /// Submissions ARRL never confirmed (#197).
    ///
    /// <para><b>Exactly the class of problem the bell exists for.</b> An unconfirmed submission leaves
    /// the session looking unsubmitted, which is correct — it may or may not have been filed — but
    /// there is nothing anywhere else that would make anyone go and look. It cannot be retried and it
    /// cannot be resent, so the only resolution is a person telephoning ARRL, and the only thing that
    /// prompts that is this.</para>
    ///
    /// <para>No "resolved" flag to filter on, unlike a reconciliation finding: it clears when the
    /// session is marked submitted by hand, which is what a human does once ARRL confirms.</para>
    /// </summary>
    private async Task<(List<AlertItem> Items, int Total)> GetUnconfirmedSubmissionAlertsAsync(
        IReadOnlyList<int>? teamIds, CancellationToken cancellationToken)
    {
        var unconfirmed = dbContext.ArrlVecSubmissions
            .Where(s => s.Outcome == VecSubmissions.ArrlReceiptOutcome.Unknown)
            .Where(s => s.Session != null && s.Session.VecSubmissionStatus != VecSubmissionStatus.Submitted)
            .Where(s => teamIds == null || (s.TeamId != null && teamIds.Contains(s.TeamId.Value)));

        var total = await unconfirmed.CountAsync(cancellationToken);
        if (total == 0)
        {
            return ([], 0);
        }

        var rows = await unconfirmed
            .Include(s => s.Team)
            .OrderByDescending(s => s.SubmittedUtc)
            .ThenByDescending(s => s.Id)
            .Take(MaxItems)
            .ToListAsync(cancellationToken);

        var items = rows
            .Select(s => new AlertItem(
                Category: "ARRL submission",
                Title: "ARRL never confirmed this filing",
                Detail: s.TransportError is not null
                    ? $"The upload did not complete ({s.TransportError}). It may still have been filed — check with ARRL before sending it again."
                    : $"ARRL's reply did not confirm {s.UnconfirmedFileNames ?? s.ArchiveFileName}. It may still have been filed — check with ARRL before sending it again.",
                TeamName: s.Team?.Name ?? "—",
                OccurredUtc: s.SubmittedUtc,
                PageName: "/SessionManager/Detail",
                HighlightId: s.Id,
                RouteId: s.SessionId))
            .ToList();

        return (items, total);
    }

    /// <summary>
    /// Sessions ExamTools is reporting that this app refuses to create for want of configuration
    /// (#440, split out of #402).
    ///
    /// <para><b>The silent one.</b> Both skip sites already logged a warning and bumped a counter that
    /// lands inside a run summary marked <c>Success</c>. On beta that ran for five days and was found
    /// only because a Session Manager noticed a colleague's session had never appeared — and it is
    /// hard to notice by design, since the config check runs only on create, so every session already
    /// in the table keeps updating normally while new ones vanish.</para>
    ///
    /// <para>No resolved flag and no dismiss: the row clears when the session ingests, and is swept
    /// when the feed stops reporting it. A dismiss button here would let somebody silence a live
    /// misconfiguration.</para>
    /// </summary>
    private async Task<(List<AlertItem> Items, int Total)> GetSkippedSessionAlertsAsync(
        UserRole role, IReadOnlyList<int>? teamIds, CancellationToken cancellationToken)
    {
        var skipped = dbContext.SkippedSessions
            .Where(s => teamIds == null || teamIds.Contains(s.TeamId));

        var total = await skipped.CountAsync(cancellationToken);
        if (total == 0)
        {
            return ([], 0);
        }

        // Oldest first, unlike the other two sources. A skip that has been refused for five days is
        // more urgent than one first seen an hour ago, not less — it is a standing misconfiguration
        // rather than an event, and every poll since has dropped another session on the floor.
        var rows = await skipped
            .Include(s => s.Team)
            .OrderBy(s => s.FirstSeenUtc)
            .ThenBy(s => s.Id)
            .Take(MaxItems)
            .ToListAsync(cancellationToken);

        // Neither fix page admits a TeamAdmin, so they are sent to Job Run History — RoleGroups.Admins,
        // and where the ingestion runs that skipped their sessions are listed. The alert still reaches
        // the person whose sessions are missing, and still links somewhere they can open.
        var canFix = role is UserRole.SystemAdmin;

        var items = rows
            .Select(s => new AlertItem(
                Category: "Ingestion",
                Title: "Session not ingested — nothing to configure it with",
                // The VEC code is quoted for both readers: it is the actionable fact either way, the
                // string a SystemAdmin types in and the one a TeamAdmin passes on. What differs is the
                // instruction, because telling somebody to perform a fix they cannot perform is how an
                // alert becomes noise.
                Detail: s.Reason == SkippedSessionReason.NoMatchingVec
                    ? $"{Describe(s)} was skipped: no VEC is configured with the ExamTools code '{s.VecCode}'. " +
                      (canFix
                          ? "Add it and the session ingests on the next poll."
                          : "Ask a system administrator to add it — the session ingests on the next poll once they have.")
                    : $"{Describe(s)} was skipped: the VEC matched but has no fee configuration in effect. " +
                      (canFix
                          ? "Add one and the session ingests on the next poll."
                          : "Ask a system administrator to add one — the session ingests on the next poll once they have."),
                TeamName: s.Team.Name,
                // First-seen, not last-seen: "how long has this been broken" is the question, and
                // last-seen would reset every poll and make a five-day-old fault look brand new.
                OccurredUtc: s.FirstSeenUtc,
                PageName: canFix
                    ? s.Reason == SkippedSessionReason.NoMatchingVec
                        ? "/Admin/Vecs"
                        : "/Admin/FeeConfigurations"
                    : "/Admin/JobRunHistory",
                // Nothing on any of those pages corresponds to this row — the missing configuration is
                // the problem. Harmless by design: the highlight marks, it never filters, so an id that
                // matches nothing simply highlights nothing (see docs/alerts.md).
                HighlightId: 0))
            .ToList();

        return (items, total);
    }

    /// <summary>Names the session the way a human would recognize it, falling back through what the feed actually gave us.</summary>
    private static string Describe(SkippedSession skip)
    {
        var title = string.IsNullOrWhiteSpace(skip.Title) ? $"Session {skip.ExamToolsSessionId}" : skip.Title.Trim();
        return skip.ScheduledStartUtc is { } start
            ? $"{title} ({start:M/d/yyyy})"
            : title;
    }

    private async Task<(List<AlertItem> Items, int Total)> GetReconciliationAlertsAsync(
        IReadOnlyList<int>? teamIds, CancellationToken cancellationToken)
    {
        var openFindings = dbContext.ReconciliationFindings
            .Where(f => f.ResolvedUtc == null)
            .Where(f => teamIds == null || teamIds.Contains(f.TeamId));

        var totalCount = await openFindings.CountAsync(cancellationToken);
        if (totalCount == 0)
        {
            return ([], 0);
        }

        // Newest first: a finding that appeared last night is the one still worth acting on, while
        // one that has been open for a fortnight has already been seen and left. Id breaks the tie
        // so paging past the cap is stable — every finding from one sweep shares a FirstSeenUtc.
        var findings = await openFindings
            .Include(f => f.Team)
            .OrderByDescending(f => f.FirstSeenUtc)
            .ThenByDescending(f => f.Id)
            .Take(MaxItems)
            .ToListAsync(cancellationToken);

        var items = findings
            .Select(f => new AlertItem(
                Category: "Reconciliation",
                Title: f.Kind == ReconciliationFindingKind.MissingSession
                    ? "Session missing from this app"
                    : "Candidate count doesn't match",
                Detail: f.Detail,
                TeamName: f.Team.Name,
                OccurredUtc: f.FirstSeenUtc,
                PageName: "/Admin/Reconciliation",
                HighlightId: f.Id))
            .ToList();

        return (items, totalCount);
    }
}

/// <summary>
/// One thing that is wrong, and where to go and look at it.
/// </summary>
/// <param name="Category">Which source raised it — the menu groups by this, so a second source reads as a second kind rather than more of the same.</param>
/// <param name="Title">The short "what kind of wrong" line.</param>
/// <param name="Detail">The specifics, as the source already words them on its own page. Deliberately not re-worded here: two phrasings of one fact drift.</param>
/// <param name="OccurredUtc">When the problem was first noticed, not when it happened.</param>
/// <param name="PageName">The Razor page the alert navigates to — a real page path, since the link is built with <c>asp-page</c>.</param>
/// <param name="HighlightId">The id of the row to highlight once there. Passed as <c>?highlight=</c>; the page scrolls to it and marks it.</param>
/// <param name="RouteId">
/// The target page's own <c>{id:int}</c> route value, for a page that is about one record rather than
/// a list — session detail, say. Null for a list page, which is what the first alert source needed and
/// why this did not exist until the second one arrived.
/// </param>
public record AlertItem(
    string Category,
    string Title,
    string Detail,
    string TeamName,
    DateTime OccurredUtc,
    string PageName,
    int HighlightId,
    int? RouteId = null);

/// <summary><see cref="TotalCount"/> is every open alert; <see cref="Items"/> is the first <see cref="AlertFeedService.MaxItems"/> of them.</summary>
public record AlertFeed(IReadOnlyList<AlertItem> Items, int TotalCount)
{
    public static readonly AlertFeed Empty = new([], 0);

    /// <summary>True when the menu is showing fewer than there are — what the "View all" line reports.</summary>
    public bool HasMore => TotalCount > Items.Count;
}
