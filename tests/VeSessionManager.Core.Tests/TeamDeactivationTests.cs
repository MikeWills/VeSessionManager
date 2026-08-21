using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Deactivating a team — the reversible half of "I no longer want to monitor this team".
///
/// <para>Mike asked for both this and a hard delete, noting disable is the easier one. They answer
/// different questions: deactivation stops the app <i>acting</i> for a team while keeping everything
/// it knows, and is undoable; deletion is for when the data itself should go.</para>
///
/// <para>⚠️ <b>Deactivation must not hide the team from admin screens.</b> It is not a soft delete.
/// Somebody has to be able to find it to reactivate it, or to decide to delete it — a team that
/// vanished from the list would be unreachable by the only two actions that apply to it.</para>
/// </summary>
public class TeamDeactivationTests
{
    private static readonly DateTime Now = new(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static Team NewTeam(string name = "HRCC") => new() { Name = name, CreatedUtc = Now };

    [Fact]
    public void ANewTeam_IsActive()
        => Assert.True(NewTeam().IsActive);

    [Fact]
    public void ATeamWithADeactivationStamp_IsNotActive()
        => Assert.False(new Team { Name = "HRCC", CreatedUtc = Now, DeactivatedUtc = Now }.IsActive);

    /// <summary>
    /// A timestamp rather than a bool, so the answer to "why did this team stop polling?" carries a
    /// date. The same reasoning as every other <c>...Utc</c> marker in this app: a flag says what, a
    /// stamp says when, and when is what somebody actually asks.
    /// </summary>
    [Fact]
    public async Task Deactivating_RecordsWhen()
    {
        await using var dbContext = CreateContext();
        var team = NewTeam();
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();

        team.DeactivatedUtc = Now;
        await dbContext.SaveChangesAsync();

        Assert.Equal(Now, (await dbContext.Teams.SingleAsync()).DeactivatedUtc);
    }

    /// <summary>Reactivating clears the stamp — this is reversible, which is the whole point of it existing beside delete.</summary>
    [Fact]
    public async Task Reactivating_ClearsTheStamp()
    {
        await using var dbContext = CreateContext();
        var team = NewTeam();
        team.DeactivatedUtc = Now;
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();

        team.DeactivatedUtc = null;
        await dbContext.SaveChangesAsync();

        Assert.True((await dbContext.Teams.SingleAsync()).IsActive);
    }

    /// <summary>
    /// The query the background jobs use. Both team-enumerating jobs must agree on it, which is why it
    /// is one expression on the entity rather than a predicate each job writes for itself — the two
    /// sites drifting is exactly how a deactivated team would keep polling from one of them.
    /// </summary>
    [Fact]
    public async Task TheActiveQuery_ReturnsOnlyActiveTeams()
    {
        await using var dbContext = CreateContext();
        dbContext.Teams.Add(NewTeam("Active"));
        dbContext.Teams.Add(new Team { Name = "Retired", CreatedUtc = Now, DeactivatedUtc = Now });
        await dbContext.SaveChangesAsync();

        var names = await dbContext.Teams.Where(Team.IsActiveExpression).Select(t => t.Name).ToListAsync();

        Assert.Equal(["Active"], names);
    }

    /// <summary>
    /// ⚠️ Not a soft delete. An admin listing teams still sees a deactivated one, or the only two
    /// actions that apply to it — reactivate, delete — become unreachable.
    /// </summary>
    [Fact]
    public async Task AnAdminListingTeams_StillSeesADeactivatedOne()
    {
        await using var dbContext = CreateContext();
        dbContext.Teams.Add(new Team { Name = "Retired", CreatedUtc = Now, DeactivatedUtc = Now });
        await dbContext.SaveChangesAsync();

        Assert.Single(await dbContext.Teams.ToListAsync());
    }
}
