using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.VolunteerExaminers;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Mapping a team's VE tag to a Discord role (#519, step 1) — the configuration the tag sync will
/// later read. Nothing here syncs anything; this is only the map.
///
/// <para><b>The map lives on <see cref="VeTag"/> rather than in a table of its own.</b> A tag is
/// already a team's own vocabulary with its own screen, and "which Discord role means this tag" is a
/// property of that vocabulary entry — a separate entity would need its own team scoping, its own
/// uniqueness rules and its own screen to say the same thing.</para>
/// </summary>
public class VeTagDiscordRoleTests
{
    private static readonly DateTime Now = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    private const ulong TeamMemberRole = 1170000000000000001;
    private const ulong SessionManagerRole = 1170000000000000002;

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static VolunteerExaminerManagementService CreateService(AppDbContext dbContext) =>
        new(dbContext, new FixedTimeProvider(Now));

    private static async Task<Team> SeedTeamAsync(AppDbContext dbContext, string name = "HRCC")
    {
        var team = new Team { Name = name };
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        return team;
    }

    // ---- setting the map ------------------------------------------------------------------------

    [Fact]
    public async Task ATagCanBeCreatedAlreadyMappedToADiscordRole()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var service = CreateService(dbContext);

        var (result, tag) = await service.CreateTagAsync(
            team.Id, "Team member", 0, null, TeamMemberRole, "Team Member", 1, CancellationToken.None);

        Assert.Equal(VeManagementResult.Success, result);
        Assert.Equal(TeamMemberRole, tag!.DiscordRoleId);
        Assert.Equal("Team Member", tag.DiscordRoleName);
    }

    /// <summary>
    /// The name is a snapshot, not the link. Discord roles get renamed, and the screen has to be able
    /// to say which role a tag is mapped to when the bot cannot reach Discord at all — the same
    /// snapshot-on-the-record reasoning as Payment.CandidateNameSnapshot.
    /// </summary>
    [Fact]
    public async Task TheRoleNameIsStoredAsASnapshotBesideTheId()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var service = CreateService(dbContext);
        var (_, tag) = await service.CreateTagAsync(
            team.Id, "Team member", 0, null, TeamMemberRole, "Team Member", 1, CancellationToken.None);

        await service.UpdateTagAsync(tag!.Id, "Team member", 0, null, TeamMemberRole, "Full Members", 1, CancellationToken.None);

        var stored = await dbContext.VeTags.SingleAsync();
        Assert.Equal(TeamMemberRole, stored.DiscordRoleId);
        Assert.Equal("Full Members", stored.DiscordRoleName);
    }

    /// <summary>
    /// UpdateTagAsync early-returns when nothing changed, and that check is a list of the fields it
    /// knows about. A role-only edit has to be in that list or it silently does nothing — the tag
    /// would report success and stay unmapped.
    /// </summary>
    [Fact]
    public async Task MappingAnExistingTagIsSaved_EvenThoughNothingElseChanged()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var service = CreateService(dbContext);
        var (_, tag) = await service.CreateTagAsync(team.Id, "Team member", 0, null, null, null, 1, CancellationToken.None);

        var result = await service.UpdateTagAsync(tag!.Id, "Team member", 0, null, TeamMemberRole, "Team Member", 1, CancellationToken.None);

        Assert.Equal(VeManagementResult.Success, result);
        Assert.Equal(TeamMemberRole, (await dbContext.VeTags.SingleAsync()).DiscordRoleId);
    }

    /// <summary>Unmapping is what turns the sync off for one tag, so it has to be reachable — and it is a change, not a no-op.</summary>
    [Fact]
    public async Task ATagCanBeUnmapped()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var service = CreateService(dbContext);
        var (_, tag) = await service.CreateTagAsync(
            team.Id, "Team member", 0, null, TeamMemberRole, "Team Member", 1, CancellationToken.None);

        var result = await service.UpdateTagAsync(tag!.Id, "Team member", 0, null, null, null, 1, CancellationToken.None);

        Assert.Equal(VeManagementResult.Success, result);
        var stored = await dbContext.VeTags.SingleAsync();
        Assert.Null(stored.DiscordRoleId);
        Assert.Null(stored.DiscordRoleName);
    }

    // ---- one role, one tag, per team ------------------------------------------------------------

    /// <summary>
    /// Two tags on one role would make the sync ambiguous to read even though it is well defined to
    /// run ("both apply"), and an admin reading the tag screen could not tell which mapping was
    /// intended. Rejected at the service, and by an index below.
    /// </summary>
    [Fact]
    public async Task TheSameRoleCannotBeMappedToTwoTagsOnOneTeam()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var service = CreateService(dbContext);
        await service.CreateTagAsync(team.Id, "Team member", 0, null, TeamMemberRole, "Team Member", 1, CancellationToken.None);

        var (result, tag) = await service.CreateTagAsync(
            team.Id, "Auditioning", 1, null, TeamMemberRole, "Team Member", 1, CancellationToken.None);

        Assert.Equal(VeManagementResult.DuplicateDiscordRole, result);
        Assert.Null(tag);
    }

    [Fact]
    public async Task TheSameRoleCannotBeMappedToASecondTagByEditing()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var service = CreateService(dbContext);
        await service.CreateTagAsync(team.Id, "Team member", 0, null, TeamMemberRole, "Team Member", 1, CancellationToken.None);
        var (_, other) = await service.CreateTagAsync(team.Id, "Auditioning", 1, null, null, null, 1, CancellationToken.None);

        var result = await service.UpdateTagAsync(other!.Id, "Auditioning", 1, null, TeamMemberRole, "Team Member", 1, CancellationToken.None);

        Assert.Equal(VeManagementResult.DuplicateDiscordRole, result);
    }

    /// <summary>Re-saving a tag that already holds the role is an edit of that tag, not a duplicate of itself.</summary>
    [Fact]
    public async Task ATagKeepingItsOwnRoleIsNotADuplicate()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var service = CreateService(dbContext);
        var (_, tag) = await service.CreateTagAsync(
            team.Id, "Team member", 0, null, TeamMemberRole, "Team Member", 1, CancellationToken.None);

        var result = await service.UpdateTagAsync(tag!.Id, "Full member", 0, null, TeamMemberRole, "Team Member", 1, CancellationToken.None);

        Assert.Equal(VeManagementResult.Success, result);
    }

    /// <summary>
    /// Tags are a team's own vocabulary, and two teams can share one Discord server. The uniqueness is
    /// per team for the same reason the tag names are.
    /// </summary>
    [Fact]
    public async Task TwoTeamsCanMapTheSameRole()
    {
        await using var dbContext = CreateContext();
        var hrcc = await SeedTeamAsync(dbContext);
        var marc = await SeedTeamAsync(dbContext, "MARC");
        var service = CreateService(dbContext);
        await service.CreateTagAsync(hrcc.Id, "Team member", 0, null, TeamMemberRole, "Team Member", 1, CancellationToken.None);

        var (result, _) = await service.CreateTagAsync(
            marc.Id, "Team member", 0, null, TeamMemberRole, "Team Member", 1, CancellationToken.None);

        Assert.Equal(VeManagementResult.Success, result);
    }

    // ---- the index behind the check -------------------------------------------------------------

    private static async Task<(SqliteConnection Connection, AppDbContext Context)> CreateSqliteAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var dbContext = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await dbContext.Database.EnsureCreatedAsync();
        return (connection, dbContext);
    }

    private static VeTag Tag(int teamId, string name, ulong? roleId) =>
        new() { TeamId = teamId, Name = name, DiscordRoleId = roleId, CreatedUtc = Now };

    /// <summary>
    /// Real SQLite, necessarily: InMemory enforces no unique index at all, so the service check above
    /// would look like the whole guard however this was written.
    /// </summary>
    [Fact]
    public async Task TheDatabaseRefusesASecondTagOnTheSameRole()
    {
        var (connection, dbContext) = await CreateSqliteAsync();
        await using var _ = connection;
        await using var __ = dbContext;
        var team = await SeedTeamAsync(dbContext);

        dbContext.VeTags.Add(Tag(team.Id, "Team member", TeamMemberRole));
        await dbContext.SaveChangesAsync();

        dbContext.VeTags.Add(Tag(team.Id, "Auditioning", TeamMemberRole));
        await Assert.ThrowsAsync<DbUpdateException>(() => dbContext.SaveChangesAsync());
    }

    /// <summary>
    /// The case that makes the index safe to add at all: most tags are unmapped, and SQLite treats
    /// NULLs in a unique index as distinct. If it did not, a team could have exactly one unmapped tag
    /// — which is the failure this test exists to catch, and one InMemory cannot show.
    /// </summary>
    [Fact]
    public async Task AnyNumberOfTagsMayBeUnmapped()
    {
        var (connection, dbContext) = await CreateSqliteAsync();
        await using var _ = connection;
        await using var __ = dbContext;
        var team = await SeedTeamAsync(dbContext);

        dbContext.VeTags.Add(Tag(team.Id, "Team member", null));
        dbContext.VeTags.Add(Tag(team.Id, "Auditioning", null));
        dbContext.VeTags.Add(Tag(team.Id, "Session manager", null));
        await dbContext.SaveChangesAsync();

        Assert.Equal(3, await dbContext.VeTags.CountAsync());
    }

    /// <summary>A snowflake is a 64-bit unsigned id and SQLite stores signed integers — worth pinning that a real one round-trips rather than silently overflowing.</summary>
    [Fact]
    public async Task ASnowflakeRoleIdRoundTrips()
    {
        var (connection, dbContext) = await CreateSqliteAsync();
        await using var _ = connection;
        await using var __ = dbContext;
        var team = await SeedTeamAsync(dbContext);

        dbContext.VeTags.Add(Tag(team.Id, "Session manager", SessionManagerRole));
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        Assert.Equal(SessionManagerRole, (await dbContext.VeTags.SingleAsync()).DiscordRoleId);
    }
}
