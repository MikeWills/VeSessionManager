using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Web.Tests;

/// <summary>
/// <c>/Admin/FccStatus</c> (2026-08-26) — the one setting on this app's manual FCC-wide-issue escape
/// hatch that a page test actually needs to pin: unlike the rest of <c>SystemSettings</c>, this page
/// is reachable by TeamAdmin as well as SystemAdmin, since either role may be the one to first notice
/// a real FCC outage. See <c>SystemSettings.FccIssueActive</c> and its sibling switches.
/// </summary>
public class FccStatusPageTests
{
    private const string Url = "/Admin/FccStatus";

    private static async Task<string> AntiforgeryTokenAsync(HttpClient client, string url)
    {
        var page = await client.GetStringAsync(url);
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(token);
        return token;
    }

    [Fact]
    public async Task TeamAdmin_CanReachThePage()
    {
        using var factory = new WebAppFactory();
        using var client = factory.CreateClientAs(UserRole.TeamAdmin);

        var response = await client.GetAsync(Url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SessionManager_IsForbidden()
    {
        using var factory = new WebAppFactory();
        using var client = factory.CreateClientAs(UserRole.SessionManager);

        var response = await client.GetAsync(Url);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PostingTheForm_PersistsAllFourSwitches()
    {
        using var factory = new WebAppFactory();
        using var client = factory.CreateClientAs(UserRole.SystemAdmin);
        var token = await AntiforgeryTokenAsync(client, Url);

        var response = await client.PostAsync(Url, new FormUrlEncodedContent([
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
            new KeyValuePair<string, string>("fccIssueActive", "true"),
            new KeyValuePair<string, string>("suppressNewLicenseReminders", "true"),
            new KeyValuePair<string, string>("suppressUpgradeReminders", "false"),
            new KeyValuePair<string, string>("suppressRenewalReminders", "true")
        ]));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var settings = await dbContext.SystemSettings.SingleAsync();
        Assert.True(settings.FccIssueActive);
        Assert.True(settings.FccIssueSuppressNewLicenseReminders);
        Assert.False(settings.FccIssueSuppressUpgradeReminders);
        Assert.True(settings.FccIssueSuppressRenewalReminders);
    }
}
