using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Messaging;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// The lookup that keeps everything outside the rule engine agreeing with it (#401, PR2) — the
/// payment-expiry write and the Applicant Status page's "days pending" colours.
///
/// <para>The distinction these tests exist for is the two methods' different answer to "this team has
/// no rule": bookkeeping needs a number regardless, and a page must show no boundary at all.</para>
/// </summary>
public class MessageThresholdServiceTests
{
    private static readonly DateTime Now = new(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc);

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<Team> SeedTeamAsync(AppDbContext dbContext, string name = "TESTTEAM")
    {
        var team = new Team { Name = name, CreatedUtc = Now };
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        return team;
    }

    private static async Task AddRuleAsync(
        AppDbContext dbContext, Team team, MessageTrigger trigger, int? hours, bool enabled = true)
    {
        var rule = MessageRuleTestHarness.NewRule(team, trigger, "PaymentExpirationNotice", hours, Now);
        rule.IsEnabled = enabled;
        dbContext.MessageRules.Add(rule);
        await dbContext.SaveChangesAsync();
    }

    [Fact]
    public async Task HoursOrDefault_WithNoRule_FallsBackToTheTriggersOwnDefault()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);

        var hours = await new MessageThresholdService(dbContext)
            .HoursOrDefaultAsync(team.Id, MessageTrigger.FccFeeOutstanding, CancellationToken.None);

        Assert.Equal(MessageTriggerDefinitions.For(MessageTrigger.FccFeeOutstanding).DefaultParameterHours, hours);
    }

    /// <summary>
    /// A disabled rule falls back too, and that is the point rather than an oversight: a switched-off
    /// rule reports no boundary of its own, so anything still reading this trigger's threshold — the
    /// Applicant Status colours, for whichever trigger still uses them — gets the trigger's plain
    /// default rather than treating "disabled" as "zero hours".
    /// </summary>
    [Fact]
    public async Task HoursOrDefault_WithADisabledRule_StillFallsBack()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await AddRuleAsync(dbContext, team, MessageTrigger.FccFeeOutstanding, 720, enabled: false);

        var hours = await new MessageThresholdService(dbContext)
            .HoursOrDefaultAsync(team.Id, MessageTrigger.FccFeeOutstanding, CancellationToken.None);

        Assert.Equal(120, hours);
    }

    [Fact]
    public async Task HoursOrDefault_ReadsTheTeamsOwnNumber()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await AddRuleAsync(dbContext, team, MessageTrigger.FccFeeOutstanding, 720);

        Assert.Equal(720, await new MessageThresholdService(dbContext)
            .HoursOrDefaultAsync(team.Id, MessageTrigger.FccFeeOutstanding, CancellationToken.None));
    }

    /// <summary>Several rules on one trigger: the boundary anyone cares about is the first one that fires.</summary>
    [Fact]
    public async Task WithSeveralRules_TheEarliestWins()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await AddRuleAsync(dbContext, team, MessageTrigger.FccFeeOutstanding, 240);
        await AddRuleAsync(dbContext, team, MessageTrigger.FccFeeOutstanding, 72);

        Assert.Equal(72, await new MessageThresholdService(dbContext)
            .ConfiguredHoursAsync(team.Id, MessageTrigger.FccFeeOutstanding, CancellationToken.None));
    }

    /// <summary>
    /// The difference that matters: with nothing configured, the page must be told "no boundary"
    /// rather than handed a default it would then colour a row on. Nothing is going to happen on any
    /// particular day, so warning about one would be inventing it.
    /// </summary>
    [Fact]
    public async Task ConfiguredHours_WithNoEnabledRule_IsNull_NotTheDefault()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await AddRuleAsync(dbContext, team, MessageTrigger.FccFeeOutstanding, 120, enabled: false);

        Assert.Null(await new MessageThresholdService(dbContext)
            .ConfiguredHoursAsync(team.Id, MessageTrigger.FccFeeOutstanding, CancellationToken.None));
    }

    /// <summary>Per team, because the page that reads this merges several — one team's setting must not colour another's rows.</summary>
    [Fact]
    public async Task ConfiguredHoursByTeam_AnswersEachTeamSeparately_AndOmitsTheOnesWithNoRule()
    {
        await using var dbContext = CreateContext();
        var teamA = await SeedTeamAsync(dbContext, "TEAMA");
        var teamB = await SeedTeamAsync(dbContext, "TEAMB");
        var teamC = await SeedTeamAsync(dbContext, "TEAMC");
        await AddRuleAsync(dbContext, teamA, MessageTrigger.FccFeeOutstanding, 120);
        await AddRuleAsync(dbContext, teamB, MessageTrigger.FccFeeOutstanding, 48);

        var byTeam = await new MessageThresholdService(dbContext).ConfiguredHoursByTeamAsync(
            [teamA.Id, teamB.Id, teamC.Id], MessageTrigger.FccFeeOutstanding, CancellationToken.None);

        Assert.Equal(120, byTeam[teamA.Id]);
        Assert.Equal(48, byTeam[teamB.Id]);
        Assert.DoesNotContain(teamC.Id, byTeam.Keys);
    }

    /// <summary>A different trigger's rule is not this trigger's threshold, however similar the numbers look.</summary>
    [Fact]
    public async Task ARuleOnAnotherTrigger_IsNotConsulted()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await AddRuleAsync(dbContext, team, MessageTrigger.BeforeSessionStart, 24);

        Assert.Null(await new MessageThresholdService(dbContext)
            .ConfiguredHoursAsync(team.Id, MessageTrigger.FccFeeOutstanding, CancellationToken.None));
    }
}
