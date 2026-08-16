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

    /// <summary>Builds a User with the given team memberships already populated in-memory — same
    /// helper shape as SessionAccessScopeTests, since AdminAccessScope delegates team resolution to
    /// SessionAccessScope and needs the same UserTeams collection loaded.</summary>
    private static User NewUser(string name, UserRole role, params int[] teamIds)
    {
        var user = new User { Name = name, Role = role };
        foreach (var teamId in teamIds)
        {
            user.UserTeams.Add(new UserTeam { TeamId = teamId });
        }
        return user;
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
        var user = NewUser("User", role, ownTeamId);

        Assert.Equal(canManageOwnTeam, Scope.CanManageTeam(user, ownTeamId));
        Assert.Equal(canManageOtherTeam, Scope.CanManageTeam(user, otherTeamId));
    }

    [Fact]
    public void CanManageTeam_MultiTeamTeamAdmin_CanManageEitherOfTheirTeams()
    {
        var teamAdmin = NewUser("Team Admin", UserRole.TeamAdmin, 1, 2);

        Assert.True(Scope.CanManageTeam(teamAdmin, 1));
        Assert.True(Scope.CanManageTeam(teamAdmin, 2));
        Assert.False(Scope.CanManageTeam(teamAdmin, 3));
    }

    [Fact]
    public void CanManageUser_SystemAdmin_CanManageAnyone()
    {
        var sysAdmin = new User { Name = "Sys Admin", Role = UserRole.SystemAdmin };
        var targetTeamAdmin = NewUser("Target", UserRole.TeamAdmin, 5);

        Assert.True(Scope.CanManageUser(sysAdmin, targetTeamAdmin));
    }

    [Theory]
    [InlineData(UserRole.SessionManager, true)]
    [InlineData(UserRole.TeamLead, true)]
    [InlineData(UserRole.TeamAdmin, false)]
    [InlineData(UserRole.SystemAdmin, false)]
    public void CanManageUser_TeamAdmin_OnlySessionManagerOrTeamLead_OnOwnTeam(UserRole targetRole, bool expected)
    {
        var teamAdmin = NewUser("Team Admin", UserRole.TeamAdmin, 1);
        var target = NewUser("Target", targetRole, 1);

        Assert.Equal(expected, Scope.CanManageUser(teamAdmin, target));
    }

    [Fact]
    public void CanManageUser_TeamAdmin_CannotManageAcrossTeams()
    {
        var teamAdmin = NewUser("Team Admin", UserRole.TeamAdmin, 1);
        var otherTeamSessionManager = NewUser("Other", UserRole.SessionManager, 2);

        Assert.False(Scope.CanManageUser(teamAdmin, otherTeamSessionManager));
    }

    [Fact]
    public void CanManageUser_MultiTeamTeamAdmin_CanManageUserSharingEitherTeam_ButNotAThirdTeam()
    {
        var teamAdmin = NewUser("Team Admin", UserRole.TeamAdmin, 1, 2);
        var userOnTeam1 = NewUser("On Team 1", UserRole.SessionManager, 1);
        var userOnTeam2 = NewUser("On Team 2", UserRole.SessionManager, 2);
        var userOnTeam3 = NewUser("On Team 3", UserRole.SessionManager, 3);

        Assert.True(Scope.CanManageUser(teamAdmin, userOnTeam1));
        Assert.True(Scope.CanManageUser(teamAdmin, userOnTeam2));
        Assert.False(Scope.CanManageUser(teamAdmin, userOnTeam3));
    }

    [Theory]
    [InlineData(UserRole.SessionManager)]
    [InlineData(UserRole.TeamLead)]
    public void CanManageUser_SessionManagerOrTeamLead_CannotManageAnyone(UserRole actingRole)
    {
        var actingUser = NewUser("Acting", actingRole, 1);
        var target = NewUser("Target", UserRole.SessionManager, 1);

        Assert.False(Scope.CanManageUser(actingUser, target));
    }

    [Theory]
    [InlineData(UserRole.SessionManager, true)]
    [InlineData(UserRole.TeamLead, true)]
    [InlineData(UserRole.TeamAdmin, false)]
    [InlineData(UserRole.SystemAdmin, false)]
    public void CanAssignRole_TeamAdmin_OnlyGrantsSessionManagerOrTeamLead(UserRole newRole, bool expected)
    {
        var teamAdmin = NewUser("Team Admin", UserRole.TeamAdmin, 1);

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
        var teamAdmin = NewUser("Team Admin", UserRole.TeamAdmin, teamA.Id);

        var visible = Scope.ScopeTeams(dbContext.Teams, teamAdmin).ToList();

        var visibleTeam = Assert.Single(visible);
        Assert.Equal(teamA.Id, visibleTeam.Id);
    }

    [Fact]
    public async Task ScopeTeams_MultiTeamTeamAdmin_SeesBothOfTheirTeams_NotAThirdTeam()
    {
        await using var dbContext = CreateContext();
        var teamA = await SeedTeamAsync(dbContext, "TEAMA");
        var teamB = await SeedTeamAsync(dbContext, "TEAMB");
        await SeedTeamAsync(dbContext, "TEAMC");
        var teamAdmin = NewUser("Team Admin", UserRole.TeamAdmin, teamA.Id, teamB.Id);

        var visible = Scope.ScopeTeams(dbContext.Teams, teamAdmin).ToList();

        Assert.Equal(2, visible.Count);
        Assert.Contains(visible, t => t.Id == teamA.Id);
        Assert.Contains(visible, t => t.Id == teamB.Id);
    }

    [Fact]
    public async Task ScopeAuditLog_SystemAdmin_SeesEverything()
    {
        await using var dbContext = CreateContext();
        var teamA = await SeedTeamAsync(dbContext, "TEAMA");
        var teamAUser = NewUser("Team A User", UserRole.SessionManager, teamA.Id);
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
    public async Task ScopeAuditLog_TeamAdmin_SeesOnlyOwnTeamsUserActions()
    {
        await using var dbContext = CreateContext();
        var teamA = await SeedTeamAsync(dbContext, "TEAMA");
        var teamB = await SeedTeamAsync(dbContext, "TEAMB");
        var teamAUser = NewUser("Team A User", UserRole.SessionManager, teamA.Id);
        var teamBUser = NewUser("Team B User", UserRole.SessionManager, teamB.Id);
        dbContext.Users.AddRange(teamAUser, teamBUser);
        await dbContext.SaveChangesAsync();
        dbContext.AuditLogs.Add(new AuditLog { User = teamAUser, Action = "TeamAAction", EntityType = "Test", EntityId = 1, TimestampUtc = Now });
        dbContext.AuditLogs.Add(new AuditLog { User = teamBUser, Action = "TeamBAction", EntityType = "Test", EntityId = 2, TimestampUtc = Now });
        await dbContext.SaveChangesAsync();
        var teamAdmin = NewUser("Team Admin", UserRole.TeamAdmin, teamA.Id);

        var visible = Scope.ScopeAuditLog(dbContext.AuditLogs, teamAdmin).ToList();

        var entry = Assert.Single(visible);
        Assert.Equal("TeamAAction", entry.Action);
    }

    // ---- #86 part 3: background-job entries were invisible to a TeamAdmin -------------------

    /// <summary>
    /// The bug this closes. <c>AuditLog.UserId</c> is null for anything a background job did, and the
    /// TeamAdmin filter matched on "a user on my team took this action" — so every automated entry
    /// (candidates withdrawn from the feed, PII purged, Zoom/Discord cancellations, exam results
    /// auto-marked) matched nothing and was silently absent. A TeamAdmin reviewing their team's
    /// history saw only what humans had done, with nothing indicating the rest existed.
    /// </summary>
    [Fact]
    public async Task ScopeAuditLog_TeamAdmin_SeesBackgroundJobEntriesForTheirOwnTeam()
    {
        await using var dbContext = CreateContext();
        var teamA = await SeedTeamAsync(dbContext, "TEAMA");
        dbContext.AuditLogs.Add(new AuditLog
        {
            UserId = null, TeamId = teamA.Id,
            Action = "CandidateWithdrawnFromFeed", EntityType = "Candidate", EntityId = 1, TimestampUtc = Now
        });
        await dbContext.SaveChangesAsync();
        var teamAdmin = NewUser("Team Admin", UserRole.TeamAdmin, teamA.Id);

        var visible = Scope.ScopeAuditLog(dbContext.AuditLogs, teamAdmin).ToList();

        Assert.Equal("CandidateWithdrawnFromFeed", Assert.Single(visible).Action);
    }

    /// <summary>Team attribution has to cut both ways, or the fix would hand every TeamAdmin the whole deployment's automated history.</summary>
    [Fact]
    public async Task ScopeAuditLog_TeamAdmin_DoesNotSeeAnotherTeamsBackgroundJobEntries()
    {
        await using var dbContext = CreateContext();
        var teamA = await SeedTeamAsync(dbContext, "TEAMA");
        var teamB = await SeedTeamAsync(dbContext, "TEAMB");
        dbContext.AuditLogs.Add(new AuditLog
        {
            UserId = null, TeamId = teamB.Id,
            Action = "CandidatePiiPurged", EntityType = "Candidate", EntityId = 1, TimestampUtc = Now
        });
        await dbContext.SaveChangesAsync();
        var teamAdmin = NewUser("Team Admin", UserRole.TeamAdmin, teamA.Id);

        Assert.Empty(Scope.ScopeAuditLog(dbContext.AuditLogs, teamAdmin));
    }

    /// <summary>
    /// A background entry with no team attribution at all stays SystemAdmin-only, deliberately.
    /// <c>VolunteerExaminer</c> is a global entity here — a VE can be on several teams' rosters — so
    /// a VE PII purge or a self-service email change genuinely belongs to no single team, and
    /// guessing one would show it to a TeamAdmin who has no claim on it. Documented in
    /// docs/audit-log.md rather than left as a surprising blank.
    /// </summary>
    [Fact]
    public async Task ScopeAuditLog_TeamAdmin_StillDoesNotSeeUnattributableBackgroundEntries()
    {
        await using var dbContext = CreateContext();
        var teamA = await SeedTeamAsync(dbContext, "TEAMA");
        dbContext.AuditLogs.Add(new AuditLog
        {
            UserId = null, TeamId = null,
            Action = "VolunteerExaminerPiiPurged", EntityType = "VolunteerExaminer", EntityId = 1, TimestampUtc = Now
        });
        await dbContext.SaveChangesAsync();
        var teamAdmin = NewUser("Team Admin", UserRole.TeamAdmin, teamA.Id);

        Assert.Empty(Scope.ScopeAuditLog(dbContext.AuditLogs, teamAdmin));
    }

    /// <summary>A SystemAdmin is unfiltered, so team attribution must not narrow what they see either.</summary>
    [Fact]
    public async Task ScopeAuditLog_SystemAdmin_SeesBackgroundEntriesWithAndWithoutATeam()
    {
        await using var dbContext = CreateContext();
        var teamA = await SeedTeamAsync(dbContext, "TEAMA");
        dbContext.AuditLogs.Add(new AuditLog { UserId = null, TeamId = teamA.Id, Action = "Attributed", EntityType = "Test", EntityId = 1, TimestampUtc = Now });
        dbContext.AuditLogs.Add(new AuditLog { UserId = null, TeamId = null, Action = "Unattributed", EntityType = "Test", EntityId = 2, TimestampUtc = Now });
        await dbContext.SaveChangesAsync();
        var sysAdmin = new User { Name = "Sys Admin", Role = UserRole.SystemAdmin };

        Assert.Equal(2, Scope.ScopeAuditLog(dbContext.AuditLogs, sysAdmin).Count());
    }

    [Fact]
    public async Task ScopeAuditLog_MultiTeamTeamAdmin_SeesActionsFromEitherOfTheirTeamsUsers()
    {
        await using var dbContext = CreateContext();
        var teamA = await SeedTeamAsync(dbContext, "TEAMA");
        var teamB = await SeedTeamAsync(dbContext, "TEAMB");
        var teamC = await SeedTeamAsync(dbContext, "TEAMC");
        var teamAUser = NewUser("Team A User", UserRole.SessionManager, teamA.Id);
        var teamBUser = NewUser("Team B User", UserRole.SessionManager, teamB.Id);
        var teamCUser = NewUser("Team C User", UserRole.SessionManager, teamC.Id);
        dbContext.Users.AddRange(teamAUser, teamBUser, teamCUser);
        await dbContext.SaveChangesAsync();
        dbContext.AuditLogs.Add(new AuditLog { User = teamAUser, Action = "TeamAAction", EntityType = "Test", EntityId = 1, TimestampUtc = Now });
        dbContext.AuditLogs.Add(new AuditLog { User = teamBUser, Action = "TeamBAction", EntityType = "Test", EntityId = 2, TimestampUtc = Now });
        dbContext.AuditLogs.Add(new AuditLog { User = teamCUser, Action = "TeamCAction", EntityType = "Test", EntityId = 3, TimestampUtc = Now });
        await dbContext.SaveChangesAsync();
        var teamAdmin = NewUser("Team Admin", UserRole.TeamAdmin, teamA.Id, teamB.Id);

        var visible = Scope.ScopeAuditLog(dbContext.AuditLogs, teamAdmin).ToList();

        Assert.Equal(2, visible.Count);
        Assert.Contains(visible, a => a.Action == "TeamAAction");
        Assert.Contains(visible, a => a.Action == "TeamBAction");
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
        var teamAdmin = NewUser("Team Admin", UserRole.TeamAdmin, teamA.Id);

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

    [Fact]
    public void TryResolveManageableTeamId_MultiTeamTeamAdmin_HonorsRequestedOwnTeam()
    {
        var teamAdmin = NewUser("Team Admin", UserRole.TeamAdmin, 10, 20);

        Assert.Equal(20, Scope.TryResolveManageableTeamId(teamAdmin, 20));
    }

    [Fact]
    public void TryResolveManageableTeamId_MultiTeamTeamAdmin_DefaultsToFirstTeam_WhenNothingRequested()
    {
        var teamAdmin = NewUser("Team Admin", UserRole.TeamAdmin, 10, 20);

        Assert.Equal(10, Scope.TryResolveManageableTeamId(teamAdmin, null));
    }

    [Fact]
    public void TryResolveManageableTeamId_MultiTeamTeamAdmin_IgnoresForeignTeam_FallsBackToFirstOwnTeam()
    {
        var teamAdmin = NewUser("Team Admin", UserRole.TeamAdmin, 10, 20);

        Assert.Equal(10, Scope.TryResolveManageableTeamId(teamAdmin, 999));
    }
}
