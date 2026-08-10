using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Web.Tests;

/// <summary>
/// Every page, actually rendered.
///
/// <para><b>Discovered from the app's own endpoints rather than a hand-written list</b>, so a new
/// page is covered the day it exists. A list would have to be maintained by the same person who
/// forgot to render the page in the first place.</para>
///
/// <para>What this catches that nothing else here can: tag-helper misuse, a service added to a page
/// constructor but never registered, a null reference in a view, a broken layout, and an
/// authorization attribute that quietly stopped matching. All of those compile, and all of them pass
/// every service-level test.</para>
/// </summary>
public class PageSmokeTests : IAsyncLifetime
{
    private WebAppFactory _factory = null!;

    public Task InitializeAsync()
    {
        // Seeding happens inside the factory, before the host starts — see WebAppFactory.Seed.
        _factory = new WebAppFactory();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Route parameters the crawler cannot invent. Anything not listed here that needs a parameter is
    /// reported rather than skipped silently — a page quietly excluded from the smoke test is exactly
    /// the page that breaks.
    /// </summary>
    private Dictionary<string, string> ParameterValues() => new()
    {
        ["id"] = _factory.Seeded.SessionId.ToString(),
        ["teamId"] = _factory.Seeded.TeamId.ToString(),
        ["vecId"] = _factory.Seeded.VecId.ToString(),
        ["userId"] = _factory.Seeded.UserId.ToString(),
        // Any well-formed guid: the page should tell an unknown token it is unknown, not throw.
        ["token"] = Guid.Empty.ToString()
    };

    private static readonly Regex RouteParameter = new(@"\{(?<name>[A-Za-z0-9_]+)(:[^}]+)?\??\}", RegexOptions.Compiled);

    /// <summary>
    /// Pages a crawl must not request. Kept deliberately tiny — every entry is coverage given up, so
    /// each one states why.
    /// </summary>
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    {
        "/Account/Logout",          // ends the session the rest of the crawl is using
        "/VeSelfService/SignIn",    // issues a sign-in email on POST; the GET is covered by the anonymous test below
        "/Error"                    // deliberately returns a problem page, so "did it render" proves nothing
    };

    public static IEnumerable<object[]> AdminPageRoutes()
    {
        // Resolved once, statically, because xUnit needs the theory data before the fixture exists.
        using var factory = new WebAppFactory();
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints;

        foreach (var endpoint in endpoints.OfType<RouteEndpoint>())
        {
            var template = endpoint.RoutePattern.RawText;
            if (string.IsNullOrWhiteSpace(template)) continue;

            var path = "/" + template.TrimStart('/');
            if (Excluded.Any(e => path.StartsWith(e, StringComparison.OrdinalIgnoreCase))) continue;

            // Razor Pages endpoints only — MVC controllers, static files and health endpoints are
            // not what this is for.
            if (endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Mvc.RazorPages.PageActionDescriptor>() is null) continue;

            yield return [path];
        }
    }

    /// <summary>
    /// The core assertion: every page renders for an authorised user. A 500 here is the class of bug
    /// that reached production twice in one day.
    /// </summary>
    [Theory]
    [MemberData(nameof(AdminPageRoutes))]
    public async Task EveryPageRendersForASystemAdmin(string route)
    {
        var url = RouteParameter.Replace(route, match =>
        {
            var name = match.Groups["name"].Value;
            return ParameterValues().TryGetValue(name, out var value)
                ? value
                : throw new InvalidOperationException(
                    $"Route '{route}' needs a value for '{{{name}}}' — add one to ParameterValues so the page stays covered.");
        });

        using var client = _factory.CreateClientAs(UserRole.SystemAdmin);
        var response = await client.GetAsync(url);

        // Redirects are legitimate (a page that needs a team selected, say). A server error never is.
        Assert.True(
            response.StatusCode != HttpStatusCode.InternalServerError,
            $"GET {url} returned 500.\n{await response.Content.ReadAsStringAsync()}");

        Assert.True(
            (int)response.StatusCode < 400 || response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound,
            $"GET {url} returned {(int)response.StatusCode}.");
    }

    /// <summary>
    /// Follows the links a rendered page actually emits.
    ///
    /// <para>This is the half that catches a <b>wrong</b> link rather than a broken page. The VE
    /// Directory rendered perfectly while every link on it pointed at no VE at all, because
    /// <c>asp-all-route-data</c> discarded the <c>asp-route-id</c> beside it — a "does it return
    /// 200" check saw nothing wrong.</para>
    ///
    /// <para><b>An empty href is the signature of that whole bug class.</b> When a tag helper cannot
    /// generate a URL — a missing route value, a renamed page, a typo'd handler — it does not throw
    /// and it does not warn. It emits <c>&lt;a href=""&gt;</c>, which renders as a link, looks
    /// entirely normal, and goes nowhere. Verified by reintroducing the original bug: the anchor
    /// came back as <c>&lt;a href=""&gt;Test VE&lt;/a&gt;</c>. Checking only the links that *do*
    /// have an href would have missed it exactly as the first version of this test did.</para>
    /// </summary>
    [Fact]
    public async Task LinksOnTheVeDirectoryPointSomewhereReal()
    {
        using var client = _factory.CreateClientAs(UserRole.SystemAdmin);

        var page = await client.GetAsync("/SessionManager/VeDirectory");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();

        var deadLinks = Regex.Matches(html, @"<a[^>]*href=""""[^>]*>(?<text>[^<]*)</a>")
            .Select(m => m.Groups["text"].Value.Trim())
            .Where(text => text.Length > 0)
            .ToList();

        Assert.True(deadLinks.Count == 0,
            "The page emitted anchor(s) with an empty href, which means URL generation failed and the "
            + "link goes nowhere — a missing route value or a renamed page. Link text: "
            + string.Join(", ", deadLinks));

        var detailLinks = Regex.Matches(html, @"href=""(?<href>/SessionManager/VeDetail[^""]*)""")
            .Select(m => WebUtility.HtmlDecode(m.Groups["href"].Value))
            .Distinct()
            .ToList();

        Assert.NotEmpty(detailLinks);

        foreach (var href in detailLinks)
        {
            // The id rides as a route segment (/VeDetail/12), since the page declares
            // @page "{id:int}"; the query form is accepted too so this survives that route changing.
            Assert.True(
                Regex.IsMatch(href, @"/VeDetail/\d+") || Regex.IsMatch(href, @"[?&]id=\d+"),
                $"Link {href} from the VE Directory names no VE.");

            var response = await client.GetAsync(href);
            Assert.True(
                response.StatusCode == HttpStatusCode.OK,
                $"Link {href} from the VE Directory returned {(int)response.StatusCode}.");
        }
    }

    /// <summary>An admin page must still refuse someone who isn't signed in — the crawl above proves pages render, not that they are protected.</summary>
    [Theory]
    [InlineData("/SessionManager/VeDirectory")]
    [InlineData("/Admin/Reconciliation")]
    [InlineData("/Admin/JobRunHistory")]
    public async Task AnAnonymousVisitorIsNotLetIn(string url)
    {
        using var client = _factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync(url);

        Assert.True(
            response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"GET {url} as an anonymous visitor returned {(int)response.StatusCode} — it should challenge or refuse.");
    }

    /// <summary>
    /// The session list projects its rows rather than loading Session entities, so every cell it
    /// renders comes from an expression EF has to translate. The crawl above only proves the page
    /// does not throw — it would pass just as happily with every column blank, which is exactly what
    /// a projection that dropped a field would look like.
    /// </summary>
    [Fact]
    public async Task TheSessionListRendersItsProjectedColumns()
    {
        using var client = _factory.CreateClientAs(UserRole.SystemAdmin);

        // The seeded session is a week old, so the default "Last 7 + Upcoming" range may exclude it.
        var response = await client.GetAsync("/SessionManager?applied=true&dateRange=&pageSize=25");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        // Assert against the row, not the page. Team names appear in the team-filter dropdown and
        // "Completed" is one of the status checkboxes, so both are present even when the list renders
        // zero rows — the first version of this test asserted on those and would have passed against
        // a projection that returned nothing at all.
        // Detail is @page "{id:int}", so the row link is path-style, not a query string.
        var rowLink = $"/SessionManager/Detail/{_factory.Seeded.SessionId}";
        Assert.Contains(rowLink, html);

        // Everything from here on is inside that one row: the cells the projection has to fill.
        var row = html[html.IndexOf("<tbody", StringComparison.Ordinal)..html.IndexOf("</tbody>", StringComparison.Ordinal)];
        Assert.Contains("ARRL", row);       // Vec.Name — replaced an Include
        Assert.Contains("TEST-TEAM", row);  // Team.Name — replaced an Include
        Assert.Contains(">1<", row);        // the candidate count that used to cost every candidate row
        // The seeded session has ExamToolsClosedUtc set and no TestingCompletedUtc — the exact case
        // that used to render "Active" forever, and the reason Session.IsCompleted exists.
        Assert.Contains("Completed", row);
    }

    /// <summary>A role without access must be refused rather than shown a VE's home address.</summary>
    [Fact]
    public async Task ASessionManagerCannotReachTheVeDirectory()
    {
        using var client = _factory.CreateClientAs(UserRole.SessionManager);

        var response = await client.GetAsync("/SessionManager/VeDirectory");

        Assert.True(
            response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Redirect or HttpStatusCode.Found,
            $"A SessionManager got {(int)response.StatusCode} from the VE Directory — it is TeamAdmin/SystemAdmin only.");
    }
}
