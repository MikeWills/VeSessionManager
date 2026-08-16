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
        // Mirrors Reconciliation.cshtml.cs's [Authorize(Roles = RoleGroups.Admins)]. RoleGroups lives
        // in Web and cannot be referenced from Core, which is exactly why the mirror is guarded by a
        // test instead of by a shared constant.
        if (role is not (UserRole.SystemAdmin or UserRole.TeamAdmin))
        {
            return AlertFeed.Empty;
        }

        var openFindings = dbContext.ReconciliationFindings
            .Where(f => f.ResolvedUtc == null)
            .Where(f => teamIds == null || teamIds.Contains(f.TeamId));

        var totalCount = await openFindings.CountAsync(cancellationToken);
        if (totalCount == 0)
        {
            return AlertFeed.Empty;
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

        return new AlertFeed(items, totalCount);
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
public record AlertItem(
    string Category,
    string Title,
    string Detail,
    string TeamName,
    DateTime OccurredUtc,
    string PageName,
    int HighlightId);

/// <summary><see cref="TotalCount"/> is every open alert; <see cref="Items"/> is the first <see cref="AlertFeedService.MaxItems"/> of them.</summary>
public record AlertFeed(IReadOnlyList<AlertItem> Items, int TotalCount)
{
    public static readonly AlertFeed Empty = new([], 0);

    /// <summary>True when the menu is showing fewer than there are — what the "View all" line reports.</summary>
    public bool HasMore => TotalCount > Items.Count;
}
