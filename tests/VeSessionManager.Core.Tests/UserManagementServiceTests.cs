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

    [Fact]
    public async Task CreateAsync_NewEmail_CreatesUserAndWritesAudit()
    {
        await using var dbContext = CreateContext();
        var userManager = CreateUserManager(dbContext);
        var actingUser = new User { UserName = "sysadmin@example.com", Email = "sysadmin@example.com", Name = "Sys Admin", Role = UserRole.SystemAdmin };
        await userManager.CreateAsync(actingUser, ValidPassword);

        var (result, created) = await CreateService(dbContext, userManager).CreateAsync(
            "new@example.com", "New User", UserRole.SessionManager, teamId: 1, ValidPassword, actingUser.Id, CancellationToken.None);

        Assert.Equal(UserActionResult.Success, result);
        Assert.NotNull(created);
        Assert.Equal("New User", created!.Name);
        Assert.Equal(UserRole.SessionManager, created.Role);
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
            "taken@example.com", "New User", UserRole.SessionManager, null, ValidPassword, actingUserId: 1, CancellationToken.None);

        Assert.Equal(UserActionResult.DuplicateEmail, result);
        Assert.Null(created);
    }

    [Fact]
    public async Task CreateAsync_WeakPassword_ReturnsInvalidPassword_DoesNotCreateUser()
    {
        await using var dbContext = CreateContext();
        var userManager = CreateUserManager(dbContext);

        var (result, created) = await CreateService(dbContext, userManager).CreateAsync(
            "new@example.com", "New User", UserRole.SessionManager, null, "weak", actingUserId: 1, CancellationToken.None);

        Assert.Equal(UserActionResult.InvalidPassword, result);
        Assert.Null(created);
        Assert.Null(await userManager.FindByEmailAsync("new@example.com"));
    }

    [Fact]
    public async Task SetRoleAsync_ExistingUser_UpdatesRoleAndTeam_WritesAudit()
    {
        await using var dbContext = CreateContext();
        var userManager = CreateUserManager(dbContext);
        var target = new User { UserName = "target@example.com", Email = "target@example.com", Name = "Target", Role = UserRole.SessionManager, TeamId = 1 };
        await userManager.CreateAsync(target, ValidPassword);

        var result = await CreateService(dbContext, userManager).SetRoleAsync(target.Id, UserRole.TeamAdmin, 1, actingUserId: 1, CancellationToken.None);

        Assert.Equal(UserActionResult.Success, result);
        var updated = await userManager.FindByIdAsync(target.Id.ToString());
        Assert.Equal(UserRole.TeamAdmin, updated!.Role);
        Assert.Single(await dbContext.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task SetRoleAsync_UnknownUser_ReturnsNotFound()
    {
        await using var dbContext = CreateContext();
        var userManager = CreateUserManager(dbContext);

        var result = await CreateService(dbContext, userManager).SetRoleAsync(999, UserRole.TeamAdmin, 1, actingUserId: 1, CancellationToken.None);

        Assert.Equal(UserActionResult.NotFound, result);
    }

    [Fact]
    public async Task SetManagerAsync_ExistingTeamLead_AssignsManager_WritesAudit()
    {
        await using var dbContext = CreateContext();
        var userManager = CreateUserManager(dbContext);
        var manager = new User { UserName = "manager@example.com", Email = "manager@example.com", Name = "Manager", Role = UserRole.SessionManager, TeamId = 1 };
        var teamLead = new User { UserName = "lead@example.com", Email = "lead@example.com", Name = "Lead", Role = UserRole.TeamLead, TeamId = 1 };
        await userManager.CreateAsync(manager, ValidPassword);
        await userManager.CreateAsync(teamLead, ValidPassword);

        var result = await CreateService(dbContext, userManager).SetManagerAsync(teamLead.Id, manager.Id, actingUserId: 1, CancellationToken.None);

        Assert.Equal(UserActionResult.Success, result);
        var updated = await userManager.FindByIdAsync(teamLead.Id.ToString());
        Assert.Equal(manager.Id, updated!.ManagedByUserId);
    }

    [Fact]
    public async Task SetManagerAsync_ManagerOnDifferentTeam_ReturnsInvalidManager_DoesNotAssign()
    {
        // Cross-tenant guard: a TeamAdmin must not be able to grant a TeamLead effective read
        // access into another team's sessions/candidates by assigning them a manager who belongs
        // to a different team (SessionAccessScope resolves a TeamLead's scope via
        // ManagedByUser.TeamId).
        await using var dbContext = CreateContext();
        var userManager = CreateUserManager(dbContext);
        var otherTeamManager = new User { UserName = "manager@example.com", Email = "manager@example.com", Name = "Manager", Role = UserRole.SessionManager, TeamId = 2 };
        var teamLead = new User { UserName = "lead@example.com", Email = "lead@example.com", Name = "Lead", Role = UserRole.TeamLead, TeamId = 1 };
        await userManager.CreateAsync(otherTeamManager, ValidPassword);
        await userManager.CreateAsync(teamLead, ValidPassword);

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
        var target = new User { UserName = "target@example.com", Email = "target@example.com", Name = "Target", Role = UserRole.SessionManager, TeamId = 1 };
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
        var target = new User { UserName = "target@example.com", Email = "target@example.com", Name = "Target", Role = UserRole.SessionManager, TeamId = 1 };
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
}
