using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// <see cref="CandidateApplicationStatusExtensions.AwaitingFccGrant"/> — the one shared predicate
/// behind Applicant Status's own Pending list, its nav badge (<c>NavBadgeCountService</c>), and the
/// bulk-email screen reached from it, so all three can never quietly drift apart on who counts as
/// still waiting.
///
/// <para>#88's exclusion is added here rather than at each of those three call sites, for the same
/// reason: one shared predicate, one place a fix lands and reaches everywhere it's used.</para>
/// </summary>
public class AwaitingFccGrantTests
{
    private static readonly DateTime Now = new(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc);

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<Candidate> SeedTestedUnmatchedCandidateAsync(AppDbContext dbContext, DateTime? importedHistoricallyUtc)
    {
        var vec = new Vec { Name = "ARRL" };
        var team = new Team { Name = "TESTTEAM", CreatedUtc = Now };
        var user = new User { Name = "Sys", Email = $"s-{Guid.NewGuid():N}@localhost", Role = UserRole.SystemAdmin };
        var session = new Session
        {
            ExamToolsSessionId = $"s-{Guid.NewGuid():N}", Title = "Session", ScheduledStartUtc = Now.AddDays(-200),
            DurationMinutes = 60, Vec = vec, TeamId = team.Id,
            FeeConfiguration = new FeeConfiguration
            {
                Vec = vec, EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                FeeCollectionEnabled = true, ExamFeeAmount = 15m, CreatedByUser = user, CreatedUtc = Now
            },
            Status = SessionStatus.Active,
            ImportedHistoricallyUtc = importedHistoricallyUtc,
            CreatedUtc = Now
        };
        dbContext.Sessions.Add(session);
        await dbContext.SaveChangesAsync();

        var candidate = new Candidate
        {
            ExamToolsApplicantId = $"a-{Guid.NewGuid():N}", SessionId = session.Id, Name = "Roana Glory",
            DateRegisteredUtc = Now, Tested = true, ApplicationStatus = CandidateApplicationStatus.Unmatched
        };
        dbContext.Candidates.Add(candidate);
        await dbContext.SaveChangesAsync();
        return candidate;
    }

    [Fact]
    public async Task ExcludesACandidateOnAnImportedSession()
    {
        await using var dbContext = CreateContext();
        await SeedTestedUnmatchedCandidateAsync(dbContext, importedHistoricallyUtc: Now.AddDays(-1));

        var pending = await dbContext.Candidates.AwaitingFccGrant().ToListAsync();

        Assert.Empty(pending);
    }

    /// <summary>The correction #88 makes: a real, non-imported candidate who is simply old still counts as pending.</summary>
    [Fact]
    public async Task ARealCandidateWhoIsSimplyOld_StillCounts()
    {
        await using var dbContext = CreateContext();
        await SeedTestedUnmatchedCandidateAsync(dbContext, importedHistoricallyUtc: null);

        var pending = await dbContext.Candidates.AwaitingFccGrant().ToListAsync();

        Assert.Single(pending);
    }
}
