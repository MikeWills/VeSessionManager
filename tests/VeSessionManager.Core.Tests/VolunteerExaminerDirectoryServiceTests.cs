using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.VolunteerExaminers;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// The VE directory and the writes behind it (issue #142 phase 2). Covers the two things most
/// likely to be got wrong quietly: "last worked" counting a session that has not happened yet, and
/// a tag from one team being applied to another team's row.
/// </summary>
public class VolunteerExaminerDirectoryServiceTests
{
    private static readonly DateTime Now = new(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static VolunteerExaminerManagementService CreateManagement(AppDbContext dbContext) =>
        new(dbContext, new FixedTimeProvider(Now));

    private static async Task<Team> SeedTeamAsync(AppDbContext dbContext, string name)
    {
        var team = new Team { Name = name, ExamToolsTeamCode = name, CreatedUtc = Now };
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        return team;
    }

    private static async Task<(VolunteerExaminer Person, VeTeamMembership Membership)> SeedVeAsync(
        AppDbContext dbContext, Team team, string callSign, string name)
    {
        var person = new VolunteerExaminer { Name = name, CallSign = callSign, CreatedUtc = Now };
        var membership = new VeTeamMembership { VolunteerExaminer = person, Team = team, IsActive = true, CreatedUtc = Now };
        dbContext.VolunteerExaminers.Add(person);
        dbContext.VeTeamMemberships.Add(membership);
        await dbContext.SaveChangesAsync();
        return (person, membership);
    }

    private static async Task<Session> SeedSessionAsync(AppDbContext dbContext, Team team, DateTime startUtc, bool finished)
    {
        var vec = new Vec { Name = $"VEC-{Guid.NewGuid()}" };
        var user = new User { Name = "System", Email = $"{Guid.NewGuid()}@localhost", Role = UserRole.SystemAdmin };
        var session = new Session
        {
            ExamToolsSessionId = Guid.NewGuid().ToString(),
            Title = "Session",
            ScheduledStartUtc = startUtc,
            DurationMinutes = 60,
            Team = team,
            Vec = vec,
            FeeConfiguration = new FeeConfiguration
            {
                Vec = vec,
                EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                FeeCollectionEnabled = true,
                ExamFeeAmount = 15m,
                CreatedByUser = user,
                CreatedUtc = Now
            },
            Status = SessionStatus.Active,
            ExamToolsClosedUtc = finished ? startUtc.AddHours(2) : null,
            CreatedUtc = Now
        };
        dbContext.Sessions.Add(session);
        await dbContext.SaveChangesAsync();
        return session;
    }

    /// <summary>
    /// The Status trap, third instance. Session.Status only ever means "not cancelled", so a
    /// scheduled-but-unrun session would report as worked — a VE booked for next month would show a
    /// "last worked" date in the future.
    /// </summary>
    [Fact]
    public async Task LastWorked_IgnoresASessionThatHasNotHappenedYet()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, "TEAM-A");
        var (person, _) = await SeedVeAsync(dbContext, team, "N2SPG", "Sam Granger");

        var past = await SeedSessionAsync(dbContext, team, Now.AddDays(-30), finished: true);
        var future = await SeedSessionAsync(dbContext, team, Now.AddDays(30), finished: false);
        dbContext.SessionVolunteerExaminers.Add(new SessionVolunteerExaminer { Session = past, VolunteerExaminer = person });
        dbContext.SessionVolunteerExaminers.Add(new SessionVolunteerExaminer { Session = future, VolunteerExaminer = person });
        await dbContext.SaveChangesAsync();

        var rows = await new VolunteerExaminerDirectoryService(dbContext)
            .GetDirectoryAsync([team.Id], search: null, tagId: null, includeInactive: false, CancellationToken.None);

        Assert.Equal(past.ScheduledStartUtc, Assert.Single(rows).LastWorkedUtc);
    }

    /// <summary>Last worked is per team — a VE's outing for another team must not answer this team's question.</summary>
    [Fact]
    public async Task LastWorked_IsScopedToTheRowsOwnTeam()
    {
        await using var dbContext = CreateContext();
        var teamA = await SeedTeamAsync(dbContext, "TEAM-A");
        var teamB = await SeedTeamAsync(dbContext, "TEAM-B");
        var (person, _) = await SeedVeAsync(dbContext, teamA, "N2SPG", "Sam Granger");
        dbContext.VeTeamMemberships.Add(new VeTeamMembership { VolunteerExaminer = person, Team = teamB, IsActive = true, CreatedUtc = Now });

        var forA = await SeedSessionAsync(dbContext, teamA, Now.AddDays(-60), finished: true);
        var forB = await SeedSessionAsync(dbContext, teamB, Now.AddDays(-5), finished: true);
        dbContext.SessionVolunteerExaminers.Add(new SessionVolunteerExaminer { Session = forA, VolunteerExaminer = person });
        dbContext.SessionVolunteerExaminers.Add(new SessionVolunteerExaminer { Session = forB, VolunteerExaminer = person });
        await dbContext.SaveChangesAsync();

        var rows = await new VolunteerExaminerDirectoryService(dbContext)
            .GetDirectoryAsync(null, search: null, tagId: null, includeInactive: false, CancellationToken.None);

        Assert.Equal(2, rows.Count);
        Assert.Equal(forA.ScheduledStartUtc, rows.Single(r => r.TeamId == teamA.Id).LastWorkedUtc);
        Assert.Equal(forB.ScheduledStartUtc, rows.Single(r => r.TeamId == teamB.Id).LastWorkedUtc);
    }

    [Fact]
    public async Task NoTags_MeansGuest_AndIsDerivedNotStored()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, "TEAM-A");
        await SeedVeAsync(dbContext, team, "N2SPG", "Sam Granger");

        var rows = await new VolunteerExaminerDirectoryService(dbContext)
            .GetDirectoryAsync([team.Id], search: null, tagId: null, includeInactive: false, CancellationToken.None);

        Assert.True(Assert.Single(rows).IsGuest);
    }

    [Fact]
    public async Task RetiredMembership_IsHiddenUnlessAskedFor()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, "TEAM-A");
        var (_, membership) = await SeedVeAsync(dbContext, team, "N2SPG", "Sam Granger");

        Assert.Equal(VeManagementResult.Success,
            await CreateManagement(dbContext).SetMembershipActiveAsync(membership.Id, false, userId: 1, CancellationToken.None));

        var service = new VolunteerExaminerDirectoryService(dbContext);
        Assert.Empty(await service.GetDirectoryAsync([team.Id], null, null, includeInactive: false, CancellationToken.None));
        Assert.Single(await service.GetDirectoryAsync([team.Id], null, null, includeInactive: true, CancellationToken.None));
    }

    /// <summary>Retiring someone must never remove the row — their session history references them by id.</summary>
    [Fact]
    public async Task Inactivating_KeepsThePersonAndTheMembership()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, "TEAM-A");
        var (_, membership) = await SeedVeAsync(dbContext, team, "N2SPG", "Sam Granger");

        await CreateManagement(dbContext).SetMembershipActiveAsync(membership.Id, false, userId: 1, CancellationToken.None);

        Assert.Single(dbContext.VolunteerExaminers);
        var stored = Assert.Single(dbContext.VeTeamMemberships);
        Assert.False(stored.IsActive);
        Assert.Equal(Now, stored.InactivatedUtc);
    }

    /// <summary>Tags are a team's private vocabulary; an id from another team must be rejected rather than quietly applied.</summary>
    [Fact]
    public async Task SetTags_RejectsATagBelongingToAnotherTeam()
    {
        await using var dbContext = CreateContext();
        var teamA = await SeedTeamAsync(dbContext, "TEAM-A");
        var teamB = await SeedTeamAsync(dbContext, "TEAM-B");
        var (_, membershipOnA) = await SeedVeAsync(dbContext, teamA, "N2SPG", "Sam Granger");

        var management = CreateManagement(dbContext);
        var (_, teamBTag) = await management.CreateTagAsync(teamB.Id, "Team member", 0, userId: 1, CancellationToken.None);

        var result = await management.SetTagsAsync(membershipOnA.Id, [teamBTag!.Id], userId: 1, CancellationToken.None);

        Assert.Equal(VeManagementResult.TagNotOnThisTeam, result);
        Assert.Empty(dbContext.VeTagAssignments);
    }

    [Fact]
    public async Task SetTags_ReplacesTheWholeSet()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, "TEAM-A");
        var (_, membership) = await SeedVeAsync(dbContext, team, "N2SPG", "Sam Granger");

        var management = CreateManagement(dbContext);
        var (_, member) = await management.CreateTagAsync(team.Id, "Team member", 0, 1, CancellationToken.None);
        var (_, lead) = await management.CreateTagAsync(team.Id, "Team lead", 1, 1, CancellationToken.None);

        await management.SetTagsAsync(membership.Id, [member!.Id, lead!.Id], 1, CancellationToken.None);
        Assert.Equal(2, dbContext.VeTagAssignments.Count());

        await management.SetTagsAsync(membership.Id, [lead.Id], 1, CancellationToken.None);
        Assert.Equal(lead.Id, Assert.Single(dbContext.VeTagAssignments).VeTagId);
    }

    /// <summary>
    /// The phase 1 merge deliberately leaves same-call-sign-different-name rows alone, because
    /// merging two people cannot be undone. They have to be visible, or the data quietly stays wrong.
    /// </summary>
    [Fact]
    public async Task RowsSharingACallSign_AreFlaggedAsPossibleDuplicates()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, "TEAM-A");
        await SeedVeAsync(dbContext, team, "N2SPG", "Sam Granger");
        await SeedVeAsync(dbContext, team, "N2SPG", "Someone Else");
        await SeedVeAsync(dbContext, team, "NP2UU", "Uma Unwin");

        var rows = await new VolunteerExaminerDirectoryService(dbContext)
            .GetDirectoryAsync([team.Id], null, null, includeInactive: false, CancellationToken.None);

        Assert.Equal(2, rows.Count(r => r.HasDuplicateCallSign));
        Assert.False(rows.Single(r => r.VolunteerExaminer.CallSign == "NP2UU").HasDuplicateCallSign);
    }

    [Fact]
    public async Task Accreditation_IsRecordedOncePerVec()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, "TEAM-A");
        var (person, _) = await SeedVeAsync(dbContext, team, "N2SPG", "Sam Granger");
        var vec = new Vec { Name = "ARRL" };
        dbContext.Vecs.Add(vec);
        await dbContext.SaveChangesAsync();

        var management = CreateManagement(dbContext);
        Assert.Equal(VeManagementResult.Success,
            await management.AddAccreditationAsync(person.Id, vec.Id, "12345", null, 1, CancellationToken.None));
        Assert.Equal(VeManagementResult.AlreadyAccredited,
            await management.AddAccreditationAsync(person.Id, vec.Id, "12345", null, 1, CancellationToken.None));

        Assert.Single(dbContext.VeVecAccreditations);
    }
}
