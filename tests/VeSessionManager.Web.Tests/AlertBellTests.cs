using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Navigation;
using Xunit;

namespace VeSessionManager.Web.Tests;

/// <summary>
/// The nav's alert bell (#339), rendered rather than asserted on in the abstract — the feed itself
/// is covered by AlertFeedServiceTests, and what these add is that the markup reaches the page and
/// carries the row it points at.
///
/// <para>Each test builds its own <see cref="WebAppFactory"/> because <see cref="AlertFeedCache"/> is
/// a singleton with a 30-second TTL: a shared factory would let one test's empty feed be served to
/// the next test's seeded one, and the failure would look like a rendering bug.</para>
/// </summary>
public class AlertBellTests
{
    private static async Task<int> SeedFindingAsync(WebAppFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var finding = new ReconciliationFinding
        {
            TeamId = factory.Seeded.TeamId,
            Kind = ReconciliationFindingKind.MissingSession,
            ExamToolsSessionId = "et-missing-session",
            SessionDateUtc = new DateTime(2026, 7, 31, 23, 0, 0, DateTimeKind.Utc),
            Detail = "ExamTools has a closed session this app never ingested.",
            FirstSeenUtc = new DateTime(2026, 8, 15, 6, 0, 0, DateTimeKind.Utc),
            LastSeenUtc = new DateTime(2026, 8, 16, 6, 0, 0, DateTimeKind.Utc)
        };
        db.ReconciliationFindings.Add(finding);
        await db.SaveChangesAsync();
        return finding.Id;
    }

    [Fact]
    public async Task WithNothingOutstanding_TheBellRendersWithoutABadge()
    {
        // The bell is a fixture of the chassis, not something that appears when there is bad news —
        // a control that comes and goes is one nobody learns to look at.
        using var factory = new WebAppFactory();
        var client = factory.CreateClientAs(UserRole.SystemAdmin);

        var html = await client.GetStringAsync("/SessionManager/Index");

        Assert.Contains("alert-menu", html);
        Assert.Contains("Nothing needs your attention.", html);
        Assert.DoesNotContain("alert-item", html);
    }

    [Fact]
    public async Task AnOpenFinding_BecomesAnAlertLinkingAtThatRow()
    {
        using var factory = new WebAppFactory();
        var findingId = await SeedFindingAsync(factory);
        var client = factory.CreateClientAs(UserRole.SystemAdmin);

        var html = await client.GetStringAsync("/SessionManager/Index");

        Assert.Contains("Session missing from this app", html);
        // The point of the issue: the link carries the finding, so the reader lands on the row and
        // not on a list they then have to search.
        Assert.Contains($"/Admin/Reconciliation?highlight={findingId}", html);
    }

    [Fact]
    public async Task FollowingAnAlert_MarksTheRowItPointsAt()
    {
        using var factory = new WebAppFactory();
        var findingId = await SeedFindingAsync(factory);
        var client = factory.CreateClientAs(UserRole.SystemAdmin);

        var html = await client.GetStringAsync($"/Admin/Reconciliation?highlight={findingId}");

        Assert.Contains($"id=\"finding-{findingId}\"", html);
        Assert.Contains("row-highlight", html);
    }

    [Fact]
    public async Task HighlightIsPresentationOnly_TheOtherFindingsAreStillListed()
    {
        // A highlight that filtered would answer a narrower question than the one asked, and would
        // hide the very context that makes a finding readable ("is this the only one?").
        using var factory = new WebAppFactory();
        var first = await SeedFindingAsync(factory);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ReconciliationFindings.Add(new ReconciliationFinding
            {
                TeamId = factory.Seeded.TeamId,
                Kind = ReconciliationFindingKind.CandidateCountMismatch,
                ExamToolsSessionId = "et-second-finding",
                SessionDateUtc = new DateTime(2026, 7, 20, 23, 0, 0, DateTimeKind.Utc),
                Detail = "ExamTools reports 12 applicants, this app has 9.",
                FirstSeenUtc = new DateTime(2026, 8, 14, 6, 0, 0, DateTimeKind.Utc),
                LastSeenUtc = new DateTime(2026, 8, 16, 6, 0, 0, DateTimeKind.Utc)
            });
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClientAs(UserRole.SystemAdmin);
        var html = await client.GetStringAsync($"/Admin/Reconciliation?highlight={first}");

        Assert.Contains("ExamTools reports 12 applicants, this app has 9.", html);
        Assert.Contains($"id=\"finding-{first}\"", html);
    }

    [Fact]
    public async Task AnUnknownHighlightId_RendersThePageNormally()
    {
        // What a stale bookmark or an already-resolved finding produces. Nothing is looked up by the
        // id, so the only correct behaviour is to ignore it.
        using var factory = new WebAppFactory();
        await SeedFindingAsync(factory);
        var client = factory.CreateClientAs(UserRole.SystemAdmin);

        var response = await client.GetAsync("/Admin/Reconciliation?highlight=999999");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("row-highlight", await response.Content.ReadAsStringAsync());
    }
}

/// <summary>
/// Every page an alert links to must admit the role the alert was offered to.
///
/// <para><b>Why this cannot be a comment.</b> <see cref="AlertFeedService"/> lives in Core and gates
/// reconciliation alerts to SystemAdmin/TeamAdmin by mirroring
/// <c>[Authorize(Roles = RoleGroups.Admins)]</c> on the page — <c>RoleGroups</c> is a Web type Core
/// cannot reference. Two copies of one rule agree right up until somebody edits one, which is the
/// same shape <c>ActionMessageSingleSourceTests</c> and <c>ReportsNavGateTests</c> exist for. Tighten
/// the page's roles without tightening the feed and the bell starts handing out links to a 403.</para>
/// </summary>
public class AlertPageRoleGateTests
{
    public static TheoryData<UserRole> EveryRole() =>
        [UserRole.SystemAdmin, UserRole.TeamAdmin, UserRole.SessionManager, UserRole.TeamLead];

    [Theory]
    [MemberData(nameof(EveryRole))]
    public async Task EveryAlertOfferedToARole_LinksToAPageThatRoleCanOpen(UserRole role)
    {
        using var factory = new WebAppFactory();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.ReconciliationFindings.Add(new ReconciliationFinding
        {
            TeamId = factory.Seeded.TeamId,
            Kind = ReconciliationFindingKind.MissingSession,
            ExamToolsSessionId = "et-role-gate",
            SessionDateUtc = new DateTime(2026, 7, 31, 23, 0, 0, DateTimeKind.Utc),
            Detail = "ExamTools has a closed session this app never ingested.",
            FirstSeenUtc = new DateTime(2026, 8, 15, 6, 0, 0, DateTimeKind.Utc),
            LastSeenUtc = new DateTime(2026, 8, 16, 6, 0, 0, DateTimeKind.Utc)
        });
        await db.SaveChangesAsync();

        var feed = await scope.ServiceProvider.GetRequiredService<AlertFeedService>()
            .GetAsync(role, [factory.Seeded.TeamId], CancellationToken.None);

        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints;

        foreach (var item in feed.Items)
        {
            var endpoint = endpoints.OfType<RouteEndpoint>().FirstOrDefault(e =>
                string.Equals("/" + (e.RoutePattern.RawText ?? "").TrimStart('/'), item.PageName, StringComparison.OrdinalIgnoreCase));
            Assert.True(endpoint is not null, $"An alert links to {item.PageName}, which is not a routable page.");

            // Asserted on the endpoint's authorization metadata rather than by probing a status
            // code, for the reason recorded on ReportsNavGateTests: a 403 cannot say why.
            var roles = endpoint!.Metadata.GetOrderedMetadata<IAuthorizeData>()
                .Select(a => a.Roles)
                .FirstOrDefault(r => !string.IsNullOrWhiteSpace(r));

            if (roles is null)
            {
                continue; // No role gate at all — every signed-in role may open it.
            }

            var allowed = roles.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            Assert.True(
                allowed.Contains(role.ToString()),
                $"{role} is offered an alert linking to {item.PageName}, which only admits: {roles}.");
        }
    }
}
