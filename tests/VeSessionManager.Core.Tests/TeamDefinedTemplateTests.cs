using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Email;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Templates a team writes for itself (#144, second half).
///
/// <para><b>Why this is safe to add to a service that was deliberately edit-only.</b>
/// <c>EmailTemplateAdminService</c>'s own summary says the set of keys is fixed by what the sending
/// services look up — and that stays true. A team-defined template is never looked up by anything;
/// a person picks it on the Email candidates screen, so no code path can break by its absence. The
/// tests below are about keeping those two populations apart: a system key must not be deletable,
/// and a name somebody types must never be able to become one.</para>
/// </summary>
public class TeamDefinedTemplateTests
{
    private static readonly DateTime Now = new(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static EmailTemplateAdminService CreateService(AppDbContext dbContext) =>
        new(dbContext, new FixedTimeProvider(Now));

    private static async Task<(Team Team, User User)> SeedAsync(AppDbContext dbContext)
    {
        var team = new Team { Name = "TEAMA", CreatedUtc = Now };
        var user = new User { Name = "Team Admin", Role = UserRole.TeamAdmin };
        dbContext.Teams.Add(team);
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
        return (team, user);
    }

    private static async Task<EmailTemplate> SeedSystemTemplateAsync(AppDbContext dbContext, Team team)
    {
        var template = new EmailTemplate
        {
            TeamId = team.Id,
            Key = "FelonyDisclosureInstructions",
            Subject = "Subject",
            Body = "Body {{CandidateName}}"
        };
        dbContext.EmailTemplates.Add(template);
        await dbContext.SaveChangesAsync();
        return template;
    }

    [Fact]
    public async Task CreateAsync_StoresTheTypedNameAndMarksItUserDefined()
    {
        await using var dbContext = CreateContext();
        var (team, user) = await SeedAsync(dbContext);

        var result = await CreateService(dbContext).CreateAsync(
            team.Id, "Field Day invite", "Come to Field Day", "<p>Hi {{CandidateFirstName}}</p>", user.Id, CancellationToken.None);

        Assert.Equal(EmailTemplateActionResult.Success, result);
        var template = await dbContext.EmailTemplates.SingleAsync();
        Assert.True(template.IsUserDefined);
        Assert.Equal("Field Day invite", template.DisplayName);
        Assert.Equal("Come to Field Day", template.Subject);
    }

    [Fact]
    public async Task ACreatedKeyCanNeverCollideWithASystemKey()
    {
        // The one property that has to hold forever, including against a system key added years from
        // now: generated keys carry a prefix containing a dot, and no shipped key has one.
        await using var dbContext = CreateContext();
        var (team, user) = await SeedAsync(dbContext);

        await CreateService(dbContext).CreateAsync(
            team.Id, "Registration confirmation", "S", "<p>B</p>", user.Id, CancellationToken.None);

        var template = await dbContext.EmailTemplates.SingleAsync();
        Assert.Contains('.', template.Key);
        Assert.DoesNotContain(template.Key, EmailTemplateTriggers.ByKey.Keys);
        Assert.All(EmailTemplateTriggers.ByKey.Keys, key => Assert.DoesNotContain('.', key));
    }

    [Fact]
    public async Task TwoTemplatesWithTheSameNameOnOneTeam_GetDistinctKeys()
    {
        // (TeamId, Key) is unique, so a second "Field Day" must not throw an index violation at the
        // admin — and must not silently overwrite the first either.
        await using var dbContext = CreateContext();
        var (team, user) = await SeedAsync(dbContext);
        var service = CreateService(dbContext);

        await service.CreateAsync(team.Id, "Field Day", "S", "<p>B</p>", user.Id, CancellationToken.None);
        var second = await service.CreateAsync(team.Id, "Field Day", "S2", "<p>B2</p>", user.Id, CancellationToken.None);

        Assert.Equal(EmailTemplateActionResult.Success, second);
        var keys = await dbContext.EmailTemplates.Select(t => t.Key).ToListAsync();
        Assert.Equal(2, keys.Distinct().Count());
    }

    [Fact]
    public async Task TheSameNameOnTwoTeams_IsFine()
    {
        await using var dbContext = CreateContext();
        var (teamA, user) = await SeedAsync(dbContext);
        var teamB = new Team { Name = "TEAMB", CreatedUtc = Now };
        dbContext.Teams.Add(teamB);
        await dbContext.SaveChangesAsync();
        var service = CreateService(dbContext);

        await service.CreateAsync(teamA.Id, "Field Day", "S", "<p>B</p>", user.Id, CancellationToken.None);
        var result = await service.CreateAsync(teamB.Id, "Field Day", "S", "<p>B</p>", user.Id, CancellationToken.None);

        Assert.Equal(EmailTemplateActionResult.Success, result);
        Assert.Equal(2, await dbContext.EmailTemplates.CountAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateAsync_WithoutAName_IsRefused(string name)
    {
        await using var dbContext = CreateContext();
        var (team, user) = await SeedAsync(dbContext);

        var result = await CreateService(dbContext).CreateAsync(
            team.Id, name, "S", "<p>B</p>", user.Id, CancellationToken.None);

        Assert.Equal(EmailTemplateActionResult.NameRequired, result);
        Assert.Empty(await dbContext.EmailTemplates.ToListAsync());
    }

    [Fact]
    public async Task CreateAsync_WithBlankContent_IsRefused()
    {
        await using var dbContext = CreateContext();
        var (team, user) = await SeedAsync(dbContext);

        var result = await CreateService(dbContext).CreateAsync(
            team.Id, "Field Day", "", "<p>B</p>", user.Id, CancellationToken.None);

        Assert.Equal(EmailTemplateActionResult.ContentRequired, result);
        Assert.Empty(await dbContext.EmailTemplates.ToListAsync());
    }

    [Fact]
    public async Task ANameOfNothingButPunctuation_StillProducesAUsableKey()
    {
        // The slug is derived from the name, and a name can be anything somebody types. A key that
        // came out empty would collide with the next such name on the same team.
        await using var dbContext = CreateContext();
        var (team, user) = await SeedAsync(dbContext);
        var service = CreateService(dbContext);

        await service.CreateAsync(team.Id, "!!! ???", "S", "<p>B</p>", user.Id, CancellationToken.None);
        var result = await service.CreateAsync(team.Id, "@@@ ###", "S", "<p>B</p>", user.Id, CancellationToken.None);

        Assert.Equal(EmailTemplateActionResult.Success, result);
        var keys = await dbContext.EmailTemplates.Select(t => t.Key).ToListAsync();
        Assert.Equal(2, keys.Distinct().Count());
        Assert.All(keys, k => Assert.NotEqual("Custom.", k));
    }

    [Fact]
    public async Task RenameAsync_ChangesTheDisplayNameButNotTheKey()
    {
        // The key is what the history rows and any open compose screen refer to. Renaming is a label
        // change, not a new template.
        await using var dbContext = CreateContext();
        var (team, user) = await SeedAsync(dbContext);
        var service = CreateService(dbContext);
        await service.CreateAsync(team.Id, "Field Day", "S", "<p>B</p>", user.Id, CancellationToken.None);
        var created = await dbContext.EmailTemplates.SingleAsync();
        var originalKey = created.Key;

        var result = await service.RenameAsync(created.Id, "Field Day 2027", user.Id, CancellationToken.None);

        Assert.Equal(EmailTemplateActionResult.Success, result);
        var reloaded = await dbContext.EmailTemplates.SingleAsync();
        Assert.Equal("Field Day 2027", reloaded.DisplayName);
        Assert.Equal(originalKey, reloaded.Key);
    }

    [Fact]
    public async Task RenamingASystemTemplate_IsRefused()
    {
        await using var dbContext = CreateContext();
        var (team, user) = await SeedAsync(dbContext);
        var system = await SeedSystemTemplateAsync(dbContext, team);

        var result = await CreateService(dbContext).RenameAsync(system.Id, "Something else", user.Id, CancellationToken.None);

        Assert.Equal(EmailTemplateActionResult.NotUserDefined, result);
        Assert.Null((await dbContext.EmailTemplates.SingleAsync()).DisplayName);
    }

    [Fact]
    public async Task DeletingASystemTemplate_IsRefused()
    {
        // The whole reason this service was edit-only: something in the code sends this key, and a
        // team that deleted it would get silent send failures with a log line nobody reads.
        await using var dbContext = CreateContext();
        var (team, user) = await SeedAsync(dbContext);
        var system = await SeedSystemTemplateAsync(dbContext, team);

        var result = await CreateService(dbContext).DeleteAsync(system.Id, user.Id, CancellationToken.None);

        Assert.Equal(EmailTemplateActionResult.NotUserDefined, result);
        Assert.Single(await dbContext.EmailTemplates.ToListAsync());
    }

    [Fact]
    public async Task DeletingAUserDefinedTemplate_RemovesItAndAudits()
    {
        await using var dbContext = CreateContext();
        var (team, user) = await SeedAsync(dbContext);
        var service = CreateService(dbContext);
        await service.CreateAsync(team.Id, "Field Day", "S", "<p>B</p>", user.Id, CancellationToken.None);
        var created = await dbContext.EmailTemplates.SingleAsync();

        var result = await service.DeleteAsync(created.Id, user.Id, CancellationToken.None);

        Assert.Equal(EmailTemplateActionResult.Success, result);
        Assert.Empty(await dbContext.EmailTemplates.ToListAsync());
        Assert.Contains(await dbContext.AuditLogs.ToListAsync(), a => a.Action == "EmailTemplateDeleted");
    }

    [Fact]
    public async Task DeletingATemplate_LeavesTheHistoryOfWhatItSent()
    {
        // CandidateEmailSend stores a label, not a foreign key, precisely so this holds: the record
        // that somebody was told something must outlive the template it was written from.
        await using var dbContext = CreateContext();
        var (team, user) = await SeedAsync(dbContext);
        var service = CreateService(dbContext);
        await service.CreateAsync(team.Id, "Field Day", "S", "<p>B</p>", user.Id, CancellationToken.None);
        var created = await dbContext.EmailTemplates.SingleAsync();

        var vec = new Vec { Name = "ARRL" };
        dbContext.Vecs.Add(vec);
        await dbContext.SaveChangesAsync();
        var feeConfiguration = new FeeConfiguration
        {
            VecId = vec.Id,
            EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            FeeCollectionEnabled = false,
            CreatedByUserId = user.Id,
            CreatedUtc = Now
        };
        dbContext.FeeConfigurations.Add(feeConfiguration);
        await dbContext.SaveChangesAsync();
        var session = new Session
        {
            ExamToolsSessionId = "s1",
            Title = "Session",
            ScheduledStartUtc = Now.AddDays(-1),
            TeamId = team.Id,
            VecId = vec.Id,
            FeeConfigurationId = feeConfiguration.Id,
            Status = SessionStatus.Active,
            CreatedUtc = Now
        };
        dbContext.Sessions.Add(session);
        await dbContext.SaveChangesAsync();
        var candidate = new Candidate { SessionId = session.Id, Name = "Ana", DateRegisteredUtc = Now };
        dbContext.Candidates.Add(candidate);
        await dbContext.SaveChangesAsync();
        dbContext.CandidateEmailSends.Add(new CandidateEmailSend
        {
            CandidateId = candidate.Id,
            TemplateLabel = "Field Day",
            SentUtc = Now,
            SentByUserId = user.Id
        });
        await dbContext.SaveChangesAsync();

        await service.DeleteAsync(created.Id, user.Id, CancellationToken.None);

        var send = Assert.Single(await dbContext.CandidateEmailSends.ToListAsync());
        Assert.Equal("Field Day", send.TemplateLabel);
    }

    [Fact]
    public async Task AUserDefinedTemplate_OffersTheFullCandidateTokenSet()
    {
        // Nothing in the code sends it, so there is no send-site dictionary to read a token list off.
        // The compose screen resolves CandidatePlaceholderValues for every draft, so that is the
        // honest answer for a template somebody invented.
        Assert.Equal(
            [.. CandidatePlaceholderValues.Names],
            EmailTemplatePlaceholders.ForUserDefined());
    }
}
