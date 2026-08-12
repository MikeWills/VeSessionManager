using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Issue #287: three create paths save twice — once to get the new row's id, once for the audit
/// entry that needs it — with nothing making the pair atomic.
///
/// <para><b>Real SQLite, and it has to be.</b> EF's in-memory provider does not support transactions,
/// so the service tests that use it would prove nothing here: the rollback would be a no-op and the
/// test would pass whether or not the fix existed. That is the trap this audit already recorded for
/// #233 and #234, both of which shipped with tests that could not fail.</para>
///
/// <para>The failure is provoked the way it would actually happen: the audit row carries a
/// <c>UserId</c> foreign key, and real SQLite enforces it. A user id that does not exist makes the
/// <i>second</i> save throw, which is precisely the window under test.</para>
/// </summary>
public class AtomicCreateSqliteTests
{
    private static readonly DateTime Now = new(2026, 8, 11, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private static async Task<(SqliteConnection Connection, AppDbContext Context)> CreateAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var dbContext = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await dbContext.Database.EnsureCreatedAsync();
        return (connection, dbContext);
    }

    /// <summary>A user id no row has, so the audit insert violates its foreign key.</summary>
    private const int NonexistentUserId = 999_999;

    [Fact]
    public async Task VecCreate_WhenTheAuditSaveFails_LeavesNoVecBehind()
    {
        var (connection, dbContext) = await CreateAsync();
        await using var _ = connection;
        await using var __ = dbContext;

        var service = new VecManagementService(dbContext, new FixedTimeProvider(Now));

        await Assert.ThrowsAnyAsync<Exception>(() =>
            service.CreateAsync("ARRL", "arrl", supportsYouthProgram: true, notes: null,
                NonexistentUserId, CancellationToken.None));

        // Without the transaction the Vec is committed by the first save and survives the second's
        // failure — a VEC that exists with nothing recording who created it.
        dbContext.ChangeTracker.Clear();
        Assert.Empty(await dbContext.Vecs.AsNoTracking().ToListAsync());
        Assert.Empty(await dbContext.AuditLogs.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// The one where a lost audit row is the lesser problem: the team's EmailSettings and default
    /// templates are seeded <i>between</i> the two saves, so a partial commit leaves a team that is
    /// silently non-functional for email — the exact state that seeding was moved into
    /// <c>CreateAsync</c> to prevent, and one the Web process does not self-heal from.
    /// </summary>
    [Fact]
    public async Task TeamCreate_WhenTheAuditSaveFails_LeavesNoTeamAndNoHalfSeededEmailSetup()
    {
        var (connection, dbContext) = await CreateAsync();
        await using var _ = connection;
        await using var __ = dbContext;

        var service = new TeamSettingsService(dbContext, new FixedTimeProvider(Now),
            NullLogger<TeamSettingsService>.Instance);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            service.CreateAsync("HRCC", NonexistentUserId, CancellationToken.None));

        dbContext.ChangeTracker.Clear();
        Assert.Empty(await dbContext.Teams.AsNoTracking().ToListAsync());
        Assert.Empty(await dbContext.EmailSettings.AsNoTracking().ToListAsync());
        Assert.Empty(await dbContext.EmailTemplates.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// The success path still commits everything — a rollback guard that also rolled back valid work
    /// would be worse than the gap it closes.
    /// </summary>
    [Fact]
    public async Task TeamCreate_OnSuccess_CommitsTheTeamItsEmailSettingsAndItsAudit()
    {
        var (connection, dbContext) = await CreateAsync();
        await using var _ = connection;
        await using var __ = dbContext;

        var user = new User { Name = "Admin", Email = "admin@localhost", Role = UserRole.SystemAdmin };
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();

        var service = new TeamSettingsService(dbContext, new FixedTimeProvider(Now),
            NullLogger<TeamSettingsService>.Instance);

        var (result, team) = await service.CreateAsync("HRCC", user.Id, CancellationToken.None);

        Assert.Equal(TeamActionResult.Success, result);
        Assert.NotNull(team);

        dbContext.ChangeTracker.Clear();
        Assert.Single(await dbContext.Teams.AsNoTracking().ToListAsync());
        Assert.NotEmpty(await dbContext.EmailTemplates.AsNoTracking().ToListAsync());
        Assert.Contains(await dbContext.AuditLogs.AsNoTracking().ToListAsync(), a => a.Action == "TeamCreated");
    }
}
