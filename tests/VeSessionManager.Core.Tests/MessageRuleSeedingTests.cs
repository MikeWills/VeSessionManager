using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Email;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// What a team starts with (#401): the four rules reproducing this app's original automatic sends,
/// seeded by <see cref="EmailDefaultsSeeder"/> the same way its templates are.
/// </summary>
public class MessageRuleSeedingTests
{
    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<Team> SeedTeamAsync(AppDbContext dbContext, string name = "TESTTEAM")
    {
        var team = new Team { Name = name, CreatedUtc = DateTime.UtcNow };
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        return team;
    }

    [Fact]
    public async Task ANewTeam_GetsTheFourRulesThatReproduceTodaysBehaviour()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);

        await EmailDefaultsSeeder.SeedForTeamAsync(dbContext, NullLogger.Instance, team);
        await dbContext.SaveChangesAsync();

        var rules = await dbContext.MessageRules.Where(r => r.TeamId == team.Id).ToListAsync();
        Assert.Equal(4, rules.Count);
        Assert.Equal(24, rules.Single(r => r.Trigger == MessageTrigger.BeforeSessionStart).ParameterHours);
        Assert.Equal(120, rules.Single(r => r.Trigger == MessageTrigger.FccFeeOutstanding).ParameterHours);
        Assert.Equal(240, rules.Single(r => r.Trigger == MessageTrigger.PaymentUnpaid).ParameterHours);
        Assert.Equal(MessageRecipient.TeamAdminAddress, rules.Single(r => r.Trigger == MessageTrigger.PaymentUnpaid).Recipient);

        // Each points at a template the same call just seeded — a rule naming a key nothing provides
        // is a rule that can only ever record Failed.
        var keys = await dbContext.EmailTemplates.Where(t => t.TeamId == team.Id).Select(t => t.Key).ToListAsync();
        Assert.All(rules, r => Assert.Contains(r.TemplateKey, keys));
    }

    /// <summary>
    /// <b>The riskiest line in the whole change.</b> Every scan is bounded by <c>CreatedUtc</c>, so
    /// seeding it at "now" is what stops a deployment mailing everybody already mid-cycle — somebody
    /// five days into an outstanding FCC fee, or with a session tomorrow. Seeded in the past, this
    /// change would have gone out as a mass send on its first tick.
    ///
    /// <para>The trade, confirmed with Mike: a candidate who registered this morning and has not had
    /// their confirmation yet never gets one. Accepted as the price of the direction that cannot
    /// mass-mail.</para>
    /// </summary>
    [Fact]
    public async Task SeededRules_AreCreatedAtSeedTime_SoNothingAlreadyPastItsMomentFires()
    {
        await using var dbContext = CreateContext();
        var before = DateTime.UtcNow;
        var team = await SeedTeamAsync(dbContext);

        await EmailDefaultsSeeder.SeedForTeamAsync(dbContext, NullLogger.Instance, team);
        await dbContext.SaveChangesAsync();
        var after = DateTime.UtcNow;

        Assert.All(await dbContext.MessageRules.ToListAsync(), r =>
        {
            Assert.InRange(r.CreatedUtc, before, after);
        });
    }

    /// <summary>
    /// Idempotent per (team, trigger), like the templates beside them — the seeder runs on every
    /// Worker start, so a team's edits have to survive it.
    /// </summary>
    [Fact]
    public async Task Reseeding_NeitherDuplicatesRulesNorOverwritesATeamsEdits()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await EmailDefaultsSeeder.SeedForTeamAsync(dbContext, NullLogger.Instance, team);
        await dbContext.SaveChangesAsync();

        var reminder = await dbContext.MessageRules.SingleAsync(r => r.Trigger == MessageTrigger.BeforeSessionStart);
        reminder.ParameterHours = 48;
        var disabled = await dbContext.MessageRules.SingleAsync(r => r.Trigger == MessageTrigger.PaymentUnpaid);
        disabled.IsEnabled = false;
        await dbContext.SaveChangesAsync();

        await EmailDefaultsSeeder.SeedForTeamAsync(dbContext, NullLogger.Instance, team);
        await dbContext.SaveChangesAsync();

        var rules = await dbContext.MessageRules.ToListAsync();
        Assert.Equal(4, rules.Count);
        Assert.Equal(48, rules.Single(r => r.Trigger == MessageTrigger.BeforeSessionStart).ParameterHours);
        Assert.False(rules.Single(r => r.Trigger == MessageTrigger.PaymentUnpaid).IsEnabled);
    }

    /// <summary>
    /// <b>A deleted rule comes back on the next Worker start</b>, because the guard asks whether the
    /// team has a rule for that trigger and a deleted one does not exist. Pinned rather than fixed:
    /// it is exactly how the seeded templates beside it behave, and PR1 ships no way to delete a rule,
    /// so nothing can hit it yet.
    ///
    /// <para>It does become reachable the moment the admin screen in PR2 offers a delete, and
    /// resurrecting a rule a team switched off by deleting it means quietly resuming a send they
    /// stopped. <c>IsEnabled</c> is the mechanism that survives reseeding (see the test above), so
    /// that screen should disable rather than delete — or this guard needs a tombstone.</para>
    /// </summary>
    [Fact]
    public async Task ADeletedRule_IsSeededAgain_WhichIsWhyTheAdminScreenShouldDisableRatherThanDelete()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await EmailDefaultsSeeder.SeedForTeamAsync(dbContext, NullLogger.Instance, team);
        await dbContext.SaveChangesAsync();

        dbContext.MessageRules.Remove(await dbContext.MessageRules.SingleAsync(r => r.Trigger == MessageTrigger.PaymentUnpaid));
        await dbContext.SaveChangesAsync();

        await EmailDefaultsSeeder.SeedForTeamAsync(dbContext, NullLogger.Instance, team);
        await dbContext.SaveChangesAsync();

        Assert.Contains(await dbContext.MessageRules.ToListAsync(), r => r.Trigger == MessageTrigger.PaymentUnpaid);
    }

    /// <summary>Rules are per team, like the credentials that send them.</summary>
    [Fact]
    public async Task EachTeamGetsItsOwnSet()
    {
        await using var dbContext = CreateContext();
        var teamA = await SeedTeamAsync(dbContext, "TEAMA");
        var teamB = await SeedTeamAsync(dbContext, "TEAMB");

        await EmailDefaultsSeeder.SeedAsync(dbContext, NullLogger.Instance);

        Assert.Equal(4, await dbContext.MessageRules.CountAsync(r => r.TeamId == teamA.Id));
        Assert.Equal(4, await dbContext.MessageRules.CountAsync(r => r.TeamId == teamB.Id));
    }
}
