using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Web.Tests;

/// <summary>
/// /Account/MyVeDetails rendered for real (#226).
///
/// <para>The crawl in PageSmokeTests already requests this page, but the seeded admin is not linked
/// to a VE record — so what it proves is that the <i>unlinked</i> branch renders. Every form on the
/// page lives in the other branch, which is the half that can carry a render-time Razor bug of the
/// kind this harness exists to catch.</para>
///
/// <para>The two email states are separate tests because they are separate markup and, underneath,
/// separate services: no address on file writes directly, an existing one goes through the
/// confirmation flow. Rendering the wrong one would offer the wrong promise to the person reading
/// it.</para>
/// </summary>
public class MyVeDetailsPageTests : IClassFixture<WebAppFactory>
{
    private readonly WebAppFactory _factory;

    public MyVeDetailsPageTests(WebAppFactory factory) => _factory = factory;

    private async Task LinkSeededUserToVeAsync(string? email)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var person = await db.VolunteerExaminers.FirstAsync(v => v.Id == _factory.Seeded.VolunteerExaminerId);
        person.Email = email;

        var user = await db.Users.FirstAsync(u => u.Id == _factory.Seeded.UserId);
        user.VolunteerExaminerId = person.Id;

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Posts the way a browser does, token and all. Razor Pages validate antiforgery automatically,
    /// so a POST without one is rejected in middleware before the page is reached — which would make
    /// every handler test below pass for the wrong reason, or fail for one.
    /// </summary>
    private static async Task<HttpResponseMessage> PostWithTokenAsync(
        HttpClient client, string url, params (string Name, string Value)[] fields)
    {
        var page = await client.GetStringAsync("/Account/MyVeDetails");
        var token = System.Text.RegularExpressions.Regex
            .Match(page, """name="__RequestVerificationToken"[^>]*value="([^"]+)""" + "\"")
            .Groups[1].Value;
        Assert.NotEmpty(token);

        var form = fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value)).ToList();
        form.Add(new KeyValuePair<string, string>("__RequestVerificationToken", token));
        return await client.PostAsync(url, new FormUrlEncodedContent(form));
    }

    private async Task UnlinkAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.FirstAsync(u => u.Id == _factory.Seeded.UserId);
        user.VolunteerExaminerId = null;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task AnUnlinkedAccountIsToldWhyThereIsNothingToEdit()
    {
        await UnlinkAsync();
        using var client = _factory.CreateClientAs(UserRole.SessionManager);

        var response = await client.GetAsync("/Account/MyVeDetails");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("isn't linked to a volunteer examiner record", html);

        // The point of the state is that it does not offer edits it cannot honour.
        Assert.DoesNotContain("Save my details", html);
    }

    [Fact]
    public async Task ALinkedAccountGetsTheContactFormFilledFromTheirRecord()
    {
        await LinkSeededUserToVeAsync("ve@localhost");
        using var client = _factory.CreateClientAs(UserRole.SessionManager);

        var response = await client.GetAsync("/Account/MyVeDetails");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Save my details", html);

        // Populated from the record rather than rendered blank — a form that loses the existing
        // values on every visit quietly erases them on the next save.
        Assert.Contains("Test VE", html);
        Assert.Contains("N0TEST", html);
    }

    /// <summary>
    /// With an address on file the page must promise confirmation, because that is what actually
    /// happens: the direct write refuses and the request goes to VeEmailChangeService.
    /// </summary>
    [Fact]
    public async Task AnExistingAddressIsOfferedThroughTheConfirmationFlow()
    {
        await LinkSeededUserToVeAsync("ve@localhost");
        using var client = _factory.CreateClientAs(UserRole.SessionManager);

        var html = await client.GetStringAsync("/Account/MyVeDetails");

        Assert.Contains("Send confirmation to my current address", html);
        Assert.Contains("ve@localhost", html);
        Assert.DoesNotContain("Save email address", html);
    }

    /// <summary>
    /// And with none, the confirmation flow cannot run at all — it mails the address that isn't
    /// there. This branch is the reason the page exists.
    /// </summary>
    [Fact]
    public async Task AMissingAddressIsOfferedAsADirectFirstEntry()
    {
        await LinkSeededUserToVeAsync(null);
        using var client = _factory.CreateClientAs(UserRole.SessionManager);

        var html = await client.GetStringAsync("/Account/MyVeDetails");

        Assert.Contains("Save email address", html);
        Assert.DoesNotContain("Send confirmation to my current address", html);
    }

    /// <summary>
    /// End to end through the real handler: the address the direct path writes is the one the page
    /// then reports, and the page flips to the confirmed flow for any further change.
    /// </summary>
    [Fact]
    public async Task PostingAFirstAddressStoresItAndClosesTheDirectPath()
    {
        await LinkSeededUserToVeAsync(null);
        using var client = _factory.CreateClientAs(UserRole.SessionManager);

        var response = await PostWithTokenAsync(client, "/Account/MyVeDetails?handler=SetEmail",
            ("NewEmail", "first@localhost"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.VolunteerExaminers.FirstAsync(v => v.Id == _factory.Seeded.VolunteerExaminerId);
        Assert.Equal("first@localhost", stored.Email);

        var html = await client.GetStringAsync("/Account/MyVeDetails");
        Assert.Contains("Send confirmation to my current address", html);
    }

    /// <summary>
    /// The link is read off the signed-in account and never from the request, so an unlinked account
    /// posting to a handler has no record to act on. It must be refused rather than falling through
    /// to some default id.
    /// </summary>
    [Fact]
    public async Task AnUnlinkedAccountCannotPostAnEdit()
    {
        await UnlinkAsync();
        using var client = _factory.CreateClientAs(UserRole.SessionManager);

        var response = await PostWithTokenAsync(client, "/Account/MyVeDetails?handler=SetEmail",
            ("NewEmail", "nobody@localhost"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // Anonymous access is not asserted here: PageSmokeTests already challenges every discovered page,
    // this one included, and a second copy would only drift.
}
