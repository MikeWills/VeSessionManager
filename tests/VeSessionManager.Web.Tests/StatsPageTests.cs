using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Web.Tests;

/// <summary>
/// The stats page (#63) is admin-only, and the nav agrees with the page.
///
/// <para><b>Both halves matter, and only one of them is the authorization.</b> This codebase states
/// the rule repeatedly — "never render a link the user's role will 403 on", and "keep the nav gate in
/// step with the attribute" — because the two live in different files and drift silently. A link that
/// 403s is a worse experience than no link; an attribute that is missing is a hole. So this asserts
/// the page refuses AND that the link is absent for the same roles.</para>
/// </summary>
public class StatsPageTests : IClassFixture<WebAppFactory>
{
    private const string Url = "/SessionManager/Stats";

    private readonly WebAppFactory _factory;

    public StatsPageTests(WebAppFactory factory) => _factory = factory;

    [Theory]
    [InlineData(UserRole.SystemAdmin)]
    [InlineData(UserRole.TeamAdmin)]
    public async Task AnAdminCanOpenIt(UserRole role)
    {
        using var client = _factory.CreateClientAs(role);
        var response = await client.GetAsync(Url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// The page shows a per-VE session count, which is why it carries the same restriction as VE
    /// Session Counts: a visible count-per-person invites comparison between volunteers that nobody
    /// asked for (the 2026-08-01 decision on VeRoster).
    /// </summary>
    [Theory]
    [InlineData(UserRole.SessionManager)]
    [InlineData(UserRole.TeamLead)]
    public async Task ANonAdminIsRefused(UserRole role)
    {
        using var client = _factory.CreateClientAs(role);
        var response = await client.GetAsync(Url);

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// Sets the seeded account's stored role, and restores it afterwards.
    ///
    /// <para><b>Necessary because the harness's role header only produces a claim.</b>
    /// <c>CreateClientAs</c> issues a <c>ClaimTypes.Role</c>, which is what <c>[Authorize]</c> reads —
    /// but the nav gate reads <c>currentUser.Role</c> from the database, so every client would
    /// otherwise render the nav as whatever the seeded row says (SystemAdmin) no matter which role
    /// was asked for. Testing the link and the attribute therefore needs two different mechanisms,
    /// which is worth knowing before writing another nav-gating test.</para>
    /// </summary>
    private async Task<UserRole> SetStoredRoleAsync(UserRole role)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.FirstAsync();
        var previous = user.Role;
        user.Role = role;
        await db.SaveChangesAsync();
        return previous;
    }

    /// <summary>The other half: no link for a role that cannot open it.</summary>
    [Theory]
    [InlineData(UserRole.SessionManager)]
    [InlineData(UserRole.TeamLead)]
    public async Task ANonAdminIsNotOfferedTheLink(UserRole role)
    {
        var previous = await SetStoredRoleAsync(role);
        try
        {
            using var client = _factory.CreateClientAs(role);
            var html = await client.GetStringAsync("/SessionManager/Index?applied=true");

            Assert.DoesNotContain(Url, html);
        }
        finally
        {
            await SetStoredRoleAsync(previous);
        }
    }

    [Fact]
    public async Task AnAdminIsOfferedTheLink()
    {
        var previous = await SetStoredRoleAsync(UserRole.SystemAdmin);
        try
        {
            using var client = _factory.CreateClientAs(UserRole.SystemAdmin);
            var html = await client.GetStringAsync("/SessionManager/Index?applied=true");

            Assert.Contains(Url, html);
        }
        finally
        {
            await SetStoredRoleAsync(previous);
        }
    }

    /// <summary>
    /// The charts read their series from a data- attribute, because the CSP is script-src 'self' and
    /// an inline script block renders but never runs. Asserted because that failure is invisible: the
    /// page looks complete and the canvases stay blank.
    /// </summary>
    [Fact]
    public async Task TheChartDataIsRenderedAsAnAttribute_NotAnInlineScript()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var template = await db.Sessions.AsNoTracking().FirstAsync();
            db.Sessions.Add(new Session
            {
                TeamId = template.TeamId,
                VecId = template.VecId,
                FeeConfigurationId = template.FeeConfigurationId,
                ExamToolsSessionId = $"stats-{Guid.NewGuid():N}",
                Title = "Stats fixture",
                ScheduledStartUtc = new DateTime(2026, 5, 10, 17, 0, 0, DateTimeKind.Utc),
                Status = SessionStatus.Active,
                // Completed, which is the only thing that makes a session count here.
                ExamToolsClosedUtc = new DateTime(2026, 5, 10, 20, 0, 0, DateTimeKind.Utc)
            });
            await db.SaveChangesAsync();
        }

        using var client = _factory.CreateClientAs(UserRole.SystemAdmin);
        var html = await client.GetStringAsync(Url);

        Assert.Contains("data-stats=", html);

        // Matched on a fragment, not the literal path: MapStaticAssets makes asp-append-version emit
        // a FINGERPRINTED filename (chart.umd.<hash>.min.js) rather than a ?v= query, so asserting
        // the full name finds nothing — the trap CLAUDE.md records, hit here on the first attempt.
        Assert.Contains("chart.umd", html);
        Assert.Contains("/lib/chart.js/", html);
    }
}
