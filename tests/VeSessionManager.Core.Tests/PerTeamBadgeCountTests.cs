using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Navigation;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Per-team counts behind the Applicant Status / Unmatched Payments team-picker pills (added
/// 2026-07-30). The invariant worth protecting: a pill's number must equal the row count you get
/// after clicking it, which is only true because both sides share a predicate in
/// NavBadgeCountService. See PendingVecSubmissionCountTests for the VEC-submission predicate.
/// </summary>
public class PerTeamBadgeCountTests
{
    private static readonly DateTime Now = new(2026, 7, 30, 12, 0, 0, DateTimeKind.Utc);

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static async Task<Team> SeedTeamAsync(AppDbContext dbContext, string name)
    {
        var team = new Team { Name = name, CreatedUtc = Now };
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        return team;
    }

    private static async Task<Session> SeedSessionAsync(AppDbContext dbContext, Team team)
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
        var session = new Session
        {
            ExamToolsSessionId = Guid.NewGuid().ToString(),
            Title = "Test Session",
            ScheduledStartUtc = new DateTime(2026, 7, 1, 17, 0, 0, DateTimeKind.Utc),
            Team = team,
            Vec = vec,
            FeeConfiguration = feeConfiguration,
            CreatedUtc = Now
        };
        dbContext.Sessions.Add(session);
        await dbContext.SaveChangesAsync();
        return session;
    }

    private static void AddCandidate(AppDbContext dbContext, Session session, bool tested, CandidateApplicationStatus status) =>
        dbContext.Candidates.Add(new Candidate
        {
            Session = session,
            Tested = tested,
            ApplicationStatus = status,
            DateRegisteredUtc = Now
        });

    private static void AddUnmatchedPayment(AppDbContext dbContext, Team team, bool resolved) =>
        dbContext.UnmatchedSquarePayments.Add(new UnmatchedSquarePayment
        {
            Team = team,
            SquareOrderId = Guid.NewGuid().ToString(),
            SquarePaymentId = Guid.NewGuid().ToString(),
            AmountUsd = 15m,
            ReceivedUtc = Now,
            ResolvedUtc = resolved ? Now : null
        });

    [Fact]
    public async Task PendingGrant_CountsPerTeam_AndNeverBleedsAcrossTeams()
    {
        await using var dbContext = CreateContext();
        var teamA = await SeedTeamAsync(dbContext, "TEAMA");
        var teamB = await SeedTeamAsync(dbContext, "TEAMB");
        var sessionA = await SeedSessionAsync(dbContext, teamA);
        var sessionB = await SeedSessionAsync(dbContext, teamB);

        AddCandidate(dbContext, sessionA, tested: true, CandidateApplicationStatus.Unmatched);
        AddCandidate(dbContext, sessionA, tested: true, CandidateApplicationStatus.Received);
        AddCandidate(dbContext, sessionB, tested: true, CandidateApplicationStatus.Received);
        await dbContext.SaveChangesAsync();

        var counts = await new NavBadgeCountService(dbContext, TimeProvider.System)
            .GetApplicantsPendingGrantByTeamAsync([teamA.Id, teamB.Id], CancellationToken.None);

        Assert.Equal(2, counts.CountFor(teamA.Id));
        Assert.Equal(1, counts.CountFor(teamB.Id));
    }

    [Theory]
    [InlineData(false, CandidateApplicationStatus.Received)]  // registered but never sat the exam
    [InlineData(true, CandidateApplicationStatus.Granted)]    // already issued — drops off the worklist
    [InlineData(true, CandidateApplicationStatus.Failed)]
    public async Task PendingGrant_ExcludesCandidatesTheWorklistItselfExcludes(bool tested, CandidateApplicationStatus status)
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, "TEAMA");
        var session = await SeedSessionAsync(dbContext, team);
        AddCandidate(dbContext, session, tested, status);
        await dbContext.SaveChangesAsync();

        var counts = await new NavBadgeCountService(dbContext, TimeProvider.System)
            .GetApplicantsPendingGrantByTeamAsync([team.Id], CancellationToken.None);

        Assert.Equal(0, counts.CountFor(team.Id));
    }

    [Fact]
    public async Task UnmatchedPayments_CountsPerTeam_ExcludingResolved()
    {
        await using var dbContext = CreateContext();
        var teamA = await SeedTeamAsync(dbContext, "TEAMA");
        var teamB = await SeedTeamAsync(dbContext, "TEAMB");

        AddUnmatchedPayment(dbContext, teamA, resolved: false);
        AddUnmatchedPayment(dbContext, teamA, resolved: false);
        AddUnmatchedPayment(dbContext, teamA, resolved: true);
        AddUnmatchedPayment(dbContext, teamB, resolved: true);
        await dbContext.SaveChangesAsync();

        var counts = await new NavBadgeCountService(dbContext, TimeProvider.System)
            .GetUnresolvedUnmatchedPaymentsByTeamAsync([teamA.Id, teamB.Id], CancellationToken.None);

        Assert.Equal(2, counts.CountFor(teamA.Id));
        // Team B's only payment is resolved, so it's absent from the dictionary entirely — the pill
        // still needs to render "0", which is exactly what CountFor is for.
        Assert.Equal(0, counts.CountFor(teamB.Id));
        Assert.False(counts.ContainsKey(teamB.Id));
    }

    [Fact]
    public async Task EmptyTeamList_ReturnsEmpty_WithoutQuerying()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, "TEAMA");
        AddUnmatchedPayment(dbContext, team, resolved: false);
        await dbContext.SaveChangesAsync();

        var counts = await new NavBadgeCountService(dbContext, TimeProvider.System)
            .GetUnresolvedUnmatchedPaymentsByTeamAsync([], CancellationToken.None);

        Assert.Empty(counts);
    }
}
