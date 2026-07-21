using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.VecSubmissions;
using Xunit;

namespace VeSessionManager.Core.Tests;

public class VecSubmissionReportServiceTests
{
    private static readonly DateTime Now = new(2026, 7, 21, 12, 0, 0, DateTimeKind.Utc);

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static async Task<Team> SeedTeamAsync(AppDbContext dbContext, string teamCode = "TESTTEAM")
    {
        var team = new Team { Name = teamCode, CreatedUtc = Now };
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        return team;
    }

    private static async Task<(Vec Vec, FeeConfiguration FeeConfiguration)> SeedVecAndFeeConfigAsync(AppDbContext dbContext)
    {
        var vec = new Vec { Name = "ARRL" };
        var user = new User { Name = "System", Email = "system@localhost", Role = UserRole.SystemAdmin };
        var feeConfiguration = new FeeConfiguration
        {
            Vec = vec,
            EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            FeeCollectionEnabled = true,
            ExamFeeAmount = 15m,
            CreatedByUser = user,
            CreatedUtc = Now
        };
        dbContext.FeeConfigurations.Add(feeConfiguration);
        await dbContext.SaveChangesAsync();
        return (vec, feeConfiguration);
    }

    private static async Task<Session> SeedSessionAsync(
        AppDbContext dbContext, Team team, Vec vec, FeeConfiguration feeConfiguration,
        VecSubmissionStatus vecStatus = VecSubmissionStatus.NotSubmitted, SessionStatus status = SessionStatus.Active)
    {
        var session = new Session
        {
            ExamToolsSessionId = Guid.NewGuid().ToString(),
            Title = "Test Session",
            ScheduledStartUtc = new DateTime(2026, 6, 1, 17, 0, 0, DateTimeKind.Utc),
            Team = team,
            Vec = vec,
            FeeConfiguration = feeConfiguration,
            VecSubmissionStatus = vecStatus,
            Status = status,
            CreatedUtc = Now
        };
        dbContext.Sessions.Add(session);
        await dbContext.SaveChangesAsync();
        return session;
    }

    private static void AddCandidate(AppDbContext dbContext, Session session, CandidateApplicationStatus status) =>
        dbContext.Candidates.Add(new Candidate
        {
            Session = session,
            ApplicationStatus = status,
            DateRegisteredUtc = Now
        });

    [Theory]
    [InlineData(CandidateApplicationStatus.Granted)]
    [InlineData(CandidateApplicationStatus.Failed)]
    [InlineData(CandidateApplicationStatus.NotTested)]
    public async Task CountsSessions_WithTerminalCandidate_StillNotSubmitted(CandidateApplicationStatus terminalStatus)
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var (vec, feeConfiguration) = await SeedVecAndFeeConfigAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, team, vec, feeConfiguration);
        AddCandidate(dbContext, session, terminalStatus);
        await dbContext.SaveChangesAsync();

        var count = await new VecSubmissionReportService(dbContext).GetPendingSubmissionCountAsync(team.Id, CancellationToken.None);

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task ExcludesSessions_AlreadySubmitted()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var (vec, feeConfiguration) = await SeedVecAndFeeConfigAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, team, vec, feeConfiguration, vecStatus: VecSubmissionStatus.Submitted);
        AddCandidate(dbContext, session, CandidateApplicationStatus.Granted);
        await dbContext.SaveChangesAsync();

        var count = await new VecSubmissionReportService(dbContext).GetPendingSubmissionCountAsync(team.Id, CancellationToken.None);

        Assert.Equal(0, count);
    }

    [Theory]
    [InlineData(CandidateApplicationStatus.Unmatched)]
    [InlineData(CandidateApplicationStatus.Received)]
    public async Task ExcludesSessions_WithOnlyNonTerminalCandidates(CandidateApplicationStatus nonTerminalStatus)
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var (vec, feeConfiguration) = await SeedVecAndFeeConfigAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, team, vec, feeConfiguration);
        AddCandidate(dbContext, session, nonTerminalStatus);
        await dbContext.SaveChangesAsync();

        var count = await new VecSubmissionReportService(dbContext).GetPendingSubmissionCountAsync(team.Id, CancellationToken.None);

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task ExcludesCancelledSessions()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var (vec, feeConfiguration) = await SeedVecAndFeeConfigAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, team, vec, feeConfiguration, status: SessionStatus.Cancelled);
        AddCandidate(dbContext, session, CandidateApplicationStatus.Granted);
        await dbContext.SaveChangesAsync();

        var count = await new VecSubmissionReportService(dbContext).GetPendingSubmissionCountAsync(team.Id, CancellationToken.None);

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task MixedTerminalAndNonTerminalCandidates_StillCounts()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var (vec, feeConfiguration) = await SeedVecAndFeeConfigAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, team, vec, feeConfiguration);
        AddCandidate(dbContext, session, CandidateApplicationStatus.Unmatched);
        AddCandidate(dbContext, session, CandidateApplicationStatus.Granted);
        await dbContext.SaveChangesAsync();

        var count = await new VecSubmissionReportService(dbContext).GetPendingSubmissionCountAsync(team.Id, CancellationToken.None);

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task OnlyCountsTheGivenTeamsSessions()
    {
        await using var dbContext = CreateContext();
        var teamA = await SeedTeamAsync(dbContext, "TEAMA");
        var teamB = await SeedTeamAsync(dbContext, "TEAMB");
        var (vec, feeConfiguration) = await SeedVecAndFeeConfigAsync(dbContext);
        var sessionA = await SeedSessionAsync(dbContext, teamA, vec, feeConfiguration);
        var sessionB = await SeedSessionAsync(dbContext, teamB, vec, feeConfiguration);
        AddCandidate(dbContext, sessionA, CandidateApplicationStatus.Granted);
        AddCandidate(dbContext, sessionB, CandidateApplicationStatus.Granted);
        await dbContext.SaveChangesAsync();

        var count = await new VecSubmissionReportService(dbContext).GetPendingSubmissionCountAsync(teamA.Id, CancellationToken.None);

        Assert.Equal(1, count);
    }
}
