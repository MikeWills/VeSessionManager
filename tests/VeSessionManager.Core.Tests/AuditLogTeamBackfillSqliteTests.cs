using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// The <c>AuditLogTeamAttribution</c> migration's backfill (#86 part 3).
///
/// <para><b>Why this needs a test at all.</b> The rest of that change is C# the compiler checks. The
/// backfill is three hand-written SQL statements with correlated subqueries, and if they resolve
/// nothing the migration still succeeds — every row simply stays null and a TeamAdmin still sees no
/// history. Silent, and indistinguishable from "there was nothing to backfill".</para>
///
/// <para>Real SQLite, and it has to be: the statements are raw SQL, so EF InMemory cannot execute
/// them at all. Same pattern as <see cref="PaymentUniqueIndexSqliteTests"/> — migrate up to the
/// revision <i>before</i> the one under test, seed the shape production actually holds, then apply
/// the rest and assert on the result.</para>
/// </summary>
public class AuditLogTeamBackfillSqliteTests
{
    private static readonly DateTime Now = new(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>The migration immediately before AuditLogTeamAttribution — the state a deployment is in when the backfill runs.</summary>
    private const string MigrationBeforeTheBackfill = "20260815190447_AddRefunds";

    /// <summary>
    /// Every insert here is raw SQL rather than EF. Seeding through the DbSet writes the columns the
    /// <i>current</i> model has, which do not exist yet at the migration this test pins to — the trap
    /// already recorded in <see cref="PaymentUniqueIndexSqliteTests"/>, and the one that broke those
    /// tests when Payment gained a column.
    /// </summary>
    /// <summary>The seeded graph: two teams, a session in each, and a candidate + payment under the first team's session.</summary>
    private sealed record Seed(int TeamAId, int TeamBId, int UserId, int SessionAId, int SessionBId, int CandidateId, int PaymentId);

    private static async Task<Seed> SeedAsync(AppDbContext dbContext)
    {
        // Ids are read back rather than assigned: the Phase6_5MultiTeamFoundation migration already
        // seeds a 'WX0MIK' team at Id 1, so hardcoding low ids collides on the primary key.
        await dbContext.Database.ExecuteSqlRawAsync(
            "INSERT INTO Teams (Name, CreatedUtc, PurgeUnpaidLinkDays, ZoomBreakoutRoomCount) VALUES ('TEAMA', {0}, 30, 0), ('TEAMB', {0}, 30, 0)", Now);
        var teamIds = await dbContext.Teams.Select(t => t.Id).OrderByDescending(id => id).Take(2).ToListAsync();
        var (teamBId, teamAId) = (teamIds[0], teamIds[1]);

        var userId = await PaymentUniqueIndexSqliteTests.SeedUserViaSqlAsync(dbContext, $"system-{Guid.NewGuid():N}@localhost");
        var sessionAId = await PaymentUniqueIndexSqliteTests.SeedSessionViaSqlAsync(dbContext, "audit-a", teamAId, userId);
        var sessionBId = await PaymentUniqueIndexSqliteTests.SeedSessionViaSqlAsync(dbContext, "audit-b", teamBId, userId);
        var candidateId = await PaymentUniqueIndexSqliteTests.SeedCandidateViaSqlAsync(dbContext, "audit-a", sessionAId);

        await dbContext.Database.ExecuteSqlRawAsync(
            "INSERT INTO Payments (CandidateId, Reason, Amount, Status, CreatedUtc, ExpiredUnpaid, RefundRequested) VALUES ({0}, 0, 15, 1, {1}, 0, 0)",
            candidateId, Now);
        var paymentId = await dbContext.Payments.Select(p => p.Id).OrderByDescending(id => id).FirstAsync();

        return new Seed(teamAId, teamBId, userId, sessionAId, sessionBId, candidateId, paymentId);
    }

    private static async Task InsertAuditAsync(AppDbContext dbContext, int id, int? userId, string entityType, int entityId)
    {
        if (userId is null)
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "INSERT INTO AuditLogs (Id, UserId, Action, EntityType, EntityId, TimestampUtc, Details) VALUES ({0}, NULL, 'JobAction', {1}, {2}, {3}, 'x')",
                id, entityType, entityId, Now);
            return;
        }

        await dbContext.Database.ExecuteSqlRawAsync(
            "INSERT INTO AuditLogs (Id, UserId, Action, EntityType, EntityId, TimestampUtc, Details) VALUES ({0}, {1}, 'UserAction', {2}, {3}, {4}, 'x')",
            id, userId.Value, entityType, entityId, Now);
    }

    private static async Task<int?> TeamIdOfAsync(AppDbContext dbContext, int auditId) =>
        (await dbContext.AuditLogs.AsNoTracking().SingleAsync(a => a.Id == auditId)).TeamId;

    [Fact]
    public async Task Migration_BackfillsTheTeamForBackgroundEntriesItCanResolve()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var _ = connection;
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var dbContext = new AppDbContext(options);

        await dbContext.GetService<IMigrator>().MigrateAsync(MigrationBeforeTheBackfill);
        var seed = await SeedAsync(dbContext);

        await InsertAuditAsync(dbContext, 1, userId: null, nameof(Session), seed.SessionAId);    // team A, directly
        await InsertAuditAsync(dbContext, 2, userId: null, nameof(Session), seed.SessionBId);    // team B, directly
        await InsertAuditAsync(dbContext, 3, userId: null, nameof(Candidate), seed.CandidateId); // team A, via its session
        await InsertAuditAsync(dbContext, 4, userId: null, nameof(Payment), seed.PaymentId);     // team A, via its candidate

        await dbContext.Database.MigrateAsync();

        // Team B is asserted alongside team A so the test fails a backfill that resolves *a* team
        // rather than the right one.
        Assert.Equal(seed.TeamAId, await TeamIdOfAsync(dbContext, 1));
        Assert.Equal(seed.TeamBId, await TeamIdOfAsync(dbContext, 2));
        Assert.Equal(seed.TeamAId, await TeamIdOfAsync(dbContext, 3));
        Assert.Equal(seed.TeamAId, await TeamIdOfAsync(dbContext, 4));
    }

    /// <summary>
    /// The rows the backfill deliberately leaves alone. A user-attributed entry already scopes
    /// through the acting user's team memberships — filling this column there as well would make one
    /// question answerable two ways, which is the drift this codebase keeps paying for elsewhere.
    /// </summary>
    [Fact]
    public async Task Migration_LeavesUserAttributedEntriesAlone()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var _ = connection;
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var dbContext = new AppDbContext(options);

        await dbContext.GetService<IMigrator>().MigrateAsync(MigrationBeforeTheBackfill);
        var seed = await SeedAsync(dbContext);
        await InsertAuditAsync(dbContext, 1, userId: seed.UserId, nameof(Session), seed.SessionAId);

        await dbContext.Database.MigrateAsync();

        Assert.Null(await TeamIdOfAsync(dbContext, 1));
    }

    /// <summary>
    /// An entry about something with no single team, or about a row since deleted, resolves to
    /// nothing and stays null rather than the migration failing or guessing. That leaves it
    /// SystemAdmin-only — exactly where it was before this column existed, so the unresolvable case
    /// is no worse off than it started.
    /// </summary>
    [Fact]
    public async Task Migration_LeavesUnresolvableBackgroundEntriesNull()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var _ = connection;
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var dbContext = new AppDbContext(options);

        await dbContext.GetService<IMigrator>().MigrateAsync(MigrationBeforeTheBackfill);
        await SeedAsync(dbContext);

        await InsertAuditAsync(dbContext, 1, userId: null, nameof(VolunteerExaminer), 7);   // global entity, no one team
        await InsertAuditAsync(dbContext, 2, userId: null, nameof(Session), 999_999);       // session since deleted

        await dbContext.Database.MigrateAsync();

        Assert.Null(await TeamIdOfAsync(dbContext, 1));
        Assert.Null(await TeamIdOfAsync(dbContext, 2));
    }
}
