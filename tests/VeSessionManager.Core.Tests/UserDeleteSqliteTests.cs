using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Deleting a user with no history (#188).
///
/// <para><b>Real SQLite.</b> Every foreign key to User is <c>Restrict</c>, and InMemory enforces
/// none of them — so a test that "passes" there proves nothing about the case this feature is
/// entirely about: refusing politely instead of throwing a constraint violation at an admin.</para>
/// </summary>
public class UserDeleteSqliteTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private readonly SqliteConnection connection;
    private readonly ServiceProvider provider;

    public UserDeleteSqliteTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(o => o.UseSqlite(connection));
        services.AddIdentityCore<User>(o => o.Password.RequireNonAlphanumeric = false)
            .AddEntityFrameworkStores<AppDbContext>();
        provider = services.BuildServiceProvider();

        provider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        provider.Dispose();
        connection.Dispose();
    }

    private AppDbContext Db => provider.GetRequiredService<AppDbContext>();

    private UserManagementService CreateService() =>
        new(provider.GetRequiredService<UserManager<User>>(), Db, new FixedTimeProvider(Now));

    /// <summary>An account with a password, so the "last sign-in-capable" guard behaves realistically.</summary>
    private async Task<User> SeedUserAsync(string email, bool withPassword = true)
    {
        var userManager = provider.GetRequiredService<UserManager<User>>();
        var user = new User { UserName = email, Email = email, Name = email, Role = UserRole.SessionManager };
        var result = withPassword
            ? await userManager.CreateAsync(user, "Correct-Horse-1")
            : await userManager.CreateAsync(user);
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Description)));
        return user;
    }

    /// <summary>The lifecycle row every real account carries — what made "no references" never true.</summary>
    private async Task AddLifecycleAuditAsync(User user, int actingUserId)
    {
        Db.AuditLogs.Add(new AuditLog
        {
            UserId = actingUserId,
            Action = "UserCreated",
            EntityType = nameof(User),
            EntityId = user.Id,
            TimestampUtc = Now
        });
        await Db.SaveChangesAsync();
    }

    [Fact]
    public async Task AThrowawayAccountIsDeleted_AlongWithItsOwnLifecycleRows()
    {
        var admin = await SeedUserAsync("admin@example.com");
        var throwaway = await SeedUserAsync("typo@example.com");
        await AddLifecycleAuditAsync(throwaway, admin.Id);

        var result = await CreateService().DeleteAsync(throwaway.Id, admin.Id, CancellationToken.None);

        Assert.Equal(UserDeleteOutcome.Deleted, result.Outcome);

        await using var verify = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);

        Assert.Null(await verify.Users.FirstOrDefaultAsync(u => u.Id == throwaway.Id));

        // Its own lifecycle row went with it; the deletion itself is on the record, naming the email.
        Assert.Empty(await verify.AuditLogs.Where(a => a.EntityType == nameof(User) && a.EntityId == throwaway.Id && a.Action == "UserCreated").ToListAsync());
        var deletion = await verify.AuditLogs.SingleAsync(a => a.Action == "UserDeleted");
        Assert.Contains("typo@example.com", deletion.Details);
    }

    /// <summary>
    /// The property that stops this being a way to erase what somebody did: an account that acted on
    /// anything else is refused, and told so.
    /// </summary>
    [Fact]
    public async Task AnAccountThatActedOnSomethingElseIsRefused_AndTheReasonNamesIt()
    {
        var admin = await SeedUserAsync("admin@example.com");
        var worker = await SeedUserAsync("worked@example.com");
        await AddLifecycleAuditAsync(worker, admin.Id);

        Db.AuditLogs.Add(new AuditLog
        {
            UserId = worker.Id,
            Action = "CandidateMarkedFailed",
            EntityType = "Candidate",
            EntityId = 42,
            TimestampUtc = Now
        });
        await Db.SaveChangesAsync();

        var result = await CreateService().DeleteAsync(worker.Id, admin.Id, CancellationToken.None);

        Assert.Equal(UserDeleteOutcome.HasHistory, result.Outcome);
        Assert.Contains("1 recorded action", result.Blockers);

        await using var verify = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        Assert.NotNull(await verify.Users.FirstOrDefaultAsync(u => u.Id == worker.Id));
    }

    /// <summary>
    /// A real domain reference, through a Restrict FK. This is the case that would throw a constraint
    /// violation rather than refuse, if the blocker were missing — which is what the coverage test
    /// guards structurally and this one demonstrates.
    /// </summary>
    [Fact]
    public async Task AnAccountThatRequestedAnImportIsRefused_WithACountedReason()
    {
        var admin = await SeedUserAsync("admin@example.com");
        var importer = await SeedUserAsync("importer@example.com");

        var team = new Team { Name = "HRCC", ExamToolsTeamCode = "HRCC" };
        Db.Teams.Add(team);
        await Db.SaveChangesAsync();

        Db.HistoricalImportRequests.Add(new HistoricalImportRequest
        {
            TeamId = team.Id,
            RequestedByUserId = importer.Id,
            StartDate = new DateOnly(2026, 1, 1),
            EndDate = new DateOnly(2026, 2, 1),
            RequestedUtc = Now
        });
        await Db.SaveChangesAsync();

        var result = await CreateService().DeleteAsync(importer.Id, admin.Id, CancellationToken.None);

        Assert.Equal(UserDeleteOutcome.HasHistory, result.Outcome);
        Assert.Contains("1 historical import requested", result.Blockers);
    }

    /// <summary>Reported all at once — clearing one only to be told about the next teaches nothing.</summary>
    [Fact]
    public async Task EveryBlockerIsReported_NotJustTheFirst()
    {
        var admin = await SeedUserAsync("admin@example.com");
        var busy = await SeedUserAsync("busy@example.com");
        var managed = await SeedUserAsync("managed@example.com");

        managed.ManagedByUserId = busy.Id;
        Db.AuditLogs.Add(new AuditLog { UserId = busy.Id, Action = "Whatever", EntityType = "Candidate", EntityId = 7, TimestampUtc = Now });
        await Db.SaveChangesAsync();

        var result = await CreateService().DeleteAsync(busy.Id, admin.Id, CancellationToken.None);

        Assert.Equal(UserDeleteOutcome.HasHistory, result.Outcome);
        Assert.Contains("1 user managed by this account", result.Blockers);
        Assert.Contains("1 recorded action", result.Blockers);
    }

    /// <summary>Memberships are configuration, not history — removed with the account rather than blocking.</summary>
    [Fact]
    public async Task TeamMembershipsAreRemovedRatherThanBlocking()
    {
        var admin = await SeedUserAsync("admin@example.com");
        var member = await SeedUserAsync("member@example.com");

        var team = new Team { Name = "HRCC", ExamToolsTeamCode = "HRCC" };
        Db.Teams.Add(team);
        await Db.SaveChangesAsync();
        Db.UserTeams.Add(new UserTeam { UserId = member.Id, TeamId = team.Id });
        await Db.SaveChangesAsync();

        var result = await CreateService().DeleteAsync(member.Id, admin.Id, CancellationToken.None);

        Assert.Equal(UserDeleteOutcome.Deleted, result.Outcome);

        await using var verify = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        Assert.Empty(await verify.UserTeams.Where(ut => ut.UserId == member.Id).ToListAsync());
    }

    [Fact]
    public async Task DeletingYourselfIsRefused()
    {
        var admin = await SeedUserAsync("admin@example.com");

        var result = await CreateService().DeleteAsync(admin.Id, admin.Id, CancellationToken.None);

        Assert.Equal(UserDeleteOutcome.CannotDeleteSelf, result.Outcome);
    }

    /// <summary>
    /// The deployment must not be able to lock itself out. Mirrors Web's startup guard: "can anyone
    /// sign in", not "does a user exist" — so a passwordless System row does not count as rescue.
    /// </summary>
    [Fact]
    public async Task TheLastAccountThatCanSignInIsRefused()
    {
        var only = await SeedUserAsync("only@example.com");
        var system = await SeedUserAsync("system@localhost", withPassword: false);

        var result = await CreateService().DeleteAsync(only.Id, system.Id, CancellationToken.None);

        Assert.Equal(UserDeleteOutcome.LastSignInCapableAccount, result.Outcome);

        await using var verify = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        Assert.NotNull(await verify.Users.FirstOrDefaultAsync(u => u.Id == only.Id));
    }

    /// <summary>But a passwordless account is itself deletable — it was never a way in.</summary>
    [Fact]
    public async Task APasswordlessAccountIsNotProtectedByThatGuard()
    {
        var admin = await SeedUserAsync("admin@example.com");
        var system = await SeedUserAsync("system@localhost", withPassword: false);

        var result = await CreateService().DeleteAsync(system.Id, admin.Id, CancellationToken.None);

        Assert.Equal(UserDeleteOutcome.Deleted, result.Outcome);
    }
}
