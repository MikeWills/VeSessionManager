using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Web.Tests;

/// <summary>
/// Filtering and paging on the audit log (#86).
///
/// <para>The page was <c>OrderByDescending(TimestampUtc).Take(200)</c> with no filter and no paging.
/// The motivating incident is the one these tests reproduce: a bulk operation wrote 176 rows in
/// about a second, so ~88% of the visible log became one operation and everything older was
/// unreachable — not deleted, just impossible to get to.</para>
///
/// <para>Each test asserts on the rendered row count, because that is what the reader actually gets.
/// A filter that narrowed the count in the heading while leaving the table alone would be a
/// plausible-looking lie, and the same class of bug ApplicantStatusPageTests guards against.</para>
/// </summary>
public class AuditLogPageTests : IClassFixture<WebAppFactory>
{
    private const string Url = "/Admin/AuditLog";

    private readonly WebAppFactory _factory;

    public AuditLogPageTests(WebAppFactory factory) => _factory = factory;

    /// <summary>
    /// A bulk write of <paramref name="bulkCount"/> identical rows on one timestamp, plus a handful
    /// of older, differently-shaped entries that the bulk write would otherwise bury.
    /// </summary>
    private async Task SeedAsync(int bulkCount)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        db.AuditLogs.RemoveRange(await db.AuditLogs.ToListAsync());
        await db.SaveChangesAsync();

        var bulkMoment = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < bulkCount; i++)
        {
            // Same timestamp for every row, exactly like the real backfill. This is also what makes
            // the ThenByDescending(Id) tiebreak load-bearing: without it, paging over rows that all
            // compare equal drops and repeats them across page boundaries.
            db.AuditLogs.Add(new AuditLog
            {
                Action = "VecSubmissionMarked",
                EntityType = "Session",
                EntityId = i + 1,
                Details = $"Bulk backfill row {i}",
                TimestampUtc = bulkMoment
            });
        }

        db.AuditLogs.Add(new AuditLog
        {
            Action = "CandidateWithdrawnFromFeed",
            EntityType = "Candidate",
            EntityId = 9001,
            Details = "Buried by the bulk write",
            TimestampUtc = new DateTime(2026, 7, 1, 9, 0, 0, DateTimeKind.Utc)
        });

        db.AuditLogs.Add(new AuditLog
        {
            Action = "SystemSettingsUpdated",
            EntityType = "SystemSettings",
            EntityId = 1,
            Details = "Older still",
            TimestampUtc = new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc)
        });

        await db.SaveChangesAsync();
    }

    /// <summary>Counts rendered body rows, ignoring the "no entries" placeholder.</summary>
    private static int RowCount(string html)
    {
        var body = Regex.Match(html, @"<tbody>(?<body>.*?)</tbody>", RegexOptions.Singleline);
        Assert.True(body.Success, "The audit table rendered no <tbody>.");
        return Regex.Matches(body.Groups["body"].Value, @"<tr>").Count;
    }

    private async Task<string> GetAsync(string url)
    {
        using var client = _factory.CreateClientAs(UserRole.SystemAdmin);
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>The default view is one page, not the whole log.</summary>
    [Fact]
    public async Task DefaultView_ShowsOnePageRatherThanEverything()
    {
        await SeedAsync(bulkCount: 176);

        var html = await GetAsync(Url);

        Assert.Equal(25, RowCount(html));
        Assert.Contains("178", html); // total count in the eyebrow: 176 + 2
    }

    /// <summary>
    /// The incident itself: an entry older than a 176-row bulk write is unreachable by scrolling and
    /// reachable by filtering. Without the filter it is not on the first page at all.
    /// </summary>
    [Fact]
    public async Task AnEntryBuriedByABulkWrite_IsReachableByFilteringOnItsAction()
    {
        await SeedAsync(bulkCount: 176);

        var unfiltered = await GetAsync(Url);
        Assert.DoesNotContain("Buried by the bulk write", unfiltered);

        var filtered = await GetAsync($"{Url}?action=CandidateWithdrawnFromFeed");

        Assert.Contains("Buried by the bulk write", filtered);
        Assert.Equal(1, RowCount(filtered));
    }

    [Fact]
    public async Task EntityTypeFilter_NarrowsToThatEntity()
    {
        await SeedAsync(bulkCount: 10);

        var html = await GetAsync($"{Url}?entityType=SystemSettings");

        Assert.Equal(1, RowCount(html));
        Assert.Contains("Older still", html);
    }

    [Fact]
    public async Task DateRangeFilter_ExcludesEntriesOutsideIt()
    {
        await SeedAsync(bulkCount: 10);

        // The bulk write is 2026-08-01; both extra entries are earlier.
        var html = await GetAsync($"{Url}?dateFrom=2026-07-15&dateTo=2026-08-31");

        Assert.Equal(10, RowCount(html));
        Assert.DoesNotContain("Older still", html);
    }

    /// <summary>
    /// <c>dateTo</c> is an inclusive calendar day, not an instant. An entry logged at midday on the
    /// end date must be included — an exclusive bound at midnight would silently drop a whole day,
    /// and it would be the most recent one.
    /// </summary>
    [Fact]
    public async Task DateToIsInclusiveOfTheWholeDay()
    {
        await SeedAsync(bulkCount: 5);

        var html = await GetAsync($"{Url}?dateFrom=2026-08-01&dateTo=2026-08-01");

        Assert.Equal(5, RowCount(html));
    }

    [Fact]
    public async Task DetailsSearch_MatchesOnSubstring()
    {
        await SeedAsync(bulkCount: 10);

        var html = await GetAsync($"{Url}?search=Older");

        Assert.Equal(1, RowCount(html));
        Assert.Contains("Older still", html);
    }

    /// <summary>
    /// Paging over rows that share a timestamp must not drop or repeat any — the property
    /// <c>ThenByDescending(Id)</c> exists for. Walks every page of a same-timestamp bulk write and
    /// checks the union is exactly the set that was written.
    /// </summary>
    [Fact]
    public async Task PagingASameTimestampBulkWrite_VisitsEveryRowExactlyOnce()
    {
        await SeedAsync(bulkCount: 176);

        var seen = new List<string>();
        for (var page = 1; page <= 8; page++)
        {
            var html = await GetAsync($"{Url}?action=VecSubmissionMarked&pageSize=25&pageNumber={page}");
            seen.AddRange(Regex.Matches(html, @"Bulk backfill row (?<n>\d+)").Select(m => m.Groups["n"].Value));
        }

        Assert.Equal(176, seen.Count);
        Assert.Equal(176, seen.Distinct().Count());
    }

    /// <summary>
    /// The pager must actually move. Trivial-looking, and it is the test the Sessions list does not
    /// have — which is why its own pager has been inert since it shipped: it binds its page number
    /// from <c>?page=</c>, and <c>page</c> is a Razor Pages route-value key, so the route provider
    /// answers first and the query string is never consulted. Nothing throws; the pager renders
    /// perfectly and hands back page 1 forever.
    /// </summary>
    [Fact]
    public async Task SecondPageShowsDifferentRowsFromTheFirst()
    {
        await SeedAsync(bulkCount: 176);

        var first = await GetAsync($"{Url}?pageSize=25&pageNumber=1");
        var second = await GetAsync($"{Url}?pageSize=25&pageNumber=2");

        Assert.Contains("Showing 1–25", first);
        Assert.Contains("Showing 26–50", second);
        Assert.Contains("Page 2 of", second);
    }

    /// <summary>Filters must survive the pager links, or paging a filtered list silently returns to
    /// the unfiltered log — the trap CLAUDE.md records for row actions on filtered list pages.</summary>
    [Fact]
    public async Task PagerLinksCarryTheActiveFilters()
    {
        await SeedAsync(bulkCount: 176);

        var html = await GetAsync($"{Url}?action=VecSubmissionMarked");

        Assert.Contains("action=VecSubmissionMarked&amp;pageSize=25&amp;pageNumber=2", html);
    }

    /// <summary>
    /// Background-job entries have no user, so "filter by user" cannot reach them and they are the
    /// ones nobody watched happen. The sentinel is negative because a real User.Id never is.
    /// </summary>
    [Fact]
    public async Task BackgroundJobFilter_ShowsOnlyUnattributedEntries()
    {
        await SeedAsync(bulkCount: 5);

        var html = await GetAsync($"{Url}?userId=-1");

        // Everything seeded here is unattributed, so all 7 match — and none renders a user name.
        Assert.Equal(7, RowCount(html));
        Assert.Contains("(background job)", html);
    }
}
