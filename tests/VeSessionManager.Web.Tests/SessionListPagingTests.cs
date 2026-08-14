using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Web.Tests;

/// <summary>
/// The session list's pager actually moves (#368).
///
/// <para><b>It did not, from 2026-07-28 to 2026-08-14.</b> The page number bound from
/// <c>?page=</c>, and <c>page</c> is a Razor Pages route-value key — the framework puts the page's
/// own path there ("/SessionManager/Index") and the route value provider runs before the query
/// string provider. Binding took the route value, failed to parse it as an int, and left the
/// default. Every page rendered as page 1.</para>
///
/// <para>Nothing threw and nothing was logged. The pager rendered correctly — right page count,
/// right "Showing X–Y of Z" — and Next simply did nothing. This is the cheapest possible test and
/// the only one that would have caught it, which is the point worth carrying forward: a pager needs
/// a test that asserts the second page <i>differs from</i> the first, not that the page renders.</para>
/// </summary>
public class SessionListPagingTests : IClassFixture<WebAppFactory>
{
    private const string Url = "/SessionManager/Index";

    private readonly WebAppFactory _factory;

    public SessionListPagingTests(WebAppFactory factory) => _factory = factory;

    /// <summary>
    /// Enough sessions to need several pages, cloned from the seeded one so every FK
    /// (Team/Vec/FeeConfiguration) is satisfied without restating the fixture here.
    /// </summary>
    private async Task<int> SeedSessionsAsync(int count, string label = "PAGE")
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var template = await db.Sessions.AsNoTracking().FirstAsync();

        for (var i = 0; i < count; i++)
        {
            db.Sessions.Add(new Session
            {
                TeamId = template.TeamId,
                VecId = template.VecId,
                FeeConfigurationId = template.FeeConfigurationId,
                ExamToolsSessionId = $"paging-{Guid.NewGuid():N}",
                Title = $"Paging fixture {i}",
                // ExtId, not Title: the list renders ExtId in its own column, while TitleLine is the
                // formatted date — the Title field is never displayed at all.
                ExtId = $"{label}-{i}",
                ScheduledStartUtc = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc).AddDays(i),
                Status = SessionStatus.Active
            });
        }

        await db.SaveChangesAsync();
        return await db.Sessions.CountAsync();
    }

    private async Task<string> GetAsync(string url)
    {
        using var client = _factory.CreateClientAs(UserRole.SystemAdmin);
        return await client.GetStringAsync(url);
    }

    private static string Showing(string html) => Regex.Match(html, @"Showing [^<]*").Value.Trim();

    /// <summary>
    /// The regression itself. "applied=true" so the filter cookie is bypassed and no date preset
    /// narrows the list; the assertion is only that page 2 is a different window onto the data.
    /// </summary>
    [Fact]
    public async Task SecondPageShowsDifferentRowsFromTheFirst()
    {
        await SeedSessionsAsync(30);

        var first = await GetAsync($"{Url}?applied=true&pageSize=10&pageNumber=1");
        var second = await GetAsync($"{Url}?applied=true&pageSize=10&pageNumber=2");

        Assert.Contains("Showing 1–10 of", Showing(first));
        Assert.Contains("Showing 11–20 of", Showing(second));
    }

    /// <summary>
    /// The pager's own links must carry the page number in the form the page can actually read. A
    /// link emitting the old <c>page=</c> key would render, be clickable, and go nowhere — so
    /// asserting on the generated href is what stops the two halves drifting apart again.
    /// </summary>
    [Fact]
    public async Task PagerLinksUseTheKeyThePageBindsFrom()
    {
        await SeedSessionsAsync(30);

        var html = await GetAsync($"{Url}?applied=true&pageSize=10&pageNumber=1");

        Assert.Contains("pageNumber=2", html);
        Assert.DoesNotMatch(new Regex(@"[?&]page=\d"), html);
    }

    /// <summary>
    /// Walks every page and checks each session appears exactly once. Catches the subtler failure a
    /// two-page comparison can miss — an off-by-one in Skip, or a non-deterministic order dropping
    /// and repeating rows across boundaries.
    /// </summary>
    [Fact]
    public async Task PagingVisitsEverySessionExactlyOnce()
    {
        var total = await SeedSessionsAsync(30, "WALK");
        const int pageSize = 10;
        var pages = (int)Math.Ceiling(total / (double)pageSize);

        var seen = new List<string>();
        for (var page = 1; page <= pages; page++)
        {
            var html = await GetAsync($"{Url}?applied=true&pageSize={pageSize}&pageNumber={page}");
            seen.AddRange(Regex.Matches(html, @"WALK-(?<n>\d+)").Select(m => m.Groups["n"].Value));
        }

        Assert.Equal(30, seen.Count);
        Assert.Equal(30, seen.Distinct().Count());
    }
}
