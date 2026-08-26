using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Web.Tests;

/// <summary>
/// The site-wide System Banner (2026-08-26) — a general-purpose message shown on every page, set on
/// <c>Admin/SystemSettings</c>, SystemAdmin-only. Same idiom as <c>_TestModeBanner</c>: read fresh on
/// every request, no caching.
/// </summary>
public class SystemBannerTests
{
    private const string SettingsUrl = "/Admin/SystemSettings";

    private static async Task<string> AntiforgeryTokenAsync(HttpClient client, string url)
    {
        var page = await client.GetStringAsync(url);
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(token);
        return token;
    }

    private static async Task SetBannerAsync(WebAppFactory factory, bool enabled, string? message)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var settings = await db.SystemSettings.FirstOrDefaultAsync(s => s.Id == 1);
        if (settings is null)
        {
            settings = new SystemSettings { Id = 1 };
            db.SystemSettings.Add(settings);
        }

        settings.SystemBannerEnabled = enabled;
        settings.SystemBannerMessage = message;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task WhenEnabledWithAMessage_TheBannerRendersOnAnOrdinaryPage()
    {
        using var factory = new WebAppFactory();
        await SetBannerAsync(factory, enabled: true, message: "The FCC is experiencing a known delay.");

        using var client = factory.CreateClientAs(UserRole.SystemAdmin);
        var html = await client.GetStringAsync("/SessionManager/Index");

        Assert.Contains("The FCC is experiencing a known delay.", html);
    }

    [Fact]
    public async Task WhenDisabled_NothingRenders()
    {
        using var factory = new WebAppFactory();
        await SetBannerAsync(factory, enabled: false, message: "Should not appear anywhere.");

        using var client = factory.CreateClientAs(UserRole.SystemAdmin);
        var html = await client.GetStringAsync("/SessionManager/Index");

        Assert.DoesNotContain("Should not appear anywhere.", html);
    }

    [Fact]
    public async Task PostingTheBannerForm_PersistsItAndItThenRenders()
    {
        using var factory = new WebAppFactory();
        using var client = factory.CreateClientAs(UserRole.SystemAdmin);
        var token = await AntiforgeryTokenAsync(client, SettingsUrl);

        var response = await client.PostAsync($"{SettingsUrl}?handler=SystemBanner", new FormUrlEncodedContent([
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
            new KeyValuePair<string, string>("systemBannerEnabled", "true"),
            new KeyValuePair<string, string>("systemBannerMessage", "Planned maintenance tonight.")
        ]));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var html = await client.GetStringAsync("/SessionManager/Index");
        Assert.Contains("Planned maintenance tonight.", html);
    }

    [Fact]
    public async Task TeamAdmin_CannotPostToTheSystemSettingsPageAtAll()
    {
        using var factory = new WebAppFactory();
        using var client = factory.CreateClientAs(UserRole.TeamAdmin);

        var response = await client.GetAsync(SettingsUrl);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
