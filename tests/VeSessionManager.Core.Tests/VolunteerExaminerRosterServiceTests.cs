using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.VolunteerExaminers;
using Xunit;

namespace VeSessionManager.Core.Tests;

public class VolunteerExaminerRosterServiceTests
{
    private static readonly DateTime Now = new(2026, 7, 22, 12, 0, 0, DateTimeKind.Utc);

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

    private static VolunteerExaminerRosterService CreateService(AppDbContext dbContext) =>
        new(dbContext, new FixedTimeProvider(Now), NullLogger<VolunteerExaminerRosterService>.Instance);

    private static async Task<(Team Team, User User, Session Session)> SeedSessionAsync(AppDbContext dbContext)
    {
        var team = new Team { Name = "TESTTEAM", CreatedUtc = Now };
        var user = new User { Name = "Session Manager", Email = "sm@example.com", Role = UserRole.SessionManager };
        var vec = new Vec { Name = "ARRL" };
        var feeConfiguration = new FeeConfiguration
        {
            Vec = vec, EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            FeeCollectionEnabled = true, ExamFeeAmount = 15m, CreatedByUser = user, CreatedUtc = Now
        };
        var session = new Session
        {
            ExamToolsSessionId = "session-1", Title = "Test Session", ScheduledStartUtc = Now,
            Team = team, Vec = vec, FeeConfiguration = feeConfiguration, CreatedUtc = Now
        };
        dbContext.Sessions.Add(session);
        await dbContext.SaveChangesAsync();
        return (team, user, session);
    }

    [Fact]
    public async Task Add_NewCallSign_CreatesVeAndLinksToSession()
    {
        await using var dbContext = CreateContext();
        var (_, user, session) = await SeedSessionAsync(dbContext);

        var result = await CreateService(dbContext).AddAsync(session.Id, "w0abc", "R. Halvorsen", user.Id, CancellationToken.None);

        Assert.Equal(VeRosterActionResult.Success, result);
        var ve = Assert.Single(dbContext.VolunteerExaminers);
        Assert.Equal("W0ABC", ve.CallSign); // stored upper-invariant
        Assert.Equal("R. Halvorsen", ve.Name);
        Assert.Single(dbContext.SessionVolunteerExaminers, l => l.SessionId == session.Id && l.VolunteerExaminerId == ve.Id);
    }

    [Fact]
    public async Task Add_ExistingCallSignForTeam_ReusesVe_DoesNotDuplicate()
    {
        await using var dbContext = CreateContext();
        var (team, user, session) = await SeedSessionAsync(dbContext);
        var existingVe = new VolunteerExaminer { Name = "R. Halvorsen", CallSign = "W0ABC", TeamId = team.Id };
        dbContext.VolunteerExaminers.Add(existingVe);
        await dbContext.SaveChangesAsync();

        var result = await CreateService(dbContext).AddAsync(session.Id, "w0abc", null, user.Id, CancellationToken.None);

        Assert.Equal(VeRosterActionResult.Success, result);
        Assert.Single(dbContext.VolunteerExaminers); // no duplicate VE row
        var link = Assert.Single(dbContext.SessionVolunteerExaminers);
        Assert.Equal(existingVe.Id, link.VolunteerExaminerId);
    }

    [Fact]
    public async Task Add_AlreadyOnRoster_ReturnsAlreadyOnRoster_NoDuplicateLink()
    {
        await using var dbContext = CreateContext();
        var (_, user, session) = await SeedSessionAsync(dbContext);
        await CreateService(dbContext).AddAsync(session.Id, "W0ABC", "R. Halvorsen", user.Id, CancellationToken.None);

        var result = await CreateService(dbContext).AddAsync(session.Id, "W0ABC", "R. Halvorsen", user.Id, CancellationToken.None);

        Assert.Equal(VeRosterActionResult.AlreadyOnRoster, result);
        Assert.Single(dbContext.SessionVolunteerExaminers);
    }

    [Fact]
    public async Task Remove_WhenOnRoster_RemovesLinkAndAudits()
    {
        await using var dbContext = CreateContext();
        var (_, user, session) = await SeedSessionAsync(dbContext);
        await CreateService(dbContext).AddAsync(session.Id, "W0ABC", "R. Halvorsen", user.Id, CancellationToken.None);
        var ve = dbContext.VolunteerExaminers.Single();

        var result = await CreateService(dbContext).RemoveAsync(session.Id, ve.Id, user.Id, CancellationToken.None);

        Assert.Equal(VeRosterActionResult.Success, result);
        Assert.Empty(dbContext.SessionVolunteerExaminers);
        Assert.Single(dbContext.VolunteerExaminers); // VE row itself stays, just unlinked from this session
        Assert.Single(dbContext.AuditLogs, a => a.Action == "VeRemovedFromSessionRoster");
    }

    [Fact]
    public async Task Remove_NotOnRoster_ReturnsNotOnRoster()
    {
        await using var dbContext = CreateContext();
        var (team, user, session) = await SeedSessionAsync(dbContext);
        var ve = new VolunteerExaminer { Name = "R. Halvorsen", CallSign = "W0ABC", TeamId = team.Id };
        dbContext.VolunteerExaminers.Add(ve);
        await dbContext.SaveChangesAsync();

        var result = await CreateService(dbContext).RemoveAsync(session.Id, ve.Id, user.Id, CancellationToken.None);

        Assert.Equal(VeRosterActionResult.NotOnRoster, result);
    }
}
