using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
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

        // Only a page that actually rendered has markup worth inspecting; a redirect or a 403 body
        // is not this page's HTML.
        if (response.StatusCode == HttpStatusCode.OK)
        {
            AssertNoEmptyHrefs(await response.Content.ReadAsStringAsync(), url);
        }
    }

    /// <summary>
    /// <b>An empty href is the signature of a whole bug class.</b> When a tag helper cannot generate
    /// a URL — a missing route value, a renamed page, a typo'd handler — it does not throw and it
    /// does not warn. It emits <c>&lt;a href=""&gt;</c>, which renders as a link, looks entirely
    /// normal, and goes nowhere.
    ///
    /// <para>The bug that prompted it: the VE Directory rendered perfectly while every link on it
    /// pointed at no VE at all, because <c>asp-all-route-data</c> discarded the <c>asp-route-id</c>
    /// beside it. A "does it return 200" check saw nothing wrong. Verified by reintroducing that
    /// bug — the anchor came back as <c>&lt;a href=""&gt;Test VE&lt;/a&gt;</c>. Checking only the
    /// links that *do* have an href would have missed it, exactly as the first version of the
    /// check did.</para>
    ///
    /// <para>Ran against one page until #270; the doc comment above was already describing a bug
    /// *class*, and the theory above already discovers every page from the app's own
    /// <c>EndpointDataSource</c>, so scoping it to one page was leaving the other 45 uncovered for
    /// no reason.</para>
    /// </summary>
    private static void AssertNoEmptyHrefs(string html, string url)
    {
        var deadLinks = Regex.Matches(html, @"<a[^>]*href=""""[^>]*>(?<text>[^<]*)</a>")
            .Select(m => m.Groups["text"].Value.Trim())
            .Where(text => text.Length > 0)
            .ToList();

        Assert.True(deadLinks.Count == 0,
            $"GET {url} emitted anchor(s) with an empty href, which means URL generation failed and the "
            + "link goes nowhere — a missing route value or a renamed page. Link text: "
            + string.Join(", ", deadLinks));
    }

    /// <summary>
    /// Follows the links a rendered page actually emits.
    ///
    /// <para>This is the half that catches a <b>wrong</b> link rather than a broken page. The VE
    /// Directory rendered perfectly while every link on it pointed at no VE at all, because
    /// <c>asp-all-route-data</c> discarded the <c>asp-route-id</c> beside it — a "does it return
    /// 200" check saw nothing wrong.</para>
    ///
    /// <para>The empty-href half of this moved to <see cref="AssertNoEmptyHrefs"/> and now runs on
    /// every page (#270) — see there for why an empty href is the signature of the class. What stays
    /// here is the part that is specific to this page: that the links it emits name a real VE and
    /// that following one works.</para>
    /// </summary>
    [Fact]
    public async Task LinksOnTheVeDirectoryPointSomewhereReal()
    {
        using var client = _factory.CreateClientAs(UserRole.SystemAdmin);

        var page = await client.GetAsync("/SessionManager/VeDirectory");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();

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

    /// <summary>
    /// The fallback policy (#158) makes authentication the default, so the risk flips: instead of a
    /// new page being accidentally public, an existing public page can be accidentally locked. These
    /// are the pages a signed-out person must still reach — several of them are reached *because*
    /// they cannot sign in, and requiring authentication would be a redirect loop.
    /// </summary>
    [Theory]
    [InlineData("/Account/Login")]
    [InlineData("/Account/ForgotPassword")]
    [InlineData("/Account/AccessDenied")]
    [InlineData("/Privacy")]
    [InlineData("/Error")]
    [InlineData("/")]
    [InlineData("/VeSelfService/Enter")]
    public async Task PublicPagesStayReachableWhileSignedOut(string url)
    {
        using var client = _factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync(url);

        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"GET {url} while signed out returned {(int)response.StatusCode} — it must stay public. "
            + "If the fallback policy caught it, the page needs [AllowAnonymous].");
    }

    /// <summary>
    /// ⚠️ The trap in #158, whose own description got this wrong: it said the Square webhook was
    /// "unaffected". A fallback policy applies to minimal-API endpoints too, not only Razor Pages, so
    /// without an explicit AllowAnonymous every Square delivery would start being refused.
    ///
    /// <para>That failure would be invisible from inside the app — Square retries, gives up, and
    /// payments simply stop being recorded, with nothing logged here. The endpoint's real gate is
    /// HMAC verification against the team's signature key, which is stronger than any cookie.</para>
    ///
    /// <para><b>Asserted on endpoint metadata rather than a status code, deliberately.</b> Probing
    /// the endpoint cannot tell the two failures apart: the handler itself answers a missing or bad
    /// signature with <b>401</b>, which is the same status authorization produces — measured, not
    /// assumed, by removing AllowAnonymous and watching the response stay identical. A status-code
    /// test would therefore have passed whether or not the exemption was there, which is worse than
    /// no test.</para>
    /// </summary>
    [Fact]
    public void TheSquareWebhookIsExemptFromTheFallbackPolicy()
    {
        var endpoints = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints;

        var webhook = Assert.Single(
            endpoints.OfType<RouteEndpoint>(),
            e => e.RoutePattern.RawText?.StartsWith("/webhooks/square", StringComparison.OrdinalIgnoreCase) == true);

        Assert.True(
            webhook.Metadata.GetMetadata<IAllowAnonymous>() is not null,
            "The Square webhook has no AllowAnonymous metadata, so the fallback authorization policy "
            + "applies to it. Square is not a signed-in user; its deliveries would be refused, and "
            + "nothing inside this app would log it. Its real gate is HMAC signature verification.");
    }

    /// <summary>
    /// ⚠️ Regression guard for a bug shipped in v0.3.0 and live for about an hour.
    ///
    /// <para><c>MapStaticAssets</c> registers real endpoints, so the FallbackPolicy added in #158
    /// applied to them: every CSS, JS, font and image request from a signed-out visitor was
    /// redirected to the login page. The pages still returned 200 and still rendered — they just
    /// arrived with no styling and no scripts, which is why nothing caught it. The login page was
    /// the worst affected, being the one page a signed-out person is guaranteed to see.</para>
    ///
    /// <para>Signed-in users saw nothing wrong at all, which is exactly the shape of failure that
    /// survives a developer testing their own change.</para>
    /// </summary>
    [Theory]
    [InlineData("/css/app.css")]
    [InlineData("/js/app.js")]
    [InlineData("/lib/bootstrap-icons/bootstrap-icons.css")]
    public async Task StaticAssetsAreServedToAnonymousVisitors(string path)
    {
        using var client = _factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync(path);

        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"GET {path} while signed out returned {(int)response.StatusCode}. Static assets are endpoints, "
            + "so the fallback authorization policy applies to them unless MapStaticAssets says "
            + "AllowAnonymous — and a signed-out visitor then gets an unstyled, script-less page.");
    }

    /// <summary>
    /// Client-side validation needs three scripts in dependency order, and **every one of them must
    /// actually be served**. Before #156 the page referenced jquery.validate and unobtrusive but
    /// nothing loaded jQuery, so the console threw "jQuery is not defined" and validation never ran
    /// — invisible from the server, because server-side validation still rejected bad input.
    ///
    /// <para>Rendering the page is not enough to catch that: a missing or misspelled script tag is
    /// still valid HTML and the page returns 200 either way. So this follows each referenced script
    /// and requires a 200, which is the part a smoke test would otherwise skip straight past.</para>
    /// </summary>
    [Theory]
    [InlineData("/Account/Login")]
    [InlineData("/Account/ForgotPassword")]
    public async Task ValidationScriptsAreReferencedInOrderAndActuallyServed(string url)
    {
        using var client = _factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var html = await (await client.GetAsync(url)).Content.ReadAsStringAsync();

        var jquery = html.IndexOf("/lib/jquery/dist/jquery", StringComparison.Ordinal);
        var validate = html.IndexOf("/lib/jquery-validation/dist/jquery.validate", StringComparison.Ordinal);
        var unobtrusive = html.IndexOf("/lib/jquery-validation-unobtrusive/", StringComparison.Ordinal);

        Assert.True(jquery >= 0, $"{url} references no jQuery — the validation plugins below it cannot run.");
        Assert.True(jquery < validate, "jQuery must be loaded before jquery.validate.");
        Assert.True(validate < unobtrusive, "jquery.validate must be loaded before the unobtrusive adapter.");

        foreach (var src in ScriptSources(html).Where(s => s.Contains("/lib/jquery", StringComparison.Ordinal)))
        {
            var response = await client.GetAsync(src);
            Assert.True(response.StatusCode == HttpStatusCode.OK,
                $"{url} references {src}, which returned {(int)response.StatusCode}. A script tag pointing at "
                + "nothing looks identical to a working one in the rendered HTML.");
        }
    }

    private static IEnumerable<string> ScriptSources(string html) =>
        Regex.Matches(html, "<script[^>]+src=\"([^\"]+)\"", RegexOptions.IgnoreCase)
            .Select(m => m.Groups[1].Value)
            .Select(src => src.Split('?')[0]);

    /// A cookie can outlive the account it names — the row is deleted, or the database is restored
    /// beneath a browser that still holds a valid, correctly-signed cookie. Authorization is
    /// satisfied, so the page runs, looks the user up, gets null and throws.
    ///
    /// <para>Nineteen call sites across twelve pages did exactly that, and the person saw a 500 for
    /// something that was not their fault and that they could not fix — the one action that would
    /// have helped, signing out, is what an error page does not offer. StaleAuthCookieFilter now
    /// resolves it before any handler runs.</para>
    /// </summary>
    [Theory]
    [InlineData("/SessionManager")]
    [InlineData("/SessionManager/ApplicantStatus")]
    [InlineData("/SessionManager/VeDirectory")]
    [InlineData("/Admin/Reconciliation")]
    public async Task AStaleCookieRedirectsToLoginRatherThanThrowing(string url)
    {
        using var client = _factory.CreateClientWithStaleCookie();

        var response = await client.GetAsync(url);

        Assert.False(response.StatusCode == HttpStatusCode.InternalServerError,
            $"GET {url} with a cookie naming a deleted user returned 500. It should sign the cookie "
            + "out and redirect to login — see StaleAuthCookieFilter.");

        Assert.True(response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found,
            $"GET {url} with a stale cookie returned {(int)response.StatusCode}; expected a redirect to login.");
        Assert.Contains("/Account/Login", response.Headers.Location?.OriginalString ?? "");
    }

    /// <summary>
    /// The control: a real signed-in user must be entirely unaffected. A filter that runs on every
    /// page is exactly the thing that could quietly log everyone out.
    /// </summary>
    [Fact]
    public async Task AValidSessionIsUntouchedByTheStaleCookieFilter()
    {
        using var client = _factory.CreateClientAs(UserRole.SystemAdmin);

        var response = await client.GetAsync("/SessionManager");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
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
