using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Navigation;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// The teamIds null-vs-empty distinction is the whole point of these tests: null means "every team"
/// (SystemAdmin) and an empty list means "no teams". Getting that backwards would silently show a
/// SystemAdmin an all-zero nav, which is exactly the kind of bug a badge can't self-report.
/// </summary>
public class NavBadgeCountServiceTests
{
    private static readonly DateTime Now = new(2026, 7, 21, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private static NavBadgeCountService CreateService(AppDbContext dbContext) =>
        new(dbContext, new FixedTimeProvider(Now));

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    /// <summary>Seeds one team with a session that is pending VEC submission, one candidate awaiting an FCC grant, and one unresolved unmatched payment — i.e. exactly 1 of each badge.</summary>
    private static async Task<Team> SeedTeamWithOneOfEachAsync(AppDbContext dbContext, string teamName)
    {
        var team = new Team { Name = teamName, CreatedUtc = Now };
        var vec = new Vec { Name = "ARRL" };
        var user = new User { Name = "System", Email = $"system-{teamName}@localhost", Role = UserRole.SystemAdmin };
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
            ScheduledStartUtc = new DateTime(2026, 6, 1, 17, 0, 0, DateTimeKind.Utc),
            Team = team,
            Vec = vec,
            FeeConfiguration = feeConfiguration,
            VecSubmissionStatus = VecSubmissionStatus.NotSubmitted,
            Status = SessionStatus.Active,
            CreatedUtc = Now
        };
        dbContext.Sessions.Add(session);

        // Terminal candidate → the session counts as pending VEC submission.
        dbContext.Candidates.Add(new Candidate { Session = session, ApplicationStatus = CandidateApplicationStatus.Granted, DateRegisteredUtc = Now });
        // Tested + non-terminal → counts as awaiting an FCC grant.
        dbContext.Candidates.Add(new Candidate { Session = session, Tested = true, ApplicationStatus = CandidateApplicationStatus.Received, DateRegisteredUtc = Now });
        await dbContext.SaveChangesAsync();

        dbContext.UnmatchedSquarePayments.Add(new UnmatchedSquarePayment
        {
            TeamId = team.Id,
            SquareOrderId = Guid.NewGuid().ToString(),
            SquarePaymentId = Guid.NewGuid().ToString(),
            AmountUsd = 15m,
            ReceivedUtc = Now
        });
        await dbContext.SaveChangesAsync();
        return team;
    }

    [Fact]
    public async Task NullTeamIds_CountsAcrossEveryTeam_NotZero()
    {
        // The SystemAdmin case — null must mean "no team filter", not "no teams".
        await using var dbContext = CreateContext();
        await SeedTeamWithOneOfEachAsync(dbContext, "TEAM-A");
        await SeedTeamWithOneOfEachAsync(dbContext, "TEAM-B");

        var counts = await CreateService(dbContext).GetCountsAsync(null, CancellationToken.None);

        Assert.Equal(2, counts.ApplicantsPendingGrant);
        Assert.Equal(2, counts.SessionsPendingVecSubmission);
        Assert.Equal(2, counts.UnresolvedUnmatchedPayments);
    }

    [Fact]
    public async Task EmptyTeamIds_CountsNothing()
    {
        await using var dbContext = CreateContext();
        await SeedTeamWithOneOfEachAsync(dbContext, "TEAM-A");

        var counts = await CreateService(dbContext).GetCountsAsync([], CancellationToken.None);

        Assert.Equal(0, counts.ApplicantsPendingGrant);
        Assert.Equal(0, counts.SessionsPendingVecSubmission);
        Assert.Equal(0, counts.UnresolvedUnmatchedPayments);
    }

    [Fact]
    public async Task ScopedToOneTeam_ExcludesOtherTeams()
    {
        await using var dbContext = CreateContext();
        var teamA = await SeedTeamWithOneOfEachAsync(dbContext, "TEAM-A");
        await SeedTeamWithOneOfEachAsync(dbContext, "TEAM-B");

        var counts = await CreateService(dbContext).GetCountsAsync([teamA.Id], CancellationToken.None);

        Assert.Equal(1, counts.ApplicantsPendingGrant);
        Assert.Equal(1, counts.SessionsPendingVecSubmission);
        Assert.Equal(1, counts.UnresolvedUnmatchedPayments);
    }

    [Fact]
    public async Task MultipleTeamIds_SumsAcrossThem()
    {
        await using var dbContext = CreateContext();
        var teamA = await SeedTeamWithOneOfEachAsync(dbContext, "TEAM-A");
        var teamB = await SeedTeamWithOneOfEachAsync(dbContext, "TEAM-B");
        await SeedTeamWithOneOfEachAsync(dbContext, "TEAM-C"); // not in scope

        var counts = await CreateService(dbContext).GetCountsAsync([teamA.Id, teamB.Id], CancellationToken.None);

        Assert.Equal(2, counts.ApplicantsPendingGrant);
        Assert.Equal(2, counts.SessionsPendingVecSubmission);
        Assert.Equal(2, counts.UnresolvedUnmatchedPayments);
    }

    [Fact]
    public async Task ResolvedUnmatchedPayment_IsNotCounted()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamWithOneOfEachAsync(dbContext, "TEAM-A");
        var unmatched = dbContext.UnmatchedSquarePayments.Single();
        unmatched.ResolvedUtc = Now;
        await dbContext.SaveChangesAsync();

        var counts = await CreateService(dbContext).GetCountsAsync([team.Id], CancellationToken.None);

        Assert.Equal(0, counts.UnresolvedUnmatchedPayments);
    }

    [Fact]
    public async Task UntestedOrGrantedCandidates_AreNotCountedAsAwaitingGrant()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamWithOneOfEachAsync(dbContext, "TEAM-A");
        var session = dbContext.Sessions.Single();

        // Not yet tested — nothing is with the FCC to wait on.
        dbContext.Candidates.Add(new Candidate { Session = session, Tested = false, ApplicationStatus = CandidateApplicationStatus.Unmatched, DateRegisteredUtc = Now });
        // Already granted — settled, no longer waiting.
        dbContext.Candidates.Add(new Candidate { Session = session, Tested = true, ApplicationStatus = CandidateApplicationStatus.Granted, DateRegisteredUtc = Now });
        await dbContext.SaveChangesAsync();

        var counts = await CreateService(dbContext).GetCountsAsync([team.Id], CancellationToken.None);

        Assert.Equal(1, counts.ApplicantsPendingGrant); // still just the one seeded Received candidate
    }

    /// <summary>
    /// The Applicants menu counts renewals too (#422-shaped ask): the parent chip is the sum of its
    /// items, and the Renewal Monitor's share is <c>NeedsAttention()</c> — the predicate whose own
    /// comment always said it was "what a future digest would count". A healthy or recently renewed
    /// license contributes nothing.
    /// </summary>
    [Fact]
    public async Task RenewalsNeedingAttention_CountsOnlyTheActionableStatuses()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamWithOneOfEachAsync(dbContext, "TEAM-A");

        dbContext.WatchedLicenses.AddRange(
            // Expired last month, past grace start but within it -> ExpiredInGrace, needs attention.
            new WatchedLicense { TeamId = team.Id, CallSign = "K0AAA", LastCheckedUtc = Now, ExpiredDateUtc = Now.AddDays(-10) },
            // Healthy for years -> Active, not counted.
            new WatchedLicense { TeamId = team.Id, CallSign = "K0BBB", LastCheckedUtc = Now, ExpiredDateUtc = Now.AddYears(5) },
            // Never found at the FCC -> NotFound, needs attention.
            new WatchedLicense { TeamId = team.Id, CallSign = "K0CCC", LastCheckedUtc = Now, NotFoundAtFcc = true });
        await dbContext.SaveChangesAsync();

        var counts = await CreateService(dbContext).GetCountsAsync([team.Id], CancellationToken.None);

        Assert.Equal(2, counts.RenewalsNeedingAttention);
    }

    [Fact]
    public async Task RenewalsNeedingAttention_RespectsTheTeamScope()
    {
        await using var dbContext = CreateContext();
        var teamA = await SeedTeamWithOneOfEachAsync(dbContext, "TEAM-A");
        var teamB = await SeedTeamWithOneOfEachAsync(dbContext, "TEAM-B");
        dbContext.WatchedLicenses.Add(
            new WatchedLicense { TeamId = teamB.Id, CallSign = "K0ZZZ", LastCheckedUtc = Now, ExpiredDateUtc = Now.AddDays(-10) });
        await dbContext.SaveChangesAsync();

        Assert.Equal(0, (await CreateService(dbContext).GetCountsAsync([teamA.Id], CancellationToken.None)).RenewalsNeedingAttention);
        Assert.Equal(1, (await CreateService(dbContext).GetCountsAsync([teamB.Id], CancellationToken.None)).RenewalsNeedingAttention);
        Assert.Equal(1, (await CreateService(dbContext).GetCountsAsync(null, CancellationToken.None)).RenewalsNeedingAttention);
        Assert.Equal(0, (await CreateService(dbContext).GetCountsAsync([], CancellationToken.None)).RenewalsNeedingAttention);
    }

}
