using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.ArrlSubmissions;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Core.Tests;

public class ArrlSubmissionServiceTests
{
    private static readonly DateTime Now = new(2026, 7, 21, 12, 0, 0, DateTimeKind.Utc);

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

    private static async Task<(Team Team, Session Session, User User)> SeedSessionAsync(AppDbContext dbContext, ArrlSubmissionStatus status = ArrlSubmissionStatus.NotSubmitted)
    {
        var team = new Team { Name = "TESTTEAM", CreatedUtc = Now };
        var vec = new Vec { Name = "ARRL" };
        var user = new User { Name = "Session Manager", Email = "sm@example.com", Role = UserRole.SessionManager };
        var feeConfiguration = new FeeConfiguration
        {
            Vec = vec,
            EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            FeeCollectionEnabled = true,
            ExamFeeAmount = 15m,
            CreatedByUser = user,
            CreatedUtc = Now
        };
        var session = new Session
        {
            ExamToolsSessionId = "session-1",
            Title = "Test Session",
            ScheduledStartUtc = new DateTime(2026, 6, 1, 17, 0, 0, DateTimeKind.Utc),
            Team = team,
            Vec = vec,
            FeeConfiguration = feeConfiguration,
            ArrlSubmissionStatus = status,
            CreatedUtc = Now
        };
        dbContext.Sessions.Add(session);
        await dbContext.SaveChangesAsync();
        return (team, session, user);
    }

    private static ArrlSubmissionService CreateService(AppDbContext dbContext) =>
        new(dbContext, new FixedTimeProvider(Now));

    [Fact]
    public async Task MarkSubmitted_OnNotSubmittedSession_SetsStatusDateAndUser()
    {
        await using var dbContext = CreateContext();
        var (_, session, user) = await SeedSessionAsync(dbContext);

        var result = await CreateService(dbContext).MarkSubmittedAsync(session.Id, user.Id, CancellationToken.None);

        Assert.Equal(ArrlSubmissionMarkResult.Marked, result);
        var updated = dbContext.Sessions.Single();
        Assert.Equal(ArrlSubmissionStatus.Submitted, updated.ArrlSubmissionStatus);
        Assert.Equal(Now, updated.ArrlSubmittedDate);
        Assert.Equal(user.Id, updated.ArrlSubmittedByUserId);
    }

    [Fact]
    public async Task MarkSubmitted_WritesAuditLogEntry()
    {
        await using var dbContext = CreateContext();
        var (_, session, user) = await SeedSessionAsync(dbContext);

        await CreateService(dbContext).MarkSubmittedAsync(session.Id, user.Id, CancellationToken.None);

        var audit = Assert.Single(dbContext.AuditLogs);
        Assert.Equal(user.Id, audit.UserId);
        Assert.Equal("ArrlSubmissionMarked", audit.Action);
        Assert.Equal(nameof(Session), audit.EntityType);
        Assert.Equal(session.Id, audit.EntityId);
        Assert.Equal(Now, audit.TimestampUtc);
    }

    [Fact]
    public async Task MarkSubmitted_AlreadySubmitted_IsNoOp_PreservesOriginalAuditInfo()
    {
        await using var dbContext = CreateContext();
        var originalDate = new DateTime(2026, 6, 2, 9, 0, 0, DateTimeKind.Utc);
        var (_, session, firstUser) = await SeedSessionAsync(dbContext);
        session.ArrlSubmissionStatus = ArrlSubmissionStatus.Submitted;
        session.ArrlSubmittedDate = originalDate;
        session.ArrlSubmittedByUserId = firstUser.Id;
        await dbContext.SaveChangesAsync();

        var secondUser = new User { Name = "Someone Else", Email = "other@example.com", Role = UserRole.SessionManager };
        dbContext.Users.Add(secondUser);
        await dbContext.SaveChangesAsync();

        var result = await CreateService(dbContext).MarkSubmittedAsync(session.Id, secondUser.Id, CancellationToken.None);

        Assert.Equal(ArrlSubmissionMarkResult.AlreadySubmitted, result);
        var unchanged = dbContext.Sessions.Single();
        Assert.Equal(originalDate, unchanged.ArrlSubmittedDate);
        Assert.Equal(firstUser.Id, unchanged.ArrlSubmittedByUserId);
        Assert.Empty(dbContext.AuditLogs);
    }

    [Fact]
    public async Task MarkSubmitted_UnknownSessionId_ReturnsSessionNotFound()
    {
        await using var dbContext = CreateContext();

        var result = await CreateService(dbContext).MarkSubmittedAsync(999, userId: 1, CancellationToken.None);

        Assert.Equal(ArrlSubmissionMarkResult.SessionNotFound, result);
        Assert.Empty(dbContext.AuditLogs);
    }
}
