using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Web.Tests;

/// <summary>
/// Going back to a list you filtered should return you to that list.
///
/// <para>Mike, 2026-08-21: <i>"when I go back to message rules from a message rule, I have to pick the
/// team again."</i> A breadcrumb built from a page name alone drops the query string, so it lands on
/// the unfiltered first page and every filter has to be re-entered.</para>
///
/// <para>⚠️ <b>Half of this file is about the open redirect the fix invites.</b> The return URL
/// arrives in the query string, which anybody can write, and the link sits on a page the victim
/// reached by signing in — the classic phishing shape. <see cref="SafeReturnUrl"/> refuses anything
/// not local, and these tests are what stop that guard being quietly dropped later.</para>
/// </summary>
public class ReturnToFilteredListTests
{
    private static async Task<int> SeedRuleAsync(WebAppFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rule = new MessageRule
        {
            TeamId = factory.Seeded.TeamId,
            Name = "Day before",
            Trigger = MessageTrigger.BeforeSessionStart,
            ParameterHours = 24,
            Subject = "Subject",
            Body = "<p>Body</p>",
            CreatedUtc = DateTime.UtcNow.AddDays(-1)
        };
        db.MessageRules.Add(rule);
        await db.SaveChangesAsync();
        return rule.Id;
    }

    /// <summary>
    /// A session the default filter will actually show. The seeded fixture starts exactly seven days
    /// ago, right on the boundary of the list's "last 7 days plus upcoming" default, so the list comes
    /// back empty and a link test finds nothing to assert on.
    /// </summary>
    private static async Task<int> SeedUpcomingSessionAsync(WebAppFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var template = await db.Sessions.AsNoTracking().FirstAsync();

        var session = new Session
        {
            TeamId = template.TeamId,
            VecId = template.VecId,
            FeeConfigurationId = template.FeeConfigurationId,
            ExamToolsSessionId = $"return-{Guid.NewGuid():N}",
            Title = "Return fixture",
            ExtId = "RETURN-1",
            ScheduledStartUtc = DateTime.UtcNow.AddDays(3),
            Status = SessionStatus.Active
        };
        db.Sessions.Add(session);
        await db.SaveChangesAsync();
        return session.Id;
    }

    /// <summary>
    /// The crumb's href, which is what a reader actually clicks.
    ///
    /// <para>Two markups, because the two areas grew them separately: the admin pages put the class on
    /// the anchor (<c>&lt;a class="crumb"&gt;</c>, from the shared _ParentCrumb partial) and the session
    /// pages put it on a wrapping div. Both are matched rather than normalised, since unifying them is
    /// a separate change and this test should not be the thing that forces it.</para>
    /// </summary>
    private static string CrumbHref(string html)
    {
        var match = Regex.Match(html, "<a class=\"crumb\" href=\"([^\"]+)\"");
        if (!match.Success)
        {
            match = Regex.Match(html, "<div class=\"crumb\"><a href=\"([^\"]+)\"");
        }

        Assert.True(match.Success, "No breadcrumb link found on the page.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    [Fact]
    public async Task TheMessagesListHandsItsOwnUrlToTheEditLinks()
    {
        using var factory = new WebAppFactory();
        await SeedRuleAsync(factory);
        var client = factory.CreateClientAs(UserRole.SystemAdmin);

        var html = await client.GetStringAsync($"/Admin/MessageRules?teamId={factory.Seeded.TeamId}");

        Assert.Contains($"MessageRules%3FteamId%3D{factory.Seeded.TeamId}", html);
    }

    [Fact]
    public async Task EditingAMessage_ReturnsToTheTeamYouCameFrom()
    {
        using var factory = new WebAppFactory();
        var id = await SeedRuleAsync(factory);
        var listUrl = $"/Admin/MessageRules?teamId={factory.Seeded.TeamId}";
        var client = factory.CreateClientAs(UserRole.SystemAdmin);

        var html = await client.GetStringAsync($"/Admin/MessageRuleEdit/{id}?return={Uri.EscapeDataString(listUrl)}");

        Assert.Equal(listUrl, CrumbHref(html));
    }

    /// <summary>
    /// With no return URL the crumb still has to work — it just forgets the filters. That is the
    /// behaviour this replaced, and it is never wrong, only forgetful.
    /// </summary>
    [Fact]
    public async Task WithNoReturnUrl_TheCrumbStillPointsAtThatRulesTeam()
    {
        using var factory = new WebAppFactory();
        var id = await SeedRuleAsync(factory);
        var client = factory.CreateClientAs(UserRole.SystemAdmin);

        var html = await client.GetStringAsync($"/Admin/MessageRuleEdit/{id}");

        var href = CrumbHref(html);
        Assert.Contains("/Admin/MessageRules", href);
        Assert.Contains($"teamId={factory.Seeded.TeamId}", href);
    }

    [Fact]
    public async Task ASessionsListLinksIntoDetailCarryingItsFilters()
    {
        using var factory = new WebAppFactory();
        await SeedUpcomingSessionAsync(factory);
        var client = factory.CreateClientAs(UserRole.SystemAdmin);

        var html = await client.GetStringAsync("/SessionManager/Index");

        // The View link carries the list's own URL, whatever the filters were.
        Assert.Matches("class=\"view-link\"[^>]*return=", html);
    }

    [Fact]
    public async Task ASessionDetail_ReturnsToTheFilteredList()
    {
        using var factory = new WebAppFactory();
        var client = factory.CreateClientAs(UserRole.SystemAdmin);
        var listUrl = "/SessionManager/Index?pageSize=25&sort=date";

        var html = await client.GetStringAsync(
            $"/SessionManager/Detail/{factory.Seeded.SessionId}?return={Uri.EscapeDataString(listUrl)}");

        Assert.Equal(listUrl, CrumbHref(html));
    }

    /// <summary>
    /// ⚠️ The one that matters. An absolute URL in <c>?return=</c> must not become the destination of a
    /// link on an authenticated admin page — that is an open redirect, and a convincing one, because
    /// the victim got there by signing in to the real site.
    /// </summary>
    [Theory]
    [InlineData("https://evil.example/phish")]
    [InlineData("//evil.example/phish")]
    [InlineData("http://evil.example")]
    [InlineData("https://evil.example\\@localhost")]
    public async Task AnOffsiteReturnUrl_IsRefused(string hostile)
    {
        using var factory = new WebAppFactory();
        var id = await SeedRuleAsync(factory);
        var client = factory.CreateClientAs(UserRole.SystemAdmin);

        var html = await client.GetStringAsync($"/Admin/MessageRuleEdit/{id}?return={Uri.EscapeDataString(hostile)}");

        var href = CrumbHref(html);
        Assert.DoesNotContain("evil.example", href);
        Assert.StartsWith("/Admin/MessageRules", href);
    }

    /// <summary>Same guard on the sessions side, which takes its return URL from a different page.</summary>
    [Fact]
    public async Task AnOffsiteReturnUrl_IsRefusedOnSessionDetailToo()
    {
        using var factory = new WebAppFactory();
        var client = factory.CreateClientAs(UserRole.SystemAdmin);

        var html = await client.GetStringAsync(
            $"/SessionManager/Detail/{factory.Seeded.SessionId}?return={Uri.EscapeDataString("https://evil.example")}");

        Assert.DoesNotContain("evil.example", CrumbHref(html));
    }
}
