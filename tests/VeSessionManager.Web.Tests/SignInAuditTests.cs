using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Web.Tests;

/// <summary>
/// Sign-in events reach the audit log, with a source address (#265).
///
/// <para>Nothing recorded sign-ins at all before this — success or failure. The log answered
/// who/what/when for ordinary admin actions and nothing whatsoever about authentication, so a
/// credential-stuffing run, or a successful login on stolen credentials, left no trace: you could
/// see what an account did, never that it signed in, from where, or how many times it failed
/// first.</para>
///
/// <para>Driven through the real login page rather than a page model, because the thing being
/// asserted is that the row exists by the time the response is written — including on the failure
/// path, which returns early.</para>
/// </summary>
public class SignInAuditTests : IClassFixture<WebAppFactory>
{
    private const string Password = "Audit-Password-12345!";
    private readonly WebAppFactory _factory;

    public SignInAuditTests(WebAppFactory factory) => _factory = factory;

    private async Task<string> EnsureUserAsync(string userName)
    {
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        if (await userManager.FindByNameAsync(userName) is null)
        {
            var created = await userManager.CreateAsync(
                new User { UserName = userName, Email = userName, Name = "Audit Subject", Role = UserRole.SessionManager },
                Password);
            Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));
        }

        return userName;
    }

    private async Task<List<AuditLog>> AuditRowsAsync(params string[] actions)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AuditLogs.Where(a => actions.Contains(a.Action)).ToListAsync();
    }

    private static string AntiforgeryToken(string html) =>
        Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;

    private async Task<HttpResponseMessage> AttemptSignInAsync(HttpClient client, string userName, string password)
    {
        var page = await client.GetStringAsync("/Account/Login");
        return await client.PostAsync("/Account/Login", new FormUrlEncodedContent(
        [
            new("Input.UserName", userName),
            new("Input.Password", password),
            new("__RequestVerificationToken", AntiforgeryToken(page))
        ]));
    }

    [Fact]
    public async Task ASuccessfulSignInIsAudited_WithASourceAddress()
    {
        var userName = await EnsureUserAsync("audit-success@localhost");
        using var client = _factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        await AttemptSignInAsync(client, userName, Password);

        var row = Assert.Single(await AuditRowsAsync("SignedIn"));
        Assert.NotNull(row.UserId);
        Assert.False(string.IsNullOrWhiteSpace(row.SourceIpAddress),
            "A sign-in row without a source address answers 'who' but not 'from where', which is the half that was missing.");
    }

    /// <summary>
    /// The failure path returns early, so this is the one most easily left unaudited — and it is the
    /// one that matters for spotting a stuffing run.
    /// </summary>
    [Fact]
    public async Task AFailedSignInIsAudited()
    {
        var userName = await EnsureUserAsync("audit-failure@localhost");
        using var client = _factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        await AttemptSignInAsync(client, userName, "wrong-password");

        var rows = await AuditRowsAsync("SignInFailed", "SignInLockedOut");
        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.False(string.IsNullOrWhiteSpace(r.SourceIpAddress)));
    }

    /// <summary>
    /// An unknown username has no user to attribute the row to, so UserId is null and the attempted
    /// name goes in Details — recording which names were tried is the point, since that is the shape
    /// of an enumeration run.
    /// </summary>
    [Fact]
    public async Task AnUnknownUsernameIsAuditedWithNoUserId()
    {
        using var client = _factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        await AttemptSignInAsync(client, "no-such-person@localhost", "irrelevant");

        var rows = await AuditRowsAsync("SignInFailed");
        var row = Assert.Single(rows, r => r.Details != null && r.Details.Contains("no-such-person@localhost"));
        Assert.Null(row.UserId);
    }

    /// <summary>
    /// Auditing must not change what the caller can observe. The response for an unknown user and a
    /// wrong password has to stay identical, or the audit trail becomes an enumeration oracle — the
    /// exact thing the shared error message exists to prevent.
    /// </summary>
    [Fact]
    public async Task AuditingDoesNotMakeSignInFailuresDistinguishable()
    {
        var userName = await EnsureUserAsync("audit-oracle@localhost");
        using var client = _factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var wrongPassword = await AttemptSignInAsync(client, userName, "wrong-password");
        var unknownUser = await AttemptSignInAsync(client, "definitely-not-here@localhost", "wrong-password");

        Assert.Equal(wrongPassword.StatusCode, unknownUser.StatusCode);
        var a = await wrongPassword.Content.ReadAsStringAsync();
        var b = await unknownUser.Content.ReadAsStringAsync();
        Assert.Contains("Invalid username or password.", a);
        Assert.Contains("Invalid username or password.", b);
    }
}
