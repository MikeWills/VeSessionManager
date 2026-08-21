using VeSessionManager.Core.Messaging;
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
    public async Task ANewTeam_GetsExampleMessages_AutomaticOnesSwitchedOff()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);

        await EmailDefaultsSeeder.SeedForTeamAsync(dbContext, NullLogger.Instance, team);
        await dbContext.SaveChangesAsync();

        var rules = await dbContext.MessageRules.Where(r => r.TeamId == team.Id).ToListAsync();

        // Four automatic, three hand-sent. The hand-sent ones were templates with no rule until the
        // content refactor; they are messages on their own manual triggers now.
        Assert.Equal(7, rules.Count);
        Assert.Equal(24, rules.Single(r => r.Trigger == MessageTrigger.BeforeSessionStart).ParameterHours);
        Assert.Equal(120, rules.Single(r => r.Trigger == MessageTrigger.FccFeeOutstanding).ParameterHours);
        Assert.Equal(240, rules.Single(r => r.Trigger == MessageTrigger.PaymentUnpaid).ParameterHours);
        Assert.Equal(MessageRecipient.TeamAdminAddress, rules.Single(r => r.Trigger == MessageTrigger.PaymentUnpaid).Recipient);

        // ⚠️ The AUTOMATIC ones arrive switched off (Mike, 2026-08-21: "keep them all turned off") —
        // examples of what a team can set up, not mail a new team starts sending to real people before
        // anybody has read it.
        var automatic = rules.Where(r => MessageTriggerDefinitions.For(r.Trigger).Mechanism != MessageTriggerMechanism.Manual).ToList();
        Assert.Equal(4, automatic.Count);
        Assert.All(automatic, r => Assert.False(r.IsEnabled));

        // The hand-sent ones arrive ON. The risk being avoided is unread mail going out by itself, and
        // a message nothing sends until somebody presses a button is not that — off would leave the
        // felony-disclosure and youth-program buttons silently doing nothing, which reads as broken.
        var manual = rules.Where(r => MessageTriggerDefinitions.For(r.Trigger).Mechanism == MessageTriggerMechanism.Manual).ToList();
        Assert.Equal(3, manual.Count);
        Assert.All(manual, r => Assert.True(r.IsEnabled));

        // And each carries its own words rather than a key pointing at a template — which is the
        // whole change. A message with no body could only ever send a blank email.
        Assert.All(rules, r => Assert.False(string.IsNullOrWhiteSpace(r.Subject)));
        Assert.All(rules, r => Assert.False(string.IsNullOrWhiteSpace(r.Body)));
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
        Assert.Equal(7, rules.Count);
        Assert.Equal(48, rules.Single(r => r.Trigger == MessageTrigger.BeforeSessionStart).ParameterHours);
        Assert.False(rules.Single(r => r.Trigger == MessageTrigger.PaymentUnpaid).IsEnabled);
    }

    /// <summary>
    /// <b>A deleted rule stays deleted.</b> This is the tombstone earning its keep: the seeder used to
    /// ask "does this team have a rule for this trigger?", which is the right question for a team
    /// being set up and the wrong one forever after — a rule somebody deleted came back on the next
    /// Worker start, quietly resuming a send they had stopped.
    ///
    /// <para>A team that wants nothing sent at a trigger point is entitled to have nothing there, and
    /// the seeder is not the authority on that after day one.</para>
    /// </summary>
    [Fact]
    public async Task ADeletedRule_IsNotSeededAgain()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await EmailDefaultsSeeder.SeedForTeamAsync(dbContext, NullLogger.Instance, team);
        await dbContext.SaveChangesAsync();

        dbContext.MessageRules.Remove(await dbContext.MessageRules.SingleAsync(r => r.Trigger == MessageTrigger.PaymentUnpaid));
        await dbContext.SaveChangesAsync();

        await EmailDefaultsSeeder.SeedForTeamAsync(dbContext, NullLogger.Instance, team);
        await dbContext.SaveChangesAsync();

        var rules = await dbContext.MessageRules.ToListAsync();
        Assert.DoesNotContain(rules, r => r.Trigger == MessageTrigger.PaymentUnpaid);

        // One fewer than a full seed: the deleted one stays deleted. Six rather than three since the
        // three hand-sent messages joined the set.
        Assert.Equal(6, rules.Count);
    }

    /// <summary>Deleting every one of them is an answer too — "we send nothing automatically" must survive a restart.</summary>
    [Fact]
    public async Task ATeamThatDeletesEveryRule_GetsNoneBack()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await EmailDefaultsSeeder.SeedForTeamAsync(dbContext, NullLogger.Instance, team);
        await dbContext.SaveChangesAsync();

        dbContext.MessageRules.RemoveRange(dbContext.MessageRules);
        await dbContext.SaveChangesAsync();

        await EmailDefaultsSeeder.SeedForTeamAsync(dbContext, NullLogger.Instance, team);
        await dbContext.SaveChangesAsync();

        Assert.Empty(dbContext.MessageRules);
    }

    /// <summary>
    /// The tombstone is stamped by the seeding run itself, and it is what every later run reads. A
    /// team left unstamped would be re-seeded on the next Worker start and end up with two of
    /// everything.
    /// </summary>
    [Fact]
    public async Task SeedingStampsTheTeam_AndASecondRunAddsNothing()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);

        await EmailDefaultsSeeder.SeedForTeamAsync(dbContext, NullLogger.Instance, team);
        await dbContext.SaveChangesAsync();
        Assert.NotNull(team.MessageRulesSeededUtc);

        await EmailDefaultsSeeder.SeedForTeamAsync(dbContext, NullLogger.Instance, team);
        await dbContext.SaveChangesAsync();

        Assert.Equal(7, await dbContext.MessageRules.CountAsync());
    }

    /// <summary>Rules are per team, like the credentials that send them.</summary>
    [Fact]
    public async Task EachTeamGetsItsOwnSet()
    {
        await using var dbContext = CreateContext();
        var teamA = await SeedTeamAsync(dbContext, "TEAMA");
        var teamB = await SeedTeamAsync(dbContext, "TEAMB");

        await EmailDefaultsSeeder.SeedAsync(dbContext, NullLogger.Instance);

        Assert.Equal(7, await dbContext.MessageRules.CountAsync(r => r.TeamId == teamA.Id));
        Assert.Equal(7, await dbContext.MessageRules.CountAsync(r => r.TeamId == teamB.Id));
    }
}
