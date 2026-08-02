using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Core.Tests;

public class UserManagementServiceTests
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

    // AppDbContext is IdentityUserContext<User,int> (no Role tables) — UserOnlyStore is the
    // matching store type, same relationship SessionAccessScope's own doc points at for why the
    // app skips IdentityDbContext's unused Role tables. Mirrors how Program.cs's real
    // AddIdentityCore<User>().AddEntityFrameworkStores<AppDbContext>() wires this up, just built by
    // hand here since tests don't spin up the full DI container.
    private static UserManager<User> CreateUserManager(AppDbContext dbContext)
    {
        var store = new UserOnlyStore<User, AppDbContext, int>(dbContext);
        return new UserManager<User>(
            store,
            Options.Create(new IdentityOptions()),
            new PasswordHasher<User>(),
            [],
            [new PasswordValidator<User>()],
            new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(),
            services: null!,
            NullLogger<UserManager<User>>.Instance);
    }

    private static UserManagementService CreateService(AppDbContext dbContext, UserManager<User> userManager) =>
        new(userManager, dbContext, new FixedTimeProvider(Now));

    private const string ValidPassword = "Valid-Password1!";

    private static async Task<Team> SeedTeamAsync(AppDbContext dbContext, string name)
    {
        var team = new Team { Name = name, CreatedUtc = Now };
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        return team;
    }

    [Fact]
    public async Task CreateAsync_NewEmail_CreatesUserWithNoTeams_WritesAudit()
    {
        await using var dbContext = CreateContext();
        var userManager = CreateUserManager(dbContext);
        var actingUser = new User { UserName = "sysadmin@example.com", Email = "sysadmin@example.com", Name = "Sys Admin", Role = UserRole.SystemAdmin };
        await userManager.CreateAsync(actingUser, ValidPassword);

        var (result, created) = await CreateService(dbContext, userManager).CreateAsync(
            "new@example.com", "New User", UserRole.SessionManager, ValidPassword, actingUser.Id, CancellationToken.None);

        Assert.Equal(UserActionResult.Success, result);
        Assert.NotNull(created);
        Assert.Equal("New User", created!.Name);
        Assert.Equal(UserRole.SessionManager, created.Role);
        // Issue #19: team assignment is now a separate action (SetTeamsAsync) — a brand-new user
        // starts with zero team memberships, not a single team baked into creation.
        Assert.Empty(await dbContext.UserTeams.Where(ut => ut.UserId == created.Id).ToListAsync());
        var audit = await dbContext.AuditLogs.SingleAsync();
        Assert.Equal("UserCreated", audit.Action);
        Assert.Equal(nameof(User), audit.EntityType);
        Assert.Equal(created.Id, audit.EntityId);
    }

    [Fact]
    public async Task CreateAsync_DuplicateEmail_ReturnsDuplicateEmail()
    {
        await using var dbContext = CreateContext();
        var userManager = CreateUserManager(dbContext);
        var existing = new User { UserName = "taken@example.com", Email = "taken@example.com", Name = "Existing", Role = UserRole.SessionManager };
        await userManager.CreateAsync(existing, ValidPassword);

        var (result, created) = await CreateService(dbContext, userManager).CreateAsync(
            "taken@example.com", "New User", UserRole.SessionManager, ValidPassword, actingUserId: 1, CancellationToken.None);

        Assert.Equal(UserActionResult.DuplicateEmail, result);
        Assert.Null(created);
    }

    [Fact]
    public async Task CreateAsync_WeakPassword_ReturnsInvalidPassword_DoesNotCreateUser()
    {
        await using var dbContext = CreateContext();
        var userManager = CreateUserManager(dbContext);

        var (result, created) = await CreateService(dbContext, userManager).CreateAsync(
            "new@example.com", "New User", UserRole.SessionManager, "weak", actingUserId: 1, CancellationToken.None);

        Assert.Equal(UserActionResult.InvalidPassword, result);
        Assert.Null(created);
        Assert.Null(await userManager.FindByEmailAsync("new@example.com"));
    }

    [Fact]
    public async Task SetRoleAsync_ExistingUser_UpdatesRole_LeavesTeamsUntouched_WritesAudit()
    {
        await using var dbContext = CreateContext();
        var userManager = CreateUserManager(dbContext);
        var team = await SeedTeamAsync(dbContext, "TEAMA");
        var target = new User { UserName = "target@example.com", Email = "target@example.com", Name = "Target", Role = UserRole.SessionManager };
        await userManager.CreateAsync(target, ValidPassword);
        dbContext.UserTeams.Add(new UserTeam { UserId = target.Id, TeamId = team.Id, CreatedUtc = Now });
        await dbContext.SaveChangesAsync();

        var result = await CreateService(dbContext, userManager).SetRoleAsync(target.Id, UserRole.TeamAdmin, actingUserId: 1, CancellationToken.None);

        Assert.Equal(UserActionResult.Success, result);
        var updated = await userManager.FindByIdAsync(target.Id.ToString());
        Assert.Equal(UserRole.TeamAdmin, updated!.Role);
        // Role and team membership are independent actions now — changing role must not touch teams.
        Assert.Single(await dbContext.UserTeams.Where(ut => ut.UserId == target.Id).ToListAsync());
        Assert.Single(await dbContext.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task SetRoleAsync_UnknownUser_ReturnsNotFound()
    {
        await using var dbContext = CreateContext();
        var userManager = CreateUserManager(dbContext);

        var result = await CreateService(dbContext, userManager).SetRoleAsync(999, UserRole.TeamAdmin, actingUserId: 1, CancellationToken.None);

        Assert.Equal(UserActionResult.NotFound, result);
    }

    [Fact]
    public async Task SetTeamsAsync_NewTeams_AddsThemAndWritesAudit()
    {
        await using var dbContext = CreateContext();
        var userManager = CreateUserManager(dbContext);
        var teamA = await SeedTeamAsync(dbContext, "TEAMA");
        var teamB = await SeedTeamAsync(dbContext, "TEAMB");
        var target = new User { UserName = "target@example.com", Email = "target@example.com", Name = "Target", Role = UserRole.SessionManager };
        await userManager.CreateAsync(target, ValidPassword);

        var result = await CreateService(dbContext, userManager).SetTeamsAsync(target.Id, [teamA.Id, teamB.Id], actingUserId: 1, CancellationToken.None);

        Assert.Equal(UserActionResult.Success, result);
        var teamIds = await dbContext.UserTeams.Where(ut => ut.UserId == target.Id).Select(ut => ut.TeamId).ToListAsync();
        Assert.Equal([teamA.Id, teamB.Id], teamIds.OrderBy(id => id));
        var audit = await dbContext.AuditLogs.SingleAsync();
        Assert.Equal("UserTeamsChanged", audit.Action);
    }

    [Fact]
    public async Task SetTeamsAsync_RemovesTeamsNoLongerRequested_KeepsThoseStillRequested()
    {
        await using var dbContext = CreateContext();
        var userManager = CreateUserManager(dbContext);
        var teamA = await SeedTeamAsync(dbContext, "TEAMA");
        var teamB = await SeedTeamAsync(dbContext, "TEAMB");
        var teamC = await SeedTeamAsync(dbContext, "TEAMC");
        var target = new User { UserName = "target@example.com", Email = "target@example.com", Name = "Target", Role = UserRole.SessionManager };
        await userManager.CreateAsync(target, ValidPassword);
        dbContext.UserTeams.Add(new UserTeam { UserId = target.Id, TeamId = teamA.Id, CreatedUtc = Now });
        dbContext.UserTeams.Add(new UserTeam { UserId = target.Id, TeamId = teamB.Id, CreatedUtc = Now });
        await dbContext.SaveChangesAsync();

        // Drop TeamB, keep TeamA, add TeamC.
        var result = await CreateService(dbContext, userManager).SetTeamsAsync(target.Id, [teamA.Id, teamC.Id], actingUserId: 1, CancellationToken.None);

        Assert.Equal(UserActionResult.Success, result);
        var teamIds = await dbContext.UserTeams.Where(ut => ut.UserId == target.Id).Select(ut => ut.TeamId).ToListAsync();
        Assert.Equal([teamA.Id, teamC.Id], teamIds.OrderBy(id => id));
    }

    [Fact]
    public async Task SetTeamsAsync_EmptyList_RemovesAllTeams()
    {
        await using var dbContext = CreateContext();
        var userManager = CreateUserManager(dbContext);
        var teamA = await SeedTeamAsync(dbContext, "TEAMA");
        var target = new User { UserName = "target@example.com", Email = "target@example.com", Name = "Target", Role = UserRole.SessionManager };
        await userManager.CreateAsync(target, ValidPassword);
        dbContext.UserTeams.Add(new UserTeam { UserId = target.Id, TeamId = teamA.Id, CreatedUtc = Now });
        await dbContext.SaveChangesAsync();

        var result = await CreateService(dbContext, userManager).SetTeamsAsync(target.Id, [], actingUserId: 1, CancellationToken.None);

        Assert.Equal(UserActionResult.Success, result);
        Assert.Empty(await dbContext.UserTeams.Where(ut => ut.UserId == target.Id).ToListAsync());
    }

    [Fact]
    public async Task SetTeamsAsync_UnknownUser_ReturnsNotFound()
    {
        await using var dbContext = CreateContext();
        var userManager = CreateUserManager(dbContext);

        var result = await CreateService(dbContext, userManager).SetTeamsAsync(999, [1], actingUserId: 1, CancellationToken.None);

        Assert.Equal(UserActionResult.NotFound, result);
    }

    [Fact]
    public async Task SetManagerAsync_ExistingTeamLead_AssignsManager_WritesAudit()
    {
        await using var dbContext = CreateContext();
        var userManager = CreateUserManager(dbContext);
        var team = await SeedTeamAsync(dbContext, "TEAMA");
        var manager = new User { UserName = "manager@example.com", Email = "manager@example.com", Name = "Manager", Role = UserRole.SessionManager };
        var teamLead = new User { UserName = "lead@example.com", Email = "lead@example.com", Name = "Lead", Role = UserRole.TeamLead };
        await userManager.CreateAsync(manager, ValidPassword);
        await userManager.CreateAsync(teamLead, ValidPassword);
        dbContext.UserTeams.Add(new UserTeam { UserId = manager.Id, TeamId = team.Id, CreatedUtc = Now });
        dbContext.UserTeams.Add(new UserTeam { UserId = teamLead.Id, TeamId = team.Id, CreatedUtc = Now });
        await dbContext.SaveChangesAsync();

        var result = await CreateService(dbContext, userManager).SetManagerAsync(teamLead.Id, manager.Id, actingUserId: 1, CancellationToken.None);

        Assert.Equal(UserActionResult.Success, result);
        var updated = await userManager.FindByIdAsync(teamLead.Id.ToString());
        Assert.Equal(manager.Id, updated!.ManagedByUserId);
    }

    [Fact]
    public async Task SetManagerAsync_ManagerSharingAtLeastOneTeam_Succeeds_EvenIfBothBelongToOtherDifferentTeamsToo()
    {
        // Issue #19: a manager and TeamLead no longer need identical single teams — sharing any
        // one team is enough, even if each also belongs to other teams the other doesn't.
        await using var dbContext = CreateContext();
        var userManager = CreateUserManager(dbContext);
        var sharedTeam = await SeedTeamAsync(dbContext, "SHARED");
        var managerOnlyTeam = await SeedTeamAsync(dbContext, "MANAGERONLY");
        var leadOnlyTeam = await SeedTeamAsync(dbContext, "LEADONLY");
        var manager = new User { UserName = "manager@example.com", Email = "manager@example.com", Name = "Manager", Role = UserRole.SessionManager };
        var teamLead = new User { UserName = "lead@example.com", Email = "lead@example.com", Name = "Lead", Role = UserRole.TeamLead };
        await userManager.CreateAsync(manager, ValidPassword);
        await userManager.CreateAsync(teamLead, ValidPassword);
        dbContext.UserTeams.Add(new UserTeam { UserId = manager.Id, TeamId = sharedTeam.Id, CreatedUtc = Now });
        dbContext.UserTeams.Add(new UserTeam { UserId = manager.Id, TeamId = managerOnlyTeam.Id, CreatedUtc = Now });
        dbContext.UserTeams.Add(new UserTeam { UserId = teamLead.Id, TeamId = sharedTeam.Id, CreatedUtc = Now });
        dbContext.UserTeams.Add(new UserTeam { UserId = teamLead.Id, TeamId = leadOnlyTeam.Id, CreatedUtc = Now });
        await dbContext.SaveChangesAsync();

        var result = await CreateService(dbContext, userManager).SetManagerAsync(teamLead.Id, manager.Id, actingUserId: 1, CancellationToken.None);

        Assert.Equal(UserActionResult.Success, result);
    }

    [Fact]
    public async Task SetManagerAsync_ManagerSharingNoTeam_ReturnsInvalidManager_DoesNotAssign()
    {
        // Cross-tenant guard: a TeamAdmin must not be able to grant a TeamLead effective read
        // access into another team's sessions/candidates by assigning them a manager who shares no
        // team with them (SessionAccessScope resolves a TeamLead's scope via ManagedByUser.UserTeams).
        await using var dbContext = CreateContext();
        var userManager = CreateUserManager(dbContext);
        var teamA = await SeedTeamAsync(dbContext, "TEAMA");
        var teamB = await SeedTeamAsync(dbContext, "TEAMB");
        var otherTeamManager = new User { UserName = "manager@example.com", Email = "manager@example.com", Name = "Manager", Role = UserRole.SessionManager };
        var teamLead = new User { UserName = "lead@example.com", Email = "lead@example.com", Name = "Lead", Role = UserRole.TeamLead };
        await userManager.CreateAsync(otherTeamManager, ValidPassword);
        await userManager.CreateAsync(teamLead, ValidPassword);
        dbContext.UserTeams.Add(new UserTeam { UserId = otherTeamManager.Id, TeamId = teamB.Id, CreatedUtc = Now });
        dbContext.UserTeams.Add(new UserTeam { UserId = teamLead.Id, TeamId = teamA.Id, CreatedUtc = Now });
        await dbContext.SaveChangesAsync();

        var result = await CreateService(dbContext, userManager).SetManagerAsync(teamLead.Id, otherTeamManager.Id, actingUserId: 1, CancellationToken.None);

        Assert.Equal(UserActionResult.InvalidManager, result);
        var updated = await userManager.FindByIdAsync(teamLead.Id.ToString());
        Assert.Null(updated!.ManagedByUserId);
        Assert.Empty(await dbContext.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task DeactivateAsync_OtherUser_SetsLockout_WritesAudit()
    {
        await using var dbContext = CreateContext();
        var userManager = CreateUserManager(dbContext);
        var actingUser = new User { UserName = "sysadmin@example.com", Email = "sysadmin@example.com", Name = "Sys Admin", Role = UserRole.SystemAdmin };
        var target = new User { UserName = "target@example.com", Email = "target@example.com", Name = "Target", Role = UserRole.SessionManager };
        await userManager.CreateAsync(actingUser, ValidPassword);
        await userManager.CreateAsync(target, ValidPassword);
        var originalSecurityStamp = target.SecurityStamp;

        var result = await CreateService(dbContext, userManager).DeactivateAsync(target.Id, actingUser.Id, CancellationToken.None);

        Assert.Equal(UserActionResult.Success, result);
        var updated = await userManager.FindByIdAsync(target.Id.ToString());
        Assert.True(await userManager.IsLockedOutAsync(updated!));
        // Lockout alone only blocks future sign-ins — the security stamp must also change so an
        // already-issued auth cookie is rejected on its next SecurityStampValidator check instead
        // of continuing to work until it naturally expires.
        Assert.NotEqual(originalSecurityStamp, updated!.SecurityStamp);
        var audit = await dbContext.AuditLogs.SingleAsync();
        Assert.Equal("UserDeactivated", audit.Action);
    }

    [Fact]
    public async Task DeactivateAsync_Self_ReturnsCannotDeactivateSelf_DoesNotLockOut()
    {
        await using var dbContext = CreateContext();
        var userManager = CreateUserManager(dbContext);
        var self = new User { UserName = "sysadmin@example.com", Email = "sysadmin@example.com", Name = "Sys Admin", Role = UserRole.SystemAdmin };
        await userManager.CreateAsync(self, ValidPassword);

        var result = await CreateService(dbContext, userManager).DeactivateAsync(self.Id, self.Id, CancellationToken.None);

        Assert.Equal(UserActionResult.CannotDeactivateSelf, result);
        var reloaded = await userManager.FindByIdAsync(self.Id.ToString());
        Assert.False(await userManager.IsLockedOutAsync(reloaded!));
        Assert.Empty(dbContext.AuditLogs);
    }

    [Fact]
    public async Task ReactivateAsync_LockedOutUser_ClearsLockout_WritesAudit()
    {
        await using var dbContext = CreateContext();
        var userManager = CreateUserManager(dbContext);
        var target = new User { UserName = "target@example.com", Email = "target@example.com", Name = "Target", Role = UserRole.SessionManager };
        await userManager.CreateAsync(target, ValidPassword);
        await userManager.SetLockoutEnabledAsync(target, true);
        await userManager.SetLockoutEndDateAsync(target, DateTimeOffset.MaxValue);

        var result = await CreateService(dbContext, userManager).ReactivateAsync(target.Id, actingUserId: 1, CancellationToken.None);

        Assert.Equal(UserActionResult.Success, result);
        var updated = await userManager.FindByIdAsync(target.Id.ToString());
        Assert.False(await userManager.IsLockedOutAsync(updated!));
        var audit = await dbContext.AuditLogs.SingleAsync();
        Assert.Equal("UserReactivated", audit.Action);
    }

    // ---- Call sign (2026-07-30) ----
    // Stored upper-invariant to match VolunteerExaminer.CallSign's existing convention, so the two
    // are comparable; blank clears rather than storing "" so "no call sign" has one representation.

    [Theory]
    [InlineData("wx0mik", "WX0MIK")]
    [InlineData("  ke9caq  ", "KE9CAQ")]
    [InlineData("N2SPG", "N2SPG")]
    public async Task SetCallSignAsync_NormalizesToUpperInvariantAndTrims(string input, string expected)
    {
        await using var dbContext = CreateContext();
        var userManager = CreateUserManager(dbContext);
        var target = new User { UserName = "t@example.com", Email = "t@example.com", Name = "Target", Role = UserRole.SessionManager };
        await userManager.CreateAsync(target, ValidPassword);

        var result = await CreateService(dbContext, userManager).SetCallSignAsync(target.Id, input, actingUserId: 1, CancellationToken.None);

        Assert.Equal(UserActionResult.Success, result);
        Assert.Equal(expected, (await userManager.FindByIdAsync(target.Id.ToString()))!.CallSign);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SetCallSignAsync_BlankInput_ClearsToNull_NotEmptyString(string? input)
    {
        await using var dbContext = CreateContext();
        var userManager = CreateUserManager(dbContext);
        var target = new User { UserName = "t@example.com", Email = "t@example.com", Name = "Target", Role = UserRole.SessionManager, CallSign = "WX0MIK" };
        await userManager.CreateAsync(target, ValidPassword);

        var result = await CreateService(dbContext, userManager).SetCallSignAsync(target.Id, input, actingUserId: 1, CancellationToken.None);

        Assert.Equal(UserActionResult.Success, result);
        Assert.Null((await userManager.FindByIdAsync(target.Id.ToString()))!.CallSign);
    }

    [Fact]
    public async Task SetCallSignAsync_WritesAudit()
    {
        await using var dbContext = CreateContext();
        var userManager = CreateUserManager(dbContext);
        var target = new User { UserName = "t@example.com", Email = "t@example.com", Name = "Target", Role = UserRole.SessionManager };
        await userManager.CreateAsync(target, ValidPassword);

        await CreateService(dbContext, userManager).SetCallSignAsync(target.Id, "wx0mik", actingUserId: 1, CancellationToken.None);

        var audit = await dbContext.AuditLogs.SingleAsync();
        Assert.Equal("UserCallSignChanged", audit.Action);
        Assert.Contains("WX0MIK", audit.Details);
    }

    [Fact]
    public async Task SetCallSignAsync_UnknownUser_ReturnsNotFound()
    {
        await using var dbContext = CreateContext();
        var userManager = CreateUserManager(dbContext);

        var result = await CreateService(dbContext, userManager).SetCallSignAsync(9999, "WX0MIK", actingUserId: 1, CancellationToken.None);

        Assert.Equal(UserActionResult.NotFound, result);
    }

    [Fact]
    public async Task CreateAsync_WithCallSign_StoresItNormalized()
    {
        await using var dbContext = CreateContext();
        var userManager = CreateUserManager(dbContext);
        var actingUser = new User { UserName = "sysadmin@example.com", Email = "sysadmin@example.com", Name = "Sys Admin", Role = UserRole.SystemAdmin };
        await userManager.CreateAsync(actingUser, ValidPassword);

        var (result, created) = await CreateService(dbContext, userManager).CreateAsync(
            "new@example.com", "New User", UserRole.SessionManager, ValidPassword, actingUser.Id, CancellationToken.None, callSign: "wx0mik");

        Assert.Equal(UserActionResult.Success, result);
        Assert.Equal("WX0MIK", created!.CallSign);
    }

    // ---- Automatic retirement of the bootstrap account (2026-08-01) ----

    private static async Task<User> SeedBootstrapAdminAsync(AppDbContext dbContext, UserManager<User> userManager)
    {
        var bootstrap = new User
        {
            Name = "Setup Administrator",
            Email = UserManagementService.BootstrapAdminEmail,
            UserName = UserManagementService.BootstrapAdminEmail,
            Role = UserRole.SystemAdmin
        };
        await userManager.CreateAsync(bootstrap, ValidPassword);
        return bootstrap;
    }

    /// <summary>
    /// The temporary bootstrap account must not be something anyone has to remember to clean up —
    /// every minute it stays enabled is a standing exposure.
    /// </summary>
    [Fact]
    public async Task CreateAsync_NewSystemAdmin_AutomaticallyDeactivatesTheBootstrapAccount()
    {
        await using var dbContext = CreateContext();
        var userManager = CreateUserManager(dbContext);
        var bootstrap = await SeedBootstrapAdminAsync(dbContext, userManager);

        // Acting user is the bootstrap account itself — the realistic case, since on a fresh
        // deployment it is the only account that could have reached the create screen.
        var (result, _) = await CreateService(dbContext, userManager).CreateAsync(
            "real@example.com", "Real Admin", UserRole.SystemAdmin, ValidPassword, bootstrap.Id, CancellationToken.None);

        Assert.Equal(UserActionResult.Success, result);
        var retired = await userManager.FindByEmailAsync(UserManagementService.BootstrapAdminEmail);
        Assert.True(await userManager.IsLockedOutAsync(retired!));
        Assert.Contains(dbContext.AuditLogs, a => a.Action == "UserDeactivated" && a.EntityId == bootstrap.Id);
    }

    /// <summary>
    /// Creating a non-admin must not retire it — otherwise adding a Session Manager on a fresh
    /// deployment would lock the only person who can administer it out of their own server.
    /// </summary>
    [Fact]
    public async Task CreateAsync_NewNonAdmin_LeavesTheBootstrapAccountAlone()
    {
        await using var dbContext = CreateContext();
        var userManager = CreateUserManager(dbContext);
        var bootstrap = await SeedBootstrapAdminAsync(dbContext, userManager);

        await CreateService(dbContext, userManager).CreateAsync(
            "sm@example.com", "Session Manager", UserRole.SessionManager, ValidPassword, bootstrap.Id, CancellationToken.None);

        var stillActive = await userManager.FindByEmailAsync(UserManagementService.BootstrapAdminEmail);
        Assert.False(await userManager.IsLockedOutAsync(stillActive!));
    }

    /// <summary>A deployment that never had a bootstrap account (--create-admin was used) must be unaffected.</summary>
    [Fact]
    public async Task CreateAsync_NoBootstrapAccountPresent_IsANoOp()
    {
        await using var dbContext = CreateContext();
        var userManager = CreateUserManager(dbContext);
        var actingUser = new User { UserName = "sysadmin@example.com", Email = "sysadmin@example.com", Name = "Sys Admin", Role = UserRole.SystemAdmin };
        await userManager.CreateAsync(actingUser, ValidPassword);

        var (result, _) = await CreateService(dbContext, userManager).CreateAsync(
            "real@example.com", "Real Admin", UserRole.SystemAdmin, ValidPassword, actingUser.Id, CancellationToken.None);

        Assert.Equal(UserActionResult.Success, result);
        Assert.DoesNotContain(dbContext.AuditLogs, a => a.Action == "UserDeactivated");
    }
}
