using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Web.Tests;

/// <summary>
/// TOTP two-factor authentication end to end (#356).
///
/// <para>Driven through the real pages rather than page models, because the properties that matter
/// are about what the HTTP response contains: whether an application cookie was issued, and where
/// the browser is sent. A page-model test cannot see either.</para>
/// </summary>
public class TwoFactorAuthTests : IClassFixture<WebAppFactory>
{
    private const string Password = "TwoFactor-Password-12345!";
    private const string IdentityCookiePrefix = ".AspNetCore.Identity.Application";

    private readonly WebAppFactory _factory;

    public TwoFactorAuthTests(WebAppFactory factory) => _factory = factory;

    private static HttpClient NewClient(WebAppFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private async Task<User> EnsureUserAsync(string userName, bool twoFactor)
    {
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();

        var user = await userManager.FindByNameAsync(userName);
        if (user is null)
        {
            user = new User { UserName = userName, Email = userName, Name = "TOTP Subject", Role = UserRole.SessionManager };
            var created = await userManager.CreateAsync(user, Password);
            Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));
        }

        if (twoFactor && !user.TwoFactorEnabled)
        {
            await userManager.ResetAuthenticatorKeyAsync(user);
            await userManager.SetTwoFactorEnabledAsync(user, true);
        }

        return user;
    }

    /// <summary>
    /// The code the user's authenticator would be showing right now, computed from the same stored
    /// key the app validates against — so this exercises the real TOTP path.
    ///
    /// <para>Not <c>GenerateTwoFactorTokenAsync</c>: for the authenticator provider that returns an
    /// <b>empty string</b> by design, because only the phone can generate. It reads exactly like the
    /// method you want, and produced a test that failed against correct code. See <see cref="Totp"/>.</para>
    /// </summary>
    private async Task<string> CurrentCodeAsync(string userName)
    {
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = await userManager.FindByNameAsync(userName);
        var key = await userManager.GetAuthenticatorKeyAsync(user!);
        return Totp.Generate(key!);
    }

    private static string Token(string html) =>
        Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;

    private static bool IssuedApplicationCookie(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
        && values.Any(v => v.StartsWith(IdentityCookiePrefix, StringComparison.Ordinal));

    private static async Task<HttpResponseMessage> PostPasswordAsync(HttpClient client, string userName, string password)
    {
        var page = await client.GetStringAsync("/Account/Login");
        return await client.PostAsync("/Account/Login", new FormUrlEncodedContent(
        [
            new("Input.UserName", userName),
            new("Input.Password", password),
            new("__RequestVerificationToken", Token(page))
        ]));
    }

    private static async Task<HttpResponseMessage> PostChallengeAsync(HttpClient client, string code, bool rememberDevice = false)
    {
        var page = await client.GetStringAsync("/Account/TwoFactorChallenge");
        return await client.PostAsync("/Account/TwoFactorChallenge", new FormUrlEncodedContent(
        [
            new("Input.Code", code),
            new("Input.RememberDevice", rememberDevice ? "true" : "false"),
            new("__RequestVerificationToken", Token(page))
        ]));
    }

    /// <summary>
    /// <b>The property the whole feature rests on.</b> A correct password must not produce a session
    /// on its own. Issuing the cookie and then challenging would mean a half-finished sign-in already
    /// carried a usable session — which is precisely what a second factor exists to prevent, and an
    /// easy thing to get wrong while everything still <i>looks</i> right in a browser.
    /// </summary>
    [Fact]
    public async Task CorrectPasswordAlone_IssuesNoSession_AndRedirectsToTheChallenge()
    {
        var userName = "totp-gate@localhost";
        await EnsureUserAsync(userName, twoFactor: true);
        using var client = NewClient(_factory);

        var response = await PostPasswordAsync(client, userName, Password);

        Assert.False(IssuedApplicationCookie(response),
            "A password alone must not issue an application cookie when two-factor is enabled.");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("TwoFactorChallenge", response.Headers.Location?.OriginalString);
    }

    /// <summary>
    /// Pins the handoff between the two requests. <c>GetTwoFactorAuthenticationUserAsync</c> expects
    /// the user id in a <c>ClaimTypes.Name</c> claim on the TwoFactorUserId cookie — Identity's own
    /// internal shape, which this app writes by hand because it does not use
    /// <c>PasswordSignInAsync</c> (see TwoFactorSignIn). That is behaviour rather than documentation,
    /// so it gets a test rather than trust.
    /// </summary>
    [Fact]
    public async Task TheChallengePageFindsThePendingUser()
    {
        var userName = "totp-handoff@localhost";
        await EnsureUserAsync(userName, twoFactor: true);
        using var client = NewClient(_factory);

        await PostPasswordAsync(client, userName, Password);
        var challenge = await client.GetAsync("/Account/TwoFactorChallenge");

        Assert.Equal(HttpStatusCode.OK, challenge.StatusCode);
        Assert.Contains("Two-factor authentication", await challenge.Content.ReadAsStringAsync());
    }

    /// <summary>Arriving cold, with no password step behind it, grants nothing.</summary>
    [Fact]
    public async Task TheChallengePageWithNoPendingSignIn_GoesBackToLogin()
    {
        using var client = NewClient(_factory);

        var response = await client.GetAsync("/Account/TwoFactorChallenge");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("Login", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task AValidCode_CompletesTheSignIn()
    {
        var userName = "totp-success@localhost";
        await EnsureUserAsync(userName, twoFactor: true);
        using var client = NewClient(_factory);

        await PostPasswordAsync(client, userName, Password);
        var response = await PostChallengeAsync(client, await CurrentCodeAsync(userName));

        Assert.True(IssuedApplicationCookie(response), "A verified code must issue the application cookie.");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public async Task AWrongCode_IssuesNoSession_AndIsAudited()
    {
        var userName = "totp-wrong@localhost";
        await EnsureUserAsync(userName, twoFactor: true);
        using var client = NewClient(_factory);

        await PostPasswordAsync(client, userName, Password);
        var response = await PostChallengeAsync(client, "000000");

        Assert.False(IssuedApplicationCookie(response));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "TwoFactorFailed"));
    }

    /// <summary>
    /// A recovery code signs in and is then spent. Without the one-time property it is simply a
    /// second, weaker password.
    /// </summary>
    [Fact]
    public async Task ARecoveryCode_SignsInOnceAndThenStopsWorking()
    {
        var userName = "totp-recovery@localhost";
        var user = await EnsureUserAsync(userName, twoFactor: true);

        string[] codes;
        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var fresh = await userManager.FindByNameAsync(userName);
            codes = [.. (await userManager.GenerateNewTwoFactorRecoveryCodesAsync(fresh!, 3))!];
        }

        using (var first = NewClient(_factory))
        {
            await PostPasswordAsync(first, userName, Password);
            var response = await PostChallengeAsync(first, codes[0]);
            Assert.True(IssuedApplicationCookie(response), "A valid recovery code must sign the user in.");
        }

        using (var second = NewClient(_factory))
        {
            await PostPasswordAsync(second, userName, Password);
            var response = await PostChallengeAsync(second, codes[0]);
            Assert.False(IssuedApplicationCookie(response), "A recovery code must not work twice.");
        }

        _ = user;
    }

    /// <summary>
    /// A trusted device skips the challenge next time. This is what stops a 30-day remembered session
    /// asking for a code on every cookie refresh — the reason the device-trust window was matched to
    /// the remember-me window rather than to the 8-hour session.
    /// </summary>
    [Fact]
    public async Task ATrustedDeviceSkipsTheChallengeOnTheNextSignIn()
    {
        var userName = "totp-trusted@localhost";
        await EnsureUserAsync(userName, twoFactor: true);
        using var client = NewClient(_factory);

        await PostPasswordAsync(client, userName, Password);
        await PostChallengeAsync(client, await CurrentCodeAsync(userName), rememberDevice: true);

        // Same client, so the trust cookie rides along.
        var again = await PostPasswordAsync(client, userName, Password);

        Assert.True(IssuedApplicationCookie(again), "A trusted device should sign in on the password alone.");
        Assert.DoesNotContain("TwoFactorChallenge", again.Headers.Location?.OriginalString ?? "");
    }

    /// <summary>
    /// A recovery code must NOT earn device trust. Someone using one has lost their authenticator, so
    /// treating that device as trusted for 30 days would mean a stolen recovery code buys a month of
    /// unchallenged access — the opposite of what the code is for.
    /// </summary>
    [Fact]
    public async Task ARecoveryCodeDoesNotEarnDeviceTrust()
    {
        var userName = "totp-recovery-trust@localhost";
        await EnsureUserAsync(userName, twoFactor: true);

        string[] codes;
        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var fresh = await userManager.FindByNameAsync(userName);
            codes = [.. (await userManager.GenerateNewTwoFactorRecoveryCodesAsync(fresh!, 3))!];
        }

        using var client = NewClient(_factory);
        await PostPasswordAsync(client, userName, Password);
        await PostChallengeAsync(client, codes[0], rememberDevice: true);

        var again = await PostPasswordAsync(client, userName, Password);

        Assert.False(IssuedApplicationCookie(again));
        Assert.Contains("TwoFactorChallenge", again.Headers.Location?.OriginalString);
    }

    /// <summary>An account without two-factor is completely unaffected — the password path is
    /// byte-for-byte what it was.</summary>
    [Fact]
    public async Task AnAccountWithoutTwoFactorSignsInOnThePasswordAlone()
    {
        var userName = "totp-none@localhost";
        await EnsureUserAsync(userName, twoFactor: false);
        using var client = NewClient(_factory);

        var response = await PostPasswordAsync(client, userName, Password);

        Assert.True(IssuedApplicationCookie(response));
        Assert.DoesNotContain("TwoFactorChallenge", response.Headers.Location?.OriginalString ?? "");
    }

    /// <summary>
    /// Enrolment must not switch two-factor on before a code has verified. Enabling first is the
    /// version of this that locks people out — a mistyped key or a phone with a drifted clock, and
    /// the account now demands a code nobody can produce.
    /// </summary>
    [Fact]
    public async Task EnrolmentDoesNotEnableTwoFactorUntilACodeVerifies()
    {
        using var client = _factory.CreateClientAs(UserRole.SystemAdmin);

        var page = await client.GetStringAsync("/Account/EnableAuthenticator");
        var response = await client.PostAsync("/Account/EnableAuthenticator", new FormUrlEncodedContent(
        [
            new("Input.Code", "000000"),
            new("__RequestVerificationToken", Token(page))
        ]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var seeded = await db.Users.SingleAsync(u => u.Id == _factory.Seeded.UserId);
        Assert.False(seeded.TwoFactorEnabled);
    }

    /// <summary>The enrolment page offers a scannable code and the same secret in typeable form —
    /// the QR and the key must be the same secret or the codes are silently always wrong.</summary>
    [Fact]
    public async Task EnrolmentOffersBothAQrCodeAndATypeableKey()
    {
        using var client = _factory.CreateClientAs(UserRole.SystemAdmin);

        var page = await client.GetStringAsync("/Account/EnableAuthenticator");

        Assert.Contains("<svg", page);
        Assert.Contains("totp-key", page);
    }
}
