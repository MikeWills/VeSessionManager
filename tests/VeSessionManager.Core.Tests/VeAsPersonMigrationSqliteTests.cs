using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using VeSessionManager.Core.Data;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// The VeAsPersonWithTeamMemberships data migration — the one thing in issue #142 that can destroy
/// data rather than merely misbehave. It merges per-team VE rows into people and repoints every
/// session link, and there is no undo once it has run against a real database.
///
/// <para><b>EF InMemory cannot test any of this.</b> It does not execute migrations at all, so the
/// merge SQL would be entirely unexercised by the rest of the suite. Everything here runs the real
/// upgrade path against real SQLite — migrate to the migration *before* this one, seed the old
/// per-team shape with raw SQL, then apply the rest — following <see cref="PaymentUniqueIndexSqliteTests"/>.</para>
///
/// <para>Rows are seeded with raw SQL naming only the columns that existed at
/// <see cref="MigrationBeforeTheMerge"/>, for the reason that file documents at length: the
/// DbContext is always the *current* model, so seeding through EF against a deliberately old schema
/// fails the moment anyone adds a column.</para>
/// </summary>
public class VeAsPersonMigrationSqliteTests
{
    private const string MigrationBeforeTheMerge = "20260807152640_SessionVeRosterFinalSynced";

    private static readonly DateTime Now = new(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc);

    private static async Task<(SqliteConnection Connection, AppDbContext Context)> MigrateToOldSchemaAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var dbContext = new AppDbContext(options);
        await dbContext.GetService<IMigrator>().MigrateAsync(MigrationBeforeTheMerge);
        return (connection, dbContext);
    }

    private static async Task<int> SeedTeamAsync(AppDbContext dbContext, string name)
    {
        await dbContext.Database.ExecuteSqlRawAsync(
            "INSERT INTO Teams (Name, CreatedUtc, PurgeUnpaidLinkDays, ZoomBreakoutRoomCount) VALUES ({0}, {1}, 0, 0)",
            name, Now);
        return await dbContext.Teams.Select(t => t.Id).OrderByDescending(id => id).FirstAsync();
    }

    /// <summary>Seeds Vec + User + FeeConfiguration + Session and returns the session id.</summary>
    private static async Task<int> SeedSessionAsync(AppDbContext dbContext, int teamId, string key)
    {
        await dbContext.Database.ExecuteSqlRawAsync(
            "INSERT INTO Vecs (Name, SupportsYouthProgram) VALUES ({0}, 0)", $"VEC-{key}");
        var vecId = await dbContext.Vecs.Select(v => v.Id).OrderByDescending(id => id).FirstAsync();

        await dbContext.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO AspNetUsers
                (UserName, NormalizedUserName, Email, NormalizedEmail, EmailConfirmed, Name, Role,
                 PhoneNumberConfirmed, TwoFactorEnabled, LockoutEnabled, AccessFailedCount, SecurityStamp, ConcurrencyStamp, MustChangePassword)
            VALUES ({0}, {1}, {0}, {1}, 1, 'System', 0, 0, 0, 0, 0, {2}, {2}, 0)
            """,
            $"system-{key}@localhost", $"SYSTEM-{key.ToUpperInvariant()}@LOCALHOST", Guid.NewGuid().ToString());
        var userId = await dbContext.Users.Select(u => u.Id).OrderByDescending(id => id).FirstAsync();

        await dbContext.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO FeeConfigurations (VecId, EffectiveDate, FeeCollectionEnabled, ExamFeeAmount, CreatedByUserId, CreatedUtc)
            VALUES ({0}, {1}, 1, '15.0', {2}, {3})
            """,
            vecId, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), userId, Now);
        var feeId = await dbContext.FeeConfigurations.Select(f => f.Id).OrderByDescending(id => id).FirstAsync();

        await dbContext.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO Sessions
                (ExamToolsSessionId, Title, ScheduledStartUtc, DurationMinutes, TeamId, VecId,
                 FeeConfigurationId, Status, VecSubmissionStatus, RescheduleFlaggedForReview, CreatedUtc)
            VALUES ({0}, 'Session', {1}, 60, {2}, {3}, {4}, 0, 0, 0, {5})
            """,
            $"session-{key}", Now.AddDays(-7), teamId, vecId, feeId, Now);

        return await dbContext.Sessions.Select(s => s.Id).OrderByDescending(id => id).FirstAsync();
    }

    private static async Task<int> SeedOldShapeVeAsync(AppDbContext dbContext, int teamId, string? callSign, string name)
    {
        // A literal NULL rather than a parameter: a bare null in a params object[] is the null
        // *array*, and DBNull has no store type mapping in this provider.
        if (callSign is null)
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "INSERT INTO VolunteerExaminers (Name, CallSign, TeamId) VALUES ({0}, NULL, {1})", name, teamId);
        }
        else
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "INSERT INTO VolunteerExaminers (Name, CallSign, TeamId) VALUES ({0}, {1}, {2})", name, callSign, teamId);
        }

        return (await dbContext.Database.SqlQuery<int>($"SELECT MAX(Id) AS Value FROM VolunteerExaminers").ToListAsync())[0];
    }

    private static Task LinkAsync(AppDbContext dbContext, int sessionId, int veId) =>
        dbContext.Database.ExecuteSqlRawAsync(
            "INSERT INTO SessionVolunteerExaminers (SessionId, VolunteerExaminerId) VALUES ({0}, {1})", sessionId, veId);

    /// <summary>
    /// The case the whole migration exists for: the same human on two teams, stored twice because
    /// the old schema had no way to say they were one person.
    /// </summary>
    [Fact]
    public async Task SameCallSignAndNameOnTwoTeams_MergesToOnePersonWithTwoMemberships()
    {
        var (connection, dbContext) = await MigrateToOldSchemaAsync();
        await using var _ = connection;
        await using var __ = dbContext;

        var teamA = await SeedTeamAsync(dbContext, "TEAM-A");
        var teamB = await SeedTeamAsync(dbContext, "TEAM-B");
        var sessionA = await SeedSessionAsync(dbContext, teamA, "a");
        var sessionB = await SeedSessionAsync(dbContext, teamB, "b");
        var veA = await SeedOldShapeVeAsync(dbContext, teamA, "N2SPG", "Sam Granger");
        var veB = await SeedOldShapeVeAsync(dbContext, teamB, "N2SPG", "Sam Granger");
        await LinkAsync(dbContext, sessionA, veA);
        await LinkAsync(dbContext, sessionB, veB);

        await dbContext.Database.MigrateAsync();
        dbContext.ChangeTracker.Clear();

        var person = Assert.Single(await dbContext.VolunteerExaminers.ToListAsync());
        Assert.Equal("N2SPG", person.CallSign);

        var memberships = await dbContext.VeTeamMemberships.ToListAsync();
        Assert.Equal(2, memberships.Count);
        Assert.Contains(memberships, m => m.TeamId == teamA && m.VolunteerExaminerId == person.Id);
        Assert.Contains(memberships, m => m.TeamId == teamB && m.VolunteerExaminerId == person.Id);
        Assert.All(memberships, m => Assert.True(m.IsActive));

        // Both appearances survive and now belong to the one person — this is the session-count
        // history the merge must not lose.
        var links = await dbContext.SessionVolunteerExaminers.ToListAsync();
        Assert.Equal(2, links.Count);
        Assert.All(links, l => Assert.Equal(person.Id, l.VolunteerExaminerId));
    }

    /// <summary>
    /// The conservative half. A call sign released and reissued to a different person is real, and
    /// merging two humans cannot be undone once their session links are repointed — so a name
    /// mismatch means no merge, both rows survive sharing a call sign, and a human decides.
    /// </summary>
    [Fact]
    public async Task SameCallSignButDifferentNames_AreLeftAsTwoPeople()
    {
        var (connection, dbContext) = await MigrateToOldSchemaAsync();
        await using var _ = connection;
        await using var __ = dbContext;

        var teamA = await SeedTeamAsync(dbContext, "TEAM-A");
        var teamB = await SeedTeamAsync(dbContext, "TEAM-B");
        await SeedOldShapeVeAsync(dbContext, teamA, "N2SPG", "Sam Granger");
        await SeedOldShapeVeAsync(dbContext, teamB, "N2SPG", "Someone Else");

        await dbContext.Database.MigrateAsync();
        dbContext.ChangeTracker.Clear();

        Assert.Equal(2, await dbContext.VolunteerExaminers.CountAsync());
        Assert.Equal(2, await dbContext.VeTeamMemberships.CountAsync());
    }

    /// <summary>Case and surrounding whitespace must not defeat the match — ExamTools' feed is not consistent about either.</summary>
    [Fact]
    public async Task MatchIgnoresCaseAndWhitespace()
    {
        var (connection, dbContext) = await MigrateToOldSchemaAsync();
        await using var _ = connection;
        await using var __ = dbContext;

        var teamA = await SeedTeamAsync(dbContext, "TEAM-A");
        var teamB = await SeedTeamAsync(dbContext, "TEAM-B");
        await SeedOldShapeVeAsync(dbContext, teamA, "N2SPG", "Sam Granger");
        await SeedOldShapeVeAsync(dbContext, teamB, "n2spg", "  sam granger ");

        await dbContext.Database.MigrateAsync();
        dbContext.ChangeTracker.Clear();

        Assert.Single(await dbContext.VolunteerExaminers.ToListAsync());
        Assert.Equal(2, await dbContext.VeTeamMemberships.CountAsync());
    }

    /// <summary>
    /// A VE with no call sign is possible today — ExamTools' roster can name one without. There is
    /// nothing to match on, so they must pass through untouched rather than being merged into
    /// whichever other row also lacks one.
    /// </summary>
    [Fact]
    public async Task VesWithNoCallSign_AreNeverMergedWithEachOther()
    {
        var (connection, dbContext) = await MigrateToOldSchemaAsync();
        await using var _ = connection;
        await using var __ = dbContext;

        var teamA = await SeedTeamAsync(dbContext, "TEAM-A");
        var teamB = await SeedTeamAsync(dbContext, "TEAM-B");
        await SeedOldShapeVeAsync(dbContext, teamA, null, "No Callsign One");
        await SeedOldShapeVeAsync(dbContext, teamB, null, "No Callsign Two");

        await dbContext.Database.MigrateAsync();
        dbContext.ChangeTracker.Clear();

        Assert.Equal(2, await dbContext.VolunteerExaminers.CountAsync());
        Assert.Equal(2, await dbContext.VeTeamMemberships.CountAsync());
    }

    /// <summary>
    /// The trap that made EF's own generated migration dangerous: TeamId and OperatorClass are both
    /// ints, and EF turned the drop into a rename. Every VE would have come out of the migration
    /// with an operator class equal to their old team id — "team 2" silently becoming "class 2".
    /// </summary>
    [Fact]
    public async Task OldTeamIdIsNotCarriedIntoOperatorClass()
    {
        var (connection, dbContext) = await MigrateToOldSchemaAsync();
        await using var _ = connection;
        await using var __ = dbContext;

        // Several teams, so the surviving VE's team id is definitely not zero.
        await SeedTeamAsync(dbContext, "TEAM-A");
        await SeedTeamAsync(dbContext, "TEAM-B");
        var teamC = await SeedTeamAsync(dbContext, "TEAM-C");
        await SeedOldShapeVeAsync(dbContext, teamC, "N2SPG", "Sam Granger");

        await dbContext.Database.MigrateAsync();
        dbContext.ChangeTracker.Clear();

        var person = Assert.Single(await dbContext.VolunteerExaminers.ToListAsync());
        Assert.Equal(Entities.LicenseClass.None, person.OperatorClass);
        Assert.Equal(teamC, Assert.Single(await dbContext.VeTeamMemberships.ToListAsync()).TeamId);
    }

    /// <summary>
    /// The bug real data found and every test above missed (2026-08-07). ExamTools reports the
    /// literal "&lt;UNKNOWN&gt;" when it has no call sign, so HRCC's unidentified VE and MARC's
    /// shared a "call sign" and a name, and were merged into one person carrying 88 sessions of both
    /// their histories. Every other test here uses realistic call signs and sails straight past it.
    /// </summary>
    [Fact]
    public async Task PlaceholderCallSigns_AreNeverMergedAcrossTeams()
    {
        var (connection, dbContext) = await MigrateToOldSchemaAsync();
        await using var _ = connection;
        await using var __ = dbContext;

        var teamA = await SeedTeamAsync(dbContext, "TEAM-A");
        var teamB = await SeedTeamAsync(dbContext, "TEAM-B");
        var sessionA = await SeedSessionAsync(dbContext, teamA, "a");
        var sessionB = await SeedSessionAsync(dbContext, teamB, "b");
        var unknownA = await SeedOldShapeVeAsync(dbContext, teamA, "<UNKNOWN>", "<UNKNOWN>");
        var unknownB = await SeedOldShapeVeAsync(dbContext, teamB, "<UNKNOWN>", "<UNKNOWN>");
        await LinkAsync(dbContext, sessionA, unknownA);
        await LinkAsync(dbContext, sessionB, unknownB);

        await dbContext.Database.MigrateAsync();
        dbContext.ChangeTracker.Clear();

        Assert.Equal(2, await dbContext.VolunteerExaminers.CountAsync());

        var links = await dbContext.SessionVolunteerExaminers.ToListAsync();
        Assert.Equal(2, links.Count);
        Assert.Equal(2, links.Select(l => l.VolunteerExaminerId).Distinct().Count());
    }

    /// <summary>
    /// The repair for databases that already ran the broken merge. Splitting merged people is
    /// normally impossible, but here it is fully determined: each pre-merge row was one team's, and
    /// every session belongs to exactly one team.
    /// </summary>
    [Fact]
    public async Task AlreadyMergedPlaceholders_AreSplitBackByTeam()
    {
        var (connection, dbContext) = await MigrateToOldSchemaAsync();
        await using var _ = connection;
        await using var __ = dbContext;

        var teamA = await SeedTeamAsync(dbContext, "TEAM-A");
        var teamB = await SeedTeamAsync(dbContext, "TEAM-B");
        var sessionA = await SeedSessionAsync(dbContext, teamA, "a");
        var sessionB = await SeedSessionAsync(dbContext, teamB, "b");
        var unknownA = await SeedOldShapeVeAsync(dbContext, teamA, "<UNKNOWN>", "<UNKNOWN>");
        var unknownB = await SeedOldShapeVeAsync(dbContext, teamB, "<UNKNOWN>", "<UNKNOWN>");
        await LinkAsync(dbContext, sessionA, unknownA);
        await LinkAsync(dbContext, sessionB, unknownB);

        await dbContext.Database.MigrateAsync();
        dbContext.ChangeTracker.Clear();

        // Reproduce the damaged state by hand: one person holding both teams' memberships and links.
        await dbContext.Database.ExecuteSqlRawAsync(
            "UPDATE SessionVolunteerExaminers SET VolunteerExaminerId = {0} WHERE VolunteerExaminerId = {1}", unknownA, unknownB);
        await dbContext.Database.ExecuteSqlRawAsync(
            "UPDATE VeTeamMemberships SET VolunteerExaminerId = {0} WHERE VolunteerExaminerId = {1}", unknownA, unknownB);
        await dbContext.Database.ExecuteSqlRawAsync(
            "DELETE FROM VolunteerExaminers WHERE Id = {0}", unknownB);
        dbContext.ChangeTracker.Clear();
        Assert.Single(await dbContext.VolunteerExaminers.ToListAsync());

        await RunSplitRepairAsync(dbContext);
        dbContext.ChangeTracker.Clear();

        Assert.Equal(2, await dbContext.VolunteerExaminers.CountAsync());
        var memberships = await dbContext.VeTeamMemberships.ToListAsync();
        Assert.Equal(2, memberships.Select(m => m.VolunteerExaminerId).Distinct().Count());

        // Each session went back to the person belonging to its own team.
        foreach (var link in await dbContext.SessionVolunteerExaminers.Include(l => l.Session).ToListAsync())
        {
            var membership = memberships.Single(m => m.VolunteerExaminerId == link.VolunteerExaminerId);
            Assert.Equal(link.Session.TeamId, membership.TeamId);
        }

        // The marker used to correlate the new ids must not be left behind in a user-visible field.
        Assert.All(await dbContext.VolunteerExaminers.ToListAsync(), v => Assert.Null(v.Notes));
    }

    /// <summary>A real call sign shared by one person across two teams must stay merged — the repair must not undo the feature.</summary>
    [Fact]
    public async Task SplitRepair_LeavesRealCallSignsMerged()
    {
        var (connection, dbContext) = await MigrateToOldSchemaAsync();
        await using var _ = connection;
        await using var __ = dbContext;

        var teamA = await SeedTeamAsync(dbContext, "TEAM-A");
        var teamB = await SeedTeamAsync(dbContext, "TEAM-B");
        await SeedOldShapeVeAsync(dbContext, teamA, "N2SPG", "Sam Granger");
        await SeedOldShapeVeAsync(dbContext, teamB, "N2SPG", "Sam Granger");

        await dbContext.Database.MigrateAsync();
        await RunSplitRepairAsync(dbContext);
        dbContext.ChangeTracker.Clear();

        Assert.Single(await dbContext.VolunteerExaminers.ToListAsync());
        Assert.Equal(2, await dbContext.VeTeamMemberships.CountAsync());
    }

    /// <summary>
    /// Replays the repair migration's statements. MigrateAsync has already applied it and a
    /// migration never runs twice, so exercising it against a hand-damaged database means running
    /// its SQL directly. Kept character-for-character in step with the migration.
    /// </summary>
    private static async Task RunSplitRepairAsync(AppDbContext dbContext)
    {
        await dbContext.Database.ExecuteSqlRawAsync("""
            CREATE TEMPORARY TABLE _ve_split AS
            SELECT m.Id AS MembershipId, m.VolunteerExaminerId AS OldVeId, m.TeamId
            FROM VeTeamMemberships m
            JOIN VolunteerExaminers v ON v.Id = m.VolunteerExaminerId
            WHERE v.CallSign IS NOT NULL
              AND v.CallSign GLOB '*[^A-Za-z0-9/]*'
              AND m.TeamId <> (SELECT MIN(m2.TeamId) FROM VeTeamMemberships m2
                                WHERE m2.VolunteerExaminerId = m.VolunteerExaminerId);
            """);
        await dbContext.Database.ExecuteSqlRawAsync("ALTER TABLE _ve_split ADD COLUMN NewVeId INTEGER;");
        await dbContext.Database.ExecuteSqlRawAsync("""
            INSERT INTO VolunteerExaminers (Name, CallSign, ContactPreference, LicenseNotFoundAtFcc, OperatorClass, CreatedUtc, Notes)
            SELECT v.Name, v.CallSign, v.ContactPreference, 0, 0,
                   strftime('%Y-%m-%d %H:%M:%f', 'now'), '_ve_split_' || s.MembershipId
            FROM _ve_split s JOIN VolunteerExaminers v ON v.Id = s.OldVeId;
            """);
        await dbContext.Database.ExecuteSqlRawAsync("""
            UPDATE _ve_split SET NewVeId = (SELECT v.Id FROM VolunteerExaminers v
                                             WHERE v.Notes = '_ve_split_' || _ve_split.MembershipId);
            """);
        await dbContext.Database.ExecuteSqlRawAsync("""
            UPDATE VeTeamMemberships
               SET VolunteerExaminerId = (SELECT s.NewVeId FROM _ve_split s WHERE s.MembershipId = VeTeamMemberships.Id)
             WHERE Id IN (SELECT MembershipId FROM _ve_split WHERE NewVeId IS NOT NULL);
            """);
        await dbContext.Database.ExecuteSqlRawAsync("""
            UPDATE SessionVolunteerExaminers
               SET VolunteerExaminerId = (
                    SELECT s.NewVeId FROM _ve_split s
                     JOIN Sessions ses ON ses.Id = SessionVolunteerExaminers.SessionId
                    WHERE s.OldVeId = SessionVolunteerExaminers.VolunteerExaminerId
                      AND s.TeamId = ses.TeamId AND s.NewVeId IS NOT NULL)
             WHERE EXISTS (
                    SELECT 1 FROM _ve_split s
                     JOIN Sessions ses ON ses.Id = SessionVolunteerExaminers.SessionId
                    WHERE s.OldVeId = SessionVolunteerExaminers.VolunteerExaminerId
                      AND s.TeamId = ses.TeamId AND s.NewVeId IS NOT NULL);
            """);
        await dbContext.Database.ExecuteSqlRawAsync("UPDATE VolunteerExaminers SET Notes = NULL WHERE Notes LIKE '_ve_split_%';");
        await dbContext.Database.ExecuteSqlRawAsync("DROP TABLE _ve_split;");
    }

    /// <summary>CreatedUtc is a new non-null column; every migrated row must come out with a real date rather than 0001-01-01.</summary>
    [Fact]
    public async Task MigratedPeople_GetARealCreatedDate()
    {
        var (connection, dbContext) = await MigrateToOldSchemaAsync();
        await using var _ = connection;
        await using var __ = dbContext;

        var team = await SeedTeamAsync(dbContext, "TEAM-A");
        await SeedOldShapeVeAsync(dbContext, team, "N2SPG", "Sam Granger");

        await dbContext.Database.MigrateAsync();
        dbContext.ChangeTracker.Clear();

        var person = Assert.Single(await dbContext.VolunteerExaminers.ToListAsync());
        Assert.True(person.CreatedUtc > new DateTime(2000, 1, 1), $"CreatedUtc was {person.CreatedUtc:o}");
    }
}
