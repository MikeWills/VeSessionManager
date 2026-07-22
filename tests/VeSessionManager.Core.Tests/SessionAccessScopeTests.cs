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
        var user = new User { Name = "Team User", Role = role, TeamId = teamA.Id };

        var visible = Scope.Scope(dbContext.Sessions, user).ToList();

        var visibleSession = Assert.Single(visible);
        Assert.Equal(sessionA.Id, visibleSession.Id);
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
        var unassigned = new User { Name = "Unassigned", Role = role, TeamId = null };

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
        var manager = new User { Name = "Manager", Role = managerRole, TeamId = team.Id };
        var teamLead = new User { Name = "Team Lead", Role = UserRole.TeamLead, ManagedByUser = manager };

        var visible = Scope.Scope(dbContext.Sessions, teamLead).ToList();

        var visibleSession = Assert.Single(visible);
        Assert.Equal(sessionInTeam.Id, visibleSession.Id);
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
        var user = new User
        {
            Name = "User",
            Role = role,
            TeamId = ownTeamId,
            ManagedByUser = role == UserRole.TeamLead ? new User { Name = "Manager", Role = UserRole.SessionManager, TeamId = ownTeamId } : null
        };

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
        var user = new User
        {
            Name = "User",
            Role = role,
            TeamId = ownTeamId,
            ManagedByUser = role == UserRole.TeamLead ? new User { Name = "Manager", Role = UserRole.SessionManager, TeamId = ownTeamId } : null
        };

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
}
