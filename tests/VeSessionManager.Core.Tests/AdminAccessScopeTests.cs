using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Authorization;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Core.Tests;

public class AdminAccessScopeTests
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

    private static readonly AdminAccessScope Scope = new(new SessionAccessScope());

    [Theory]
    [InlineData(UserRole.SystemAdmin, true, true)]
    [InlineData(UserRole.TeamAdmin, true, false)]
    [InlineData(UserRole.SessionManager, false, false)]
    [InlineData(UserRole.TeamLead, false, false)]
    public void CanManageTeam_MatchesRoleAndTeamOwnership(UserRole role, bool canManageOwnTeam, bool canManageOtherTeam)
    {
        var ownTeamId = 1;
        var otherTeamId = 2;
        var user = new User { Name = "User", Role = role, TeamId = ownTeamId };

        Assert.Equal(canManageOwnTeam, Scope.CanManageTeam(user, ownTeamId));
        Assert.Equal(canManageOtherTeam, Scope.CanManageTeam(user, otherTeamId));
    }

    [Fact]
    public void CanManageUser_SystemAdmin_CanManageAnyone()
    {
        var sysAdmin = new User { Name = "Sys Admin", Role = UserRole.SystemAdmin };
        var targetTeamAdmin = new User { Name = "Target", Role = UserRole.TeamAdmin, TeamId = 5 };

        Assert.True(Scope.CanManageUser(sysAdmin, targetTeamAdmin));
    }

    [Theory]
    [InlineData(UserRole.SessionManager, true)]
    [InlineData(UserRole.TeamLead, true)]
    [InlineData(UserRole.TeamAdmin, false)]
    [InlineData(UserRole.SystemAdmin, false)]
    public void CanManageUser_TeamAdmin_OnlySessionManagerOrTeamLead_OnOwnTeam(UserRole targetRole, bool expected)
    {
        var teamAdmin = new User { Name = "Team Admin", Role = UserRole.TeamAdmin, TeamId = 1 };
        var target = new User { Name = "Target", Role = targetRole, TeamId = 1 };

        Assert.Equal(expected, Scope.CanManageUser(teamAdmin, target));
    }

    [Fact]
    public void CanManageUser_TeamAdmin_CannotManageAcrossTeams()
    {
        var teamAdmin = new User { Name = "Team Admin", Role = UserRole.TeamAdmin, TeamId = 1 };
        var otherTeamSessionManager = new User { Name = "Other", Role = UserRole.SessionManager, TeamId = 2 };

        Assert.False(Scope.CanManageUser(teamAdmin, otherTeamSessionManager));
    }

    [Theory]
    [InlineData(UserRole.SessionManager)]
    [InlineData(UserRole.TeamLead)]
    public void CanManageUser_SessionManagerOrTeamLead_CannotManageAnyone(UserRole actingRole)
    {
        var actingUser = new User { Name = "Acting", Role = actingRole, TeamId = 1 };
        var target = new User { Name = "Target", Role = UserRole.SessionManager, TeamId = 1 };

        Assert.False(Scope.CanManageUser(actingUser, target));
    }

    [Theory]
    [InlineData(UserRole.SessionManager, true)]
    [InlineData(UserRole.TeamLead, true)]
    [InlineData(UserRole.TeamAdmin, false)]
    [InlineData(UserRole.SystemAdmin, false)]
    public void CanAssignRole_TeamAdmin_OnlyGrantsSessionManagerOrTeamLead(UserRole newRole, bool expected)
    {
        var teamAdmin = new User { Name = "Team Admin", Role = UserRole.TeamAdmin, TeamId = 1 };

        Assert.Equal(expected, Scope.CanAssignRole(teamAdmin, newRole));
    }

    [Theory]
    [InlineData(UserRole.SystemAdmin)]
    [InlineData(UserRole.TeamAdmin)]
    [InlineData(UserRole.SessionManager)]
    [InlineData(UserRole.TeamLead)]
    public void CanAssignRole_SystemAdmin_GrantsAnyRole(UserRole newRole)
    {
        // Re-run under a SystemAdmin acting user for every possible target role.
        var sysAdmin = new User { Name = "Sys Admin", Role = UserRole.SystemAdmin };

        Assert.True(Scope.CanAssignRole(sysAdmin, newRole));
    }

    [Theory]
    [InlineData(UserRole.SystemAdmin, true)]
    [InlineData(UserRole.TeamAdmin, false)]
    [InlineData(UserRole.SessionManager, false)]
    [InlineData(UserRole.TeamLead, false)]
    public void CanAccessVecManagement_SystemAdminOnly(UserRole role, bool expected)
    {
        var user = new User { Name = "User", Role = role };
        Assert.Equal(expected, Scope.CanAccessVecManagement(user));
    }

    [Theory]
    [InlineData(UserRole.SystemAdmin, true)]
    [InlineData(UserRole.TeamAdmin, false)]
    public void CanAccessSystemSettings_SystemAdminOnly(UserRole role, bool expected)
    {
        var user = new User { Name = "User", Role = role };
        Assert.Equal(expected, Scope.CanAccessSystemSettings(user));
    }

    [Theory]
    [InlineData(UserRole.SystemAdmin, true)]
    [InlineData(UserRole.TeamAdmin, false)]
    public void CanCreateTeam_SystemAdminOnly(UserRole role, bool expected)
    {
        var user = new User { Name = "User", Role = role };
        Assert.Equal(expected, Scope.CanCreateTeam(user));
    }

    [Fact]
    public async Task ScopeTeams_SystemAdmin_SeesAllTeams()
    {
        await using var dbContext = CreateContext();
        await SeedTeamAsync(dbContext, "TEAMA");
        await SeedTeamAsync(dbContext, "TEAMB");
        var sysAdmin = new User { Name = "Sys Admin", Role = UserRole.SystemAdmin };

        var visible = Scope.ScopeTeams(dbContext.Teams, sysAdmin).ToList();

        Assert.Equal(2, visible.Count);
    }

    [Fact]
    public async Task ScopeTeams_TeamAdmin_SeesOnlyOwnTeam()
    {
        await using var dbContext = CreateContext();
        var teamA = await SeedTeamAsync(dbContext, "TEAMA");
        await SeedTeamAsync(dbContext, "TEAMB");
        var teamAdmin = new User { Name = "Team Admin", Role = UserRole.TeamAdmin, TeamId = teamA.Id };

        var visible = Scope.ScopeTeams(dbContext.Teams, teamAdmin).ToList();

        var visibleTeam = Assert.Single(visible);
        Assert.Equal(teamA.Id, visibleTeam.Id);
    }

    [Fact]
    public async Task ScopeAuditLog_SystemAdmin_SeesEverything()
    {
        await using var dbContext = CreateContext();
        var teamA = await SeedTeamAsync(dbContext, "TEAMA");
        var teamAUser = new User { Name = "Team A User", Role = UserRole.SessionManager, TeamId = teamA.Id };
        dbContext.Users.Add(teamAUser);
        await dbContext.SaveChangesAsync();
        dbContext.AuditLogs.Add(new AuditLog { User = teamAUser, Action = "Test", EntityType = "Test", EntityId = 1, TimestampUtc = Now });
        dbContext.AuditLogs.Add(new AuditLog { UserId = null, Action = "JobAction", EntityType = "Test", EntityId = 2, TimestampUtc = Now });
        await dbContext.SaveChangesAsync();
        var sysAdmin = new User { Name = "Sys Admin", Role = UserRole.SystemAdmin };

        var visible = Scope.ScopeAuditLog(dbContext.AuditLogs, sysAdmin).ToList();

        Assert.Equal(2, visible.Count);
    }

    [Fact]
    public async Task ScopeAuditLog_TeamAdmin_SeesOnlyOwnTeamsUserActions_NotUnattributedEntries()
    {
        await using var dbContext = CreateContext();
        var teamA = await SeedTeamAsync(dbContext, "TEAMA");
        var teamB = await SeedTeamAsync(dbContext, "TEAMB");
        var teamAUser = new User { Name = "Team A User", Role = UserRole.SessionManager, TeamId = teamA.Id };
        var teamBUser = new User { Name = "Team B User", Role = UserRole.SessionManager, TeamId = teamB.Id };
        dbContext.Users.AddRange(teamAUser, teamBUser);
        await dbContext.SaveChangesAsync();
        dbContext.AuditLogs.Add(new AuditLog { User = teamAUser, Action = "TeamAAction", EntityType = "Test", EntityId = 1, TimestampUtc = Now });
        dbContext.AuditLogs.Add(new AuditLog { User = teamBUser, Action = "TeamBAction", EntityType = "Test", EntityId = 2, TimestampUtc = Now });
        dbContext.AuditLogs.Add(new AuditLog { UserId = null, Action = "JobAction", EntityType = "Test", EntityId = 3, TimestampUtc = Now });
        await dbContext.SaveChangesAsync();
        var teamAdmin = new User { Name = "Team Admin", Role = UserRole.TeamAdmin, TeamId = teamA.Id };

        var visible = Scope.ScopeAuditLog(dbContext.AuditLogs, teamAdmin).ToList();

        var entry = Assert.Single(visible);
        Assert.Equal("TeamAAction", entry.Action);
    }

    [Fact]
    public async Task ScopeJobRunHistory_TeamAdmin_SeesOnlyOwnTeamsRuns()
    {
        await using var dbContext = CreateContext();
        var teamA = await SeedTeamAsync(dbContext, "TEAMA");
        var teamB = await SeedTeamAsync(dbContext, "TEAMB");
        dbContext.JobRunHistories.Add(new JobRunHistory { JobName = "TeamAJob", Team = teamA, StartedUtc = Now, Success = true });
        dbContext.JobRunHistories.Add(new JobRunHistory { JobName = "TeamBJob", Team = teamB, StartedUtc = Now, Success = true });
        dbContext.JobRunHistories.Add(new JobRunHistory { JobName = "GlobalJob", TeamId = null, StartedUtc = Now, Success = true });
        await dbContext.SaveChangesAsync();
        var teamAdmin = new User { Name = "Team Admin", Role = UserRole.TeamAdmin, TeamId = teamA.Id };

        var visible = Scope.ScopeJobRunHistory(dbContext.JobRunHistories, teamAdmin).ToList();

        var run = Assert.Single(visible);
        Assert.Equal("TeamAJob", run.JobName);
    }

    [Fact]
    public async Task ScopeJobRunHistory_SystemAdmin_SeesEverythingIncludingGlobalRuns()
    {
        await using var dbContext = CreateContext();
        var teamA = await SeedTeamAsync(dbContext, "TEAMA");
        dbContext.JobRunHistories.Add(new JobRunHistory { JobName = "TeamAJob", Team = teamA, StartedUtc = Now, Success = true });
        dbContext.JobRunHistories.Add(new JobRunHistory { JobName = "GlobalJob", TeamId = null, StartedUtc = Now, Success = true });
        await dbContext.SaveChangesAsync();
        var sysAdmin = new User { Name = "Sys Admin", Role = UserRole.SystemAdmin };

        var visible = Scope.ScopeJobRunHistory(dbContext.JobRunHistories, sysAdmin).ToList();

        Assert.Equal(2, visible.Count);
    }
}
