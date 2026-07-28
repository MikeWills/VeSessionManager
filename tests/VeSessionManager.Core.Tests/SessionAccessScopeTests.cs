using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Authorization;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Core.Tests;

public class SessionAccessScopeTests
{
    private static readonly DateTime Now = new(2026, 7, 21, 12, 0, 0, DateTimeKind.Utc);

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

    private static async Task<(Vec Vec, FeeConfiguration FeeConfiguration)> SeedVecAndFeeConfigAsync(AppDbContext dbContext)
    {
        var vec = new Vec { Name = "ARRL" };
        var user = new User { Name = "System", Role = UserRole.SystemAdmin };
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

    private static async Task<Session> SeedSessionAsync(AppDbContext dbContext, Team team, Vec vec, FeeConfiguration feeConfiguration)
    {
        var session = new Session
        {
            ExamToolsSessionId = Guid.NewGuid().ToString(),
            Title = "Test Session",
            ScheduledStartUtc = new DateTime(2026, 6, 1, 17, 0, 0, DateTimeKind.Utc),
            Team = team,
            Vec = vec,
            FeeConfiguration = feeConfiguration,
            CreatedUtc = Now
        };
        dbContext.Sessions.Add(session);
        await dbContext.SaveChangesAsync();
        return session;
    }

    /// <summary>Builds a User with the given team memberships already populated in-memory (no DB
    /// round-trip needed — Scope/GetEffectiveTeamIds/CanView/CanEdit all work off the object graph
    /// directly, same as the pre-multi-team tests did with a single TeamId).</summary>
    private static User NewUser(string name, UserRole role, params int[] teamIds)
    {
        var user = new User { Name = name, Role = role };
        foreach (var teamId in teamIds)
        {
            user.UserTeams.Add(new UserTeam { TeamId = teamId });
        }
        return user;
    }

    private static readonly SessionAccessScope Scope = new();

    [Fact]
    public async Task SystemAdmin_SeesSessionsAcrossEveryTeam()
    {
        await using var dbContext = CreateContext();
        var (vec, feeConfiguration) = await SeedVecAndFeeConfigAsync(dbContext);
        var teamA = await SeedTeamAsync(dbContext, "TEAMA");
        var teamB = await SeedTeamAsync(dbContext, "TEAMB");
        await SeedSessionAsync(dbContext, teamA, vec, feeConfiguration);
        await SeedSessionAsync(dbContext, teamB, vec, feeConfiguration);
        var sysAdmin = new User { Name = "Sys Admin", Role = UserRole.SystemAdmin };

        var visible = Scope.Scope(dbContext.Sessions, sysAdmin).ToList();

        Assert.Equal(2, visible.Count);
    }

    [Fact]
    public async Task SystemAdmin_WithSelectedTeamId_SeesOnlyThatTeam()
    {
        await using var dbContext = CreateContext();
        var (vec, feeConfiguration) = await SeedVecAndFeeConfigAsync(dbContext);
        var teamA = await SeedTeamAsync(dbContext, "TEAMA");
        var teamB = await SeedTeamAsync(dbContext, "TEAMB");
        var sessionA = await SeedSessionAsync(dbContext, teamA, vec, feeConfiguration);
        await SeedSessionAsync(dbContext, teamB, vec, feeConfiguration);
        var sysAdmin = new User { Name = "Sys Admin", Role = UserRole.SystemAdmin };

        var visible = Scope.Scope(dbContext.Sessions, sysAdmin, teamA.Id).ToList();

        var visibleSession = Assert.Single(visible);
        Assert.Equal(sessionA.Id, visibleSession.Id);
    }

    [Theory]
    [InlineData(UserRole.TeamAdmin)]
    [InlineData(UserRole.SessionManager)]
    public async Task TeamAdminAndSessionManager_SeeOnlyTheirOwnTeamsSessions(UserRole role)
    {
        await using var dbContext = CreateContext();
        var (vec, feeConfiguration) = await SeedVecAndFeeConfigAsync(dbContext);
        var teamA = await SeedTeamAsync(dbContext, "TEAMA");
        var teamB = await SeedTeamAsync(dbContext, "TEAMB");
        var sessionA = await SeedSessionAsync(dbContext, teamA, vec, feeConfiguration);
        await SeedSessionAsync(dbContext, teamB, vec, feeConfiguration);
        var user = NewUser("Team User", role, teamA.Id);

        var visible = Scope.Scope(dbContext.Sessions, user).ToList();

        var visibleSession = Assert.Single(visible);
        Assert.Equal(sessionA.Id, visibleSession.Id);
    }

    [Theory]
    [InlineData(UserRole.TeamAdmin)]
    [InlineData(UserRole.SessionManager)]
    public async Task MultiTeamUser_SeesUnionOfBothTeamsSessions_UnfilteredByDefault(UserRole role)
    {
        await using var dbContext = CreateContext();
        var (vec, feeConfiguration) = await SeedVecAndFeeConfigAsync(dbContext);
        var teamA = await SeedTeamAsync(dbContext, "TEAMA");
        var teamB = await SeedTeamAsync(dbContext, "TEAMB");
        var teamC = await SeedTeamAsync(dbContext, "TEAMC");
        var sessionA = await SeedSessionAsync(dbContext, teamA, vec, feeConfiguration);
        var sessionB = await SeedSessionAsync(dbContext, teamB, vec, feeConfiguration);
        await SeedSessionAsync(dbContext, teamC, vec, feeConfiguration); // not a member — must never appear
        var user = NewUser("Multi Team User", role, teamA.Id, teamB.Id);

        var visible = Scope.Scope(dbContext.Sessions, user).ToList();

        Assert.Equal(2, visible.Count);
        Assert.Contains(visible, s => s.Id == sessionA.Id);
        Assert.Contains(visible, s => s.Id == sessionB.Id);
    }

    [Fact]
    public async Task MultiTeamUser_WithSelectedTeamId_NarrowsToJustThatTeam()
    {
        await using var dbContext = CreateContext();
        var (vec, feeConfiguration) = await SeedVecAndFeeConfigAsync(dbContext);
        var teamA = await SeedTeamAsync(dbContext, "TEAMA");
        var teamB = await SeedTeamAsync(dbContext, "TEAMB");
        var sessionA = await SeedSessionAsync(dbContext, teamA, vec, feeConfiguration);
        await SeedSessionAsync(dbContext, teamB, vec, feeConfiguration);
        var user = NewUser("Multi Team User", UserRole.SessionManager, teamA.Id, teamB.Id);

        var visible = Scope.Scope(dbContext.Sessions, user, teamA.Id).ToList();

        var visibleSession = Assert.Single(visible);
        Assert.Equal(sessionA.Id, visibleSession.Id);
    }

    [Fact]
    public async Task MultiTeamUser_WithSelectedTeamId_ForATeamTheyDoNotBelongTo_FallsBackToAllTheirTeams()
    {
        await using var dbContext = CreateContext();
        var (vec, feeConfiguration) = await SeedVecAndFeeConfigAsync(dbContext);
        var teamA = await SeedTeamAsync(dbContext, "TEAMA");
        var teamB = await SeedTeamAsync(dbContext, "TEAMB");
        var foreignTeam = await SeedTeamAsync(dbContext, "FOREIGN");
        var sessionA = await SeedSessionAsync(dbContext, teamA, vec, feeConfiguration);
        var sessionB = await SeedSessionAsync(dbContext, teamB, vec, feeConfiguration);
        await SeedSessionAsync(dbContext, foreignTeam, vec, feeConfiguration);
        var user = NewUser("Multi Team User", UserRole.SessionManager, teamA.Id, teamB.Id);

        // A tampered/foreign ?teamId= must never leak that team's data — falls back to "all my teams."
        var visible = Scope.Scope(dbContext.Sessions, user, foreignTeam.Id).ToList();

        Assert.Equal(2, visible.Count);
        Assert.Contains(visible, s => s.Id == sessionA.Id);
        Assert.Contains(visible, s => s.Id == sessionB.Id);
    }

    [Theory]
    [InlineData(UserRole.TeamAdmin)]
    [InlineData(UserRole.SessionManager)]
    public async Task UnassignedTeamAdminOrSessionManager_SeesNothing_NotEverything(UserRole role)
    {
        await using var dbContext = CreateContext();
        var (vec, feeConfiguration) = await SeedVecAndFeeConfigAsync(dbContext);
        var team = await SeedTeamAsync(dbContext, "TEAMA");
        await SeedSessionAsync(dbContext, team, vec, feeConfiguration);
        var unassigned = NewUser("Unassigned", role);

        var visible = Scope.Scope(dbContext.Sessions, unassigned).ToList();

        Assert.Empty(visible);
    }

    [Theory]
    [InlineData(UserRole.SessionManager)]
    [InlineData(UserRole.TeamAdmin)]
    public async Task TeamLead_SeesTheirAssignedManagersTeam_RegardlessOfManagersOwnRole(UserRole managerRole)
    {
        await using var dbContext = CreateContext();
        var (vec, feeConfiguration) = await SeedVecAndFeeConfigAsync(dbContext);
        var team = await SeedTeamAsync(dbContext, "TEAMA");
        var otherTeam = await SeedTeamAsync(dbContext, "TEAMB");
        var sessionInTeam = await SeedSessionAsync(dbContext, team, vec, feeConfiguration);
        await SeedSessionAsync(dbContext, otherTeam, vec, feeConfiguration);
        var manager = NewUser("Manager", managerRole, team.Id);
        var teamLead = new User { Name = "Team Lead", Role = UserRole.TeamLead, ManagedByUser = manager };

        var visible = Scope.Scope(dbContext.Sessions, teamLead).ToList();

        var visibleSession = Assert.Single(visible);
        Assert.Equal(sessionInTeam.Id, visibleSession.Id);
    }

    [Fact]
    public async Task TeamLead_WhoseManagerBelongsToMultipleTeams_InheritsAllOfThem()
    {
        await using var dbContext = CreateContext();
        var (vec, feeConfiguration) = await SeedVecAndFeeConfigAsync(dbContext);
        var teamA = await SeedTeamAsync(dbContext, "TEAMA");
        var teamB = await SeedTeamAsync(dbContext, "TEAMB");
        var teamC = await SeedTeamAsync(dbContext, "TEAMC");
        var sessionA = await SeedSessionAsync(dbContext, teamA, vec, feeConfiguration);
        var sessionB = await SeedSessionAsync(dbContext, teamB, vec, feeConfiguration);
        await SeedSessionAsync(dbContext, teamC, vec, feeConfiguration); // manager isn't on this team
        var manager = NewUser("Manager", UserRole.SessionManager, teamA.Id, teamB.Id);
        var teamLead = new User { Name = "Team Lead", Role = UserRole.TeamLead, ManagedByUser = manager };

        var visible = Scope.Scope(dbContext.Sessions, teamLead).ToList();

        Assert.Equal(2, visible.Count);
        Assert.Contains(visible, s => s.Id == sessionA.Id);
        Assert.Contains(visible, s => s.Id == sessionB.Id);
    }

    [Fact]
    public async Task TeamLead_WithNoManagerAssigned_SeesNothing()
    {
        await using var dbContext = CreateContext();
        var (vec, feeConfiguration) = await SeedVecAndFeeConfigAsync(dbContext);
        var team = await SeedTeamAsync(dbContext, "TEAMA");
        await SeedSessionAsync(dbContext, team, vec, feeConfiguration);
        var unassignedTeamLead = new User { Name = "Team Lead", Role = UserRole.TeamLead };

        var visible = Scope.Scope(dbContext.Sessions, unassignedTeamLead).ToList();

        Assert.Empty(visible);
    }

    [Theory]
    [InlineData(UserRole.SystemAdmin, true, true)]
    [InlineData(UserRole.TeamAdmin, true, false)]
    [InlineData(UserRole.SessionManager, true, false)]
    [InlineData(UserRole.TeamLead, false, false)]
    public void CanEdit_MatchesRoleAndTeamOwnership(UserRole role, bool canEditOwnTeam, bool canEditOtherTeam)
    {
        var ownTeamId = 1;
        var otherTeamId = 2;
        var ownSession = new Session { ExamToolsSessionId = "own", Title = "Own", TeamId = ownTeamId, CreatedUtc = Now };
        var otherSession = new Session { ExamToolsSessionId = "other", Title = "Other", TeamId = otherTeamId, CreatedUtc = Now };
        var user = NewUser("User", role, ownTeamId);
        if (role == UserRole.TeamLead)
        {
            user.ManagedByUser = NewUser("Manager", UserRole.SessionManager, ownTeamId);
        }

        Assert.Equal(canEditOwnTeam, Scope.CanEdit(user, ownSession));
        Assert.Equal(canEditOtherTeam, Scope.CanEdit(user, otherSession));
    }

    [Theory]
    [InlineData(UserRole.SystemAdmin, true, true)]
    [InlineData(UserRole.TeamAdmin, true, false)]
    [InlineData(UserRole.SessionManager, true, false)]
    [InlineData(UserRole.TeamLead, true, false)]
    public void CanView_UnlikeCanEdit_AllowsTeamLeadToViewTheirOwnTeamsSession(UserRole role, bool canViewOwnTeam, bool canViewOtherTeam)
    {
        var ownTeamId = 1;
        var otherTeamId = 2;
        var ownSession = new Session { ExamToolsSessionId = "own", Title = "Own", TeamId = ownTeamId, CreatedUtc = Now };
        var otherSession = new Session { ExamToolsSessionId = "other", Title = "Other", TeamId = otherTeamId, CreatedUtc = Now };
        var user = NewUser("User", role, ownTeamId);
        if (role == UserRole.TeamLead)
        {
            user.ManagedByUser = NewUser("Manager", UserRole.SessionManager, ownTeamId);
        }

        Assert.Equal(canViewOwnTeam, Scope.CanView(user, ownSession));
        Assert.Equal(canViewOtherTeam, Scope.CanView(user, otherSession));
    }

    [Fact]
    public void CanView_TeamLeadWithNoManagerAssigned_CannotViewAnySession()
    {
        var session = new Session { ExamToolsSessionId = "s", Title = "S", TeamId = 1, CreatedUtc = Now };
        var unassignedTeamLead = new User { Name = "Team Lead", Role = UserRole.TeamLead };

        Assert.False(Scope.CanView(unassignedTeamLead, session));
    }

    [Fact]
    public void MultiTeamUser_CanView_EitherOfTheirTeamsSessions_ButNotAThirdTeam()
    {
        var teamAId = 1;
        var teamBId = 2;
        var foreignTeamId = 3;
        var user = NewUser("Multi Team User", UserRole.SessionManager, teamAId, teamBId);
        var sessionA = new Session { ExamToolsSessionId = "a", Title = "A", TeamId = teamAId, CreatedUtc = Now };
        var sessionB = new Session { ExamToolsSessionId = "b", Title = "B", TeamId = teamBId, CreatedUtc = Now };
        var foreignSession = new Session { ExamToolsSessionId = "f", Title = "F", TeamId = foreignTeamId, CreatedUtc = Now };

        Assert.True(Scope.CanView(user, sessionA));
        Assert.True(Scope.CanView(user, sessionB));
        Assert.False(Scope.CanView(user, foreignSession));
    }

    [Fact]
    public void TryResolveViewableTeamId_SystemAdmin_UsesWhateverWasRequested()
    {
        Assert.Equal(5, Scope.TryResolveViewableTeamId(new User { Name = "Sys Admin", Role = UserRole.SystemAdmin }, 5));
        Assert.Null(Scope.TryResolveViewableTeamId(new User { Name = "Sys Admin", Role = UserRole.SystemAdmin }, null));
    }

    [Fact]
    public void TryResolveViewableTeamId_NonAdmin_DefaultsToFirstOwnTeam_WhenNothingRequested()
    {
        var user = NewUser("SM", UserRole.SessionManager, 10, 20);

        Assert.Equal(10, Scope.TryResolveViewableTeamId(user, null));
    }

    [Fact]
    public void TryResolveViewableTeamId_NonAdmin_HonorsRequestedTeam_WhenItsOneOfTheirs()
    {
        var user = NewUser("SM", UserRole.SessionManager, 10, 20);

        Assert.Equal(20, Scope.TryResolveViewableTeamId(user, 20));
    }

    [Fact]
    public void TryResolveViewableTeamId_NonAdmin_IgnoresForeignTeam_FallsBackToFirstOwnTeam()
    {
        var user = NewUser("SM", UserRole.SessionManager, 10, 20);

        Assert.Equal(10, Scope.TryResolveViewableTeamId(user, 999));
    }

    [Fact]
    public void TryResolveViewableTeamId_UnassignedNonAdmin_ReturnsNull()
    {
        var user = NewUser("SM", UserRole.SessionManager);

        Assert.Null(Scope.TryResolveViewableTeamId(user, null));
    }
}
