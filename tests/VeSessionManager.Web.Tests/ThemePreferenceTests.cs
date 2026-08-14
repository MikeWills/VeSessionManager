using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Web.Tests;

/// <summary>
/// Dark mode remembered on the account rather than in one browser, and the OS setting as the
/// default before anyone has chosen.
///
/// <para><b>What is asserted here, and what cannot be.</b> The resolution order lives in
/// wwwroot/js/theme.js and runs in a browser, so no test in this project executes it. What these
/// tests pin is the half the server owns, and it is the half that carries the two silent failure
/// modes: emitting <c>data-theme</c> when the account has made no choice (which would pin every
/// pre-existing account to light and quietly defeat the OS default, while looking completely
/// correct), and emitting the attribute too late in the document to beat the first paint.</para>
/// </summary>
public class ThemePreferenceTests : IClassFixture<WebAppFactory>
{
    private readonly WebAppFactory _factory;

    public ThemePreferenceTests(WebAppFactory factory) => _factory = factory;

    private async Task SetPreferenceAsync(ThemePreference preference)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Users
            .Where(u => u.Id == _factory.Seeded.UserId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.ThemePreference, preference));
    }

    private async Task<ThemePreference> ReadPreferenceAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Users
            .Where(u => u.Id == _factory.Seeded.UserId)
            .Select(u => u.ThemePreference)
            .SingleAsync();
    }

    /// <summary>The opening &lt;html&gt; tag, which is the only place the server states the theme.</summary>
    private static string HtmlTag(string html) => Regex.Match(html, "<html[^>]*>").Value;

    private static string ThemeToggleToken(string html)
    {
        var token = Regex.Match(html, "data-antiforgery-token=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(token);
        return WebUtility.HtmlDecode(token);
    }

    /// <summary>
    /// The load-bearing case, and the one an implementation gets wrong by being helpful. An account
    /// that has never touched the toggle must produce NO data-theme at all: theme.js treats a
    /// server-rendered attribute as authoritative and stops there, so writing "light" for
    /// ThemePreference.System would render identically for a light-mode user and silently override
    /// the OS setting for everyone else. Every account that predates this feature is in this state.
    /// </summary>
    [Fact]
    public async Task NoChoiceMade_RendersNoThemeAttribute_SoTheOsSettingDecides()
    {
        await SetPreferenceAsync(ThemePreference.System);

        using var client = _factory.CreateClientAs(UserRole.SystemAdmin);
        var html = await client.GetStringAsync("/SessionManager/Index");

        Assert.DoesNotContain("data-theme", HtmlTag(html));
    }

    [Theory]
    [InlineData(ThemePreference.Dark, "dark")]
    [InlineData(ThemePreference.Light, "light")]
    public async Task ASavedChoiceIsRenderedOntoTheHtmlElement(ThemePreference preference, string expected)
    {
        await SetPreferenceAsync(preference);

        using var client = _factory.CreateClientAs(UserRole.SystemAdmin);
        var html = await client.GetStringAsync("/SessionManager/Index");

        Assert.Contains($"data-theme=\"{expected}\"", HtmlTag(html));
    }

    /// <summary>
    /// The anti-flash guarantee, which is otherwise invisible: a correct data-theme that arrives
    /// after the body has painted still shows a white flash on every navigation for a dark-mode
    /// user. Both halves have to be in the head — the attribute (server-rendered) and theme.js
    /// (which resolves it for signed-out pages and for accounts with no saved choice).
    ///
    /// <para>An inline script would be the conventional fix and is not available: the CSP is
    /// script-src 'self' with no nonce, so an inline script renders fine and never runs.</para>
    /// </summary>
    [Fact]
    public async Task TheThemeScriptRunsBeforeTheBodyPaints()
    {
        using var client = _factory.CreateClientAs(UserRole.SystemAdmin);
        var html = await client.GetStringAsync("/SessionManager/Index");

        // Matched loosely on purpose: MapStaticAssets makes asp-append-version emit a *fingerprinted*
        // filename (/js/theme.qjcbqpniws.js) rather than appending a ?v= query, so a literal
        // "/js/theme.js" finds nothing and reports the script as missing when it is right there.
        var tag = Regex.Match(html, """<script[^>]*\bsrc="[^"]*/js/theme[^"]*\.js[^"]*"[^>]*>""");
        Assert.True(tag.Success, "theme.js is not referenced at all — the OS default and the saved preference both stop working.");

        var body = html.IndexOf("<body", StringComparison.Ordinal);
        Assert.True(tag.Index < body, "theme.js must load in <head>; below <body> it resolves the theme after the page has already painted.");

        // Not deferred or async, for the same reason: either one postpones execution until after the
        // document is parsed, which is precisely the flash this is here to prevent.
        Assert.DoesNotContain("defer", tag.Value);
        Assert.DoesNotContain("async", tag.Value);
    }

    /// <summary>The whole point of the feature: the choice outlives this browser.</summary>
    [Fact]
    public async Task TogglingSavesTheChoiceToTheAccount()
    {
        await SetPreferenceAsync(ThemePreference.Light);

        using var client = _factory.CreateClientAs(UserRole.SystemAdmin);
        var html = await client.GetStringAsync("/SessionManager/Index");

        var response = await client.SendAsync(BuildRequest(ThemeToggleToken(html), "dark"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(ThemePreference.Dark, await ReadPreferenceAsync());
    }

    /// <summary>
    /// The token travels in a header rather than a form field, because a fetch() has no form to
    /// carry one. That relies on AntiforgeryOptions.HeaderName, which is left at its default — so
    /// this pair of tests is what pins it: setting HeaderName to null (or renaming it) makes the
    /// header ignored, which breaks TogglingSavesTheChoiceToTheAccount rather than this test, and
    /// removing validation entirely breaks this one. Neither direction is visible from clicking the
    /// toggle, which works identically in all three cases.
    /// </summary>
    [Fact]
    public async Task WithoutAnAntiforgeryTokenTheSaveIsRefused()
    {
        await SetPreferenceAsync(ThemePreference.Light);

        using var client = _factory.CreateClientAs(UserRole.SystemAdmin);
        await client.GetStringAsync("/SessionManager/Index");

        var response = await client.PostAsync(
            "/Account/Theme",
            new FormUrlEncodedContent([new KeyValuePair<string, string>("theme", "dark")]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ThemePreference.Light, await ReadPreferenceAsync());
    }

    /// <summary>
    /// "System" means "no choice yet" and the toggle never sends it, so a hand-crafted POST must not
    /// be able to put an account into a state the UI can neither reach nor show.
    /// </summary>
    [Theory]
    [InlineData("system")]
    [InlineData("System")]
    [InlineData("")]
    [InlineData("purple")]
    public async Task OnlyLightAndDarkAreAccepted(string theme)
    {
        await SetPreferenceAsync(ThemePreference.Dark);

        using var client = _factory.CreateClientAs(UserRole.SystemAdmin);
        var html = await client.GetStringAsync("/SessionManager/Index");

        var response = await client.SendAsync(BuildRequest(ThemeToggleToken(html), theme));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ThemePreference.Dark, await ReadPreferenceAsync());
    }

    /// <summary>
    /// Authenticated by the app-wide FallbackPolicy rather than an [Authorize] attribute, so this
    /// pins the thing that would break if the policy were ever narrowed.
    /// </summary>
    [Fact]
    public async Task ASignedOutVisitorCannotSaveAPreference()
    {
        // No role header: the harness's auth handler returns NoResult, i.e. anonymous.
        using var client = _factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.PostAsync(
            "/Account/Theme",
            new FormUrlEncodedContent([new KeyValuePair<string, string>("theme", "dark")]));

        Assert.NotEqual(HttpStatusCode.NoContent, response.StatusCode);
    }

    /// <summary>
    /// An HttpRequestMessage rather than PostAsync's content, because the token is a *request*
    /// header — HttpContentHeaders rejects it outright as a misused header name.
    /// </summary>
    private static HttpRequestMessage BuildRequest(string token, string theme)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Theme")
        {
            Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("theme", theme)])
        };
        request.Headers.Add("RequestVerificationToken", token);
        return request;
    }
}
