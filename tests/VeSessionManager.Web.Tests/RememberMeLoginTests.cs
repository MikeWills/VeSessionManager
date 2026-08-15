using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Web.Tests;

/// <summary>
/// "Keep me signed in" (#340), asserted where the bug actually lived: the <c>Set-Cookie</c> header.
///
/// <para>The original defect was invisible to every other kind of test. Sign-in worked, the session
/// worked, and the page rendered — the cookie simply carried no <c>Max-Age</c>, so it was a session
/// cookie that a phone's browser discarded on its next restart. Nothing about that is observable
/// from a page model, a status code, or a rendered page; only the header shows it.</para>
///
/// <para>Which is why these tests read the raw header rather than asking whether sign-in succeeded.
/// A test that only checked "am I signed in?" would have passed against the broken version.</para>
/// </summary>
public class RememberMeLoginTests : IClassFixture<WebAppFactory>
{
    private const string Password = "Test-Password-12345!";
    private const string IdentityCookiePrefix = ".AspNetCore.Identity.Application";

    private readonly WebAppFactory _factory;

    public RememberMeLoginTests(WebAppFactory factory) => _factory = factory;

    /// <summary>
    /// The seeded admin carries a placeholder hash (it exists to satisfy the startup guard; the test
    /// auth scheme never checks it), so a real password sign-in needs a real account.
    /// </summary>
    private async Task<string> EnsureSignInAbleUserAsync()
    {
        const string userName = "remember-me@localhost";

        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();

        var existing = await userManager.FindByNameAsync(userName);
        if (existing is null)
        {
            var user = new User
            {
                UserName = userName,
                Email = userName,
                Name = "Remember Me",
                Role = UserRole.SessionManager
            };

            var created = await userManager.CreateAsync(user, Password);
            Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));
        }

        return userName;
    }

    private static string AntiforgeryToken(string html)
    {
        var token = Regex.Match(html, """name="__RequestVerificationToken"[^>]*value="([^"]+)""" + "\"").Groups[1].Value;
        Assert.NotEmpty(token);
        return token;
    }

    /// <summary>
    /// The Set-Cookie line for the Identity cookie, or null if it was never issued. Takes the
    /// <i>last</i> match deliberately: if a response ever writes the same cookie twice, the browser
    /// keeps the last one, so that is the value whose behavior is worth asserting.
    /// </summary>
    private static string? IdentityCookie(HttpResponseHeaders headers) =>
        headers.TryGetValues("Set-Cookie", out var values)
            ? values.LastOrDefault(v => v.StartsWith(IdentityCookiePrefix, StringComparison.Ordinal))
            : null;

    private async Task<string?> SignInAndCaptureCookieAsync(bool rememberMe)
    {
        var userName = await EnsureSignInAbleUserAsync();
        using var client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var page = await client.GetStringAsync("/Account/Login");

        var form = new List<KeyValuePair<string, string>>
        {
            new("Input.UserName", userName),
            new("Input.Password", Password),
            new("__RequestVerificationToken", AntiforgeryToken(page))
        };

        if (rememberMe)
        {
            // Exactly what a ticked checkbox posts — the hidden false is always sent too.
            form.Add(new KeyValuePair<string, string>("Input.RememberMe", "true"));
        }

        form.Add(new KeyValuePair<string, string>("Input.RememberMe", "false"));

        var response = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(form));
        return IdentityCookie(response.Headers);
    }

    /// <summary>
    /// The fix. A remembered sign-in must produce a cookie the browser keeps across a restart, which
    /// means an explicit lifetime — and one measured in weeks, not the eight hours ExpireTimeSpan
    /// would give a merely-persistent cookie.
    /// </summary>
    [Fact]
    public async Task RememberMe_IssuesACookieThatSurvivesABrowserRestart()
    {
        var cookie = await SignInAndCaptureCookieAsync(rememberMe: true);

        Assert.NotNull(cookie);

        // `expires=` is the whole fix: it is what makes the cookie survive the browser process, and
        // its absence was the bug. Note ASP.NET Core writes `expires=` and NOT `max-age` — asserting
        // on max-age fails against a perfectly correct cookie, which is how this test first read.
        var expires = Regex.Match(cookie!, @"expires=([^;]+)", RegexOptions.IgnoreCase);
        Assert.True(expires.Success, $"No expires= in: {cookie}");

        var expiresUtc = DateTimeOffset.Parse(expires.Groups[1].Value.Trim(),
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);

        // ~30 days out. A range, not an exact instant: the value is computed against the server
        // clock mid-request. The assertion that matters is "weeks, not hours" — anything near 8
        // hours means the window fell back to ExpireTimeSpan and the fix is not doing anything.
        var days = (expiresUtc - DateTimeOffset.UtcNow).TotalDays;
        Assert.InRange(days, 29, 31);
    }

    /// <summary>
    /// The other half, and the one that stops this becoming "always remember me". An unticked
    /// sign-in must still be a session cookie — that behavior was deliberate for a shared machine,
    /// and #340 only ever asked for an opt-out from it.
    /// </summary>
    [Fact]
    public async Task WithoutRememberMe_TheCookieIsStillASessionCookie()
    {
        var cookie = await SignInAndCaptureCookieAsync(rememberMe: false);

        Assert.NotNull(cookie);
        Assert.DoesNotContain("expires=", cookie, StringComparison.OrdinalIgnoreCase);
        // No max-age either, but expires= is the one that decides it (see the sibling test).
        Assert.DoesNotContain("max-age=", cookie, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The control has to be on the page and inside the form that posts the credentials, or the
    /// server-side behavior above is unreachable from a browser.
    /// </summary>
    [Fact]
    public async Task TheLoginPageOffersTheCheckbox()
    {
        using var client = _factory.CreateClient();

        var html = await client.GetStringAsync("/Account/Login");

        Assert.Contains("Input.RememberMe", html);
        Assert.Contains("Keep me signed in on this device", html);
        // The window is stated rather than left to be discovered.
        Assert.Contains("30 days", html);
    }
}
