using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Discord;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.VolunteerExaminers;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Applying a Discord tag sync plan (#519 step 3) — the only part of this feature that writes.
///
/// <para><b>Apply re-reads Discord and writes that result</b>, rather than replaying the plan the
/// screen showed. A preview is a photograph: a role revoked in the seconds between looking and
/// clicking would otherwise be applied as though it were still held. Anything that differs from what
/// was previewed is reported back, so the person who clicked learns their picture was out of date
/// rather than silently getting a different outcome.</para>
/// </summary>
public class DiscordTagSyncApplyTests
{
    private static readonly DateTime Now = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    private const ulong Guild = 900000000000000001;
    private const ulong MemberRole = 1170000000000000001;
    private const int ActingUserId = 1;

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private sealed class FakeGuildClient : IDiscordGuildClient
    {
        public List<DiscordGuildMember> Members { get; } = [];
        public bool Throws { get; set; }
        public bool IsConfigured => true;

        public Task<IReadOnlyList<DiscordRoleSummary>> ListRolesAsync(ulong guildId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DiscordRoleSummary>>([new(MemberRole, "Team Member")]);

        public Task<IReadOnlyList<DiscordGuildMember>> ListMembersAsync(ulong guildId, CancellationToken cancellationToken) =>
            Throws
                ? Task.FromException<IReadOnlyList<DiscordGuildMember>>(new InvalidOperationException("no"))
                : Task.FromResult<IReadOnlyList<DiscordGuildMember>>([.. Members]);
    }

    private static DiscordGuildMember Member(ulong id, string displayName, params ulong[] roles) =>
        new(id, $"user{id}", displayName, displayName, roles);

    private sealed record World(AppDbContext Db, FakeGuildClient Client, Team Team, DiscordTagSyncService Service, VeTag MemberTag);

    private static async Task<World> SeedAsync()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var client = new FakeGuildClient();

        var team = new Team { Name = "HRCC", DiscordGuildId = Guild };
        db.Teams.Add(team);
        db.Users.Add(new User { Id = ActingUserId, Name = "Admin", Email = "admin@example.org", Role = UserRole.SystemAdmin });
        await db.SaveChangesAsync();

        var tag = new VeTag { TeamId = team.Id, Name = "Team member", DiscordRoleId = MemberRole, DiscordRoleName = "Team Member", CreatedUtc = Now };
        db.VeTags.Add(tag);
        await db.SaveChangesAsync();

        var service = new DiscordTagSyncService(db, client, new FixedTimeProvider(Now), NullLogger<DiscordTagSyncService>.Instance);
        return new World(db, client, team, service, tag);
    }

    private static async Task<VeTeamMembership> AddVeAsync(World world, string name, string callSign, params VeTag[] tags)
    {
        var person = new VolunteerExaminer { Name = name, CallSign = callSign, CreatedUtc = Now };
        world.Db.VolunteerExaminers.Add(person);
        await world.Db.SaveChangesAsync();

        var membership = new VeTeamMembership { VolunteerExaminerId = person.Id, TeamId = world.Team.Id, IsActive = true, CreatedUtc = Now };
        world.Db.VeTeamMemberships.Add(membership);
        await world.Db.SaveChangesAsync();

        foreach (var tag in tags)
        {
            world.Db.VeTagAssignments.Add(new VeTagAssignment { VeTeamMembershipId = membership.Id, VeTagId = tag.Id, CreatedUtc = Now });
        }

        await world.Db.SaveChangesAsync();
        return membership;
    }

    private static Task<IReadOnlyList<int>> TagIdsAsync(World world, int membershipId) =>
        world.Db.VeTagAssignments
            .Where(a => a.VeTeamMembershipId == membershipId)
            .Select(a => a.VeTagId)
            .ToListAsync()
            .ContinueWith(t => (IReadOnlyList<int>)t.Result);

    // ---- it writes what the rule says -----------------------------------------------------------

    [Fact]
    public async Task ATagIsAdded()
    {
        var world = await SeedAsync();
        var membership = await AddVeAsync(world, "Mike", "WX0MIK");
        world.Client.Members.Add(Member(1, "Mike - WX0MIK", MemberRole));

        var result = await world.Service.ApplyAsync(world.Team.Id, ActingUserId, null, CancellationToken.None);

        Assert.Equal(1, result.TagsAdded);
        Assert.Equal([world.MemberTag.Id], await TagIdsAsync(world, membership.Id));
    }

    [Fact]
    public async Task ATagIsRemoved()
    {
        var world = await SeedAsync();
        var membership = await AddVeAsync(world, "Mike", "WX0MIK", world.MemberTag);
        world.Client.Members.Add(Member(1, "Mike - WX0MIK"));

        var result = await world.Service.ApplyAsync(world.Team.Id, ActingUserId, null, CancellationToken.None);

        Assert.Equal(1, result.TagsRemoved);
        Assert.Empty(await TagIdsAsync(world, membership.Id));
    }

    /// <summary>The match is stored, which is what stops the next run from having to guess from a display name.</summary>
    [Fact]
    public async Task TheDiscordAccountIsRecorded()
    {
        var world = await SeedAsync();
        var membership = await AddVeAsync(world, "Mike", "WX0MIK");
        world.Client.Members.Add(Member(7, "Mike - WX0MIK", MemberRole));

        var result = await world.Service.ApplyAsync(world.Team.Id, ActingUserId, null, CancellationToken.None);

        var person = await world.Db.VolunteerExaminers.SingleAsync(v => v.Id == membership.VolunteerExaminerId);
        Assert.Equal(7ul, person.DiscordUserId);
        Assert.Equal("user7", person.DiscordUsername);
        Assert.Equal(1, result.Linked);
    }

    /// <summary>
    /// Running it twice must be a no-op the second time — the same idempotence every scan-based job
    /// here has, and the thing that makes a scheduled run (step 4) safe to add later.
    /// </summary>
    [Fact]
    public async Task ApplyingTwiceChangesNothingTheSecondTime()
    {
        var world = await SeedAsync();
        await AddVeAsync(world, "Mike", "WX0MIK");
        world.Client.Members.Add(Member(1, "Mike - WX0MIK", MemberRole));

        await world.Service.ApplyAsync(world.Team.Id, ActingUserId, null, CancellationToken.None);
        var second = await world.Service.ApplyAsync(world.Team.Id, ActingUserId, null, CancellationToken.None);

        Assert.Equal(0, second.TagsAdded);
        Assert.Equal(0, second.TagsRemoved);
        Assert.Equal(0, second.Linked);
    }

    /// <summary>Every write here is a change to somebody's record made by a person who clicked a button — it belongs in the audit log like every other one.</summary>
    [Fact]
    public async Task TheChangeIsAudited()
    {
        var world = await SeedAsync();
        await AddVeAsync(world, "Mike", "WX0MIK");
        world.Client.Members.Add(Member(1, "Mike - WX0MIK", MemberRole));

        await world.Service.ApplyAsync(world.Team.Id, ActingUserId, null, CancellationToken.None);

        var audit = await world.Db.AuditLogs.ToListAsync();
        Assert.Contains(audit, a => a.Action.Contains("Discord", StringComparison.OrdinalIgnoreCase));
        Assert.All(audit, a => Assert.Equal(ActingUserId, a.UserId));
    }

    // ---- what it must not write -----------------------------------------------------------------

    [Fact]
    public async Task AVeWhoIsNotInTheServerIsUntouched()
    {
        var world = await SeedAsync();
        var membership = await AddVeAsync(world, "Mike", "WX0MIK", world.MemberTag);
        world.Client.Members.Add(Member(50, "Someone Else"));

        await world.Service.ApplyAsync(world.Team.Id, ActingUserId, null, CancellationToken.None);

        Assert.Equal([world.MemberTag.Id], await TagIdsAsync(world, membership.Id));
    }

    /// <summary>The guard that matters most on the writing path: no data must never be applied as "nobody holds a role".</summary>
    [Fact]
    public async Task AFailedFetchWritesNothing()
    {
        var world = await SeedAsync();
        var membership = await AddVeAsync(world, "Mike", "WX0MIK", world.MemberTag);
        world.Client.Throws = true;

        var result = await world.Service.ApplyAsync(world.Team.Id, ActingUserId, null, CancellationToken.None);

        Assert.False(result.Plan.Ran);
        Assert.Equal(0, result.TagsRemoved);
        Assert.Equal([world.MemberTag.Id], await TagIdsAsync(world, membership.Id));
        Assert.Empty(await world.Db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task AnEmptyServerWritesNothing()
    {
        var world = await SeedAsync();
        var membership = await AddVeAsync(world, "Mike", "WX0MIK", world.MemberTag);

        var result = await world.Service.ApplyAsync(world.Team.Id, ActingUserId, null, CancellationToken.None);

        Assert.False(result.Plan.Ran);
        Assert.Equal([world.MemberTag.Id], await TagIdsAsync(world, membership.Id));
    }

    // ---- the preview is a photograph ------------------------------------------------------------

    /// <summary>
    /// Discord changed between looking and clicking. The fresh answer is what gets written — applying
    /// the stale one would act on a role that has since been taken away.
    /// </summary>
    [Fact]
    public async Task ApplyUsesTheFreshAnswerNotThePreviewedOne()
    {
        var world = await SeedAsync();
        var membership = await AddVeAsync(world, "Mike", "WX0MIK");
        world.Client.Members.Add(Member(1, "Mike - WX0MIK", MemberRole));

        var preview = await world.Service.BuildPreviewAsync(world.Team.Id, CancellationToken.None);
        Assert.Single(preview.Changes);

        // The role is revoked in the meantime.
        world.Client.Members.Clear();
        world.Client.Members.Add(Member(1, "Mike - WX0MIK"));

        var result = await world.Service.ApplyAsync(world.Team.Id, ActingUserId, preview.Fingerprint, CancellationToken.None);

        Assert.Equal(0, result.TagsAdded);
        Assert.Empty(await TagIdsAsync(world, membership.Id));
    }

    [Fact]
    public async Task ADifferenceFromThePreviewIsReported()
    {
        var world = await SeedAsync();
        await AddVeAsync(world, "Mike", "WX0MIK");
        world.Client.Members.Add(Member(1, "Mike - WX0MIK", MemberRole));
        var preview = await world.Service.BuildPreviewAsync(world.Team.Id, CancellationToken.None);

        world.Client.Members.Clear();
        world.Client.Members.Add(Member(1, "Mike - WX0MIK"));

        var result = await world.Service.ApplyAsync(world.Team.Id, ActingUserId, preview.Fingerprint, CancellationToken.None);

        Assert.True(result.DifferedFromPreview);
    }

    [Fact]
    public async Task AnUnchangedServerDoesNotReportADifference()
    {
        var world = await SeedAsync();
        await AddVeAsync(world, "Mike", "WX0MIK");
        world.Client.Members.Add(Member(1, "Mike - WX0MIK", MemberRole));
        var preview = await world.Service.BuildPreviewAsync(world.Team.Id, CancellationToken.None);

        var result = await world.Service.ApplyAsync(world.Team.Id, ActingUserId, preview.Fingerprint, CancellationToken.None);

        Assert.False(result.DifferedFromPreview);
        Assert.Equal(1, result.TagsAdded);
    }

    /// <summary>Applying without having previewed is allowed — a scheduled run (step 4) has no preview to compare against, and must not read as "everything differed".</summary>
    [Fact]
    public async Task NoPreviewMeansNoDifferenceIsClaimed()
    {
        var world = await SeedAsync();
        await AddVeAsync(world, "Mike", "WX0MIK");
        world.Client.Members.Add(Member(1, "Mike - WX0MIK", MemberRole));

        var result = await world.Service.ApplyAsync(world.Team.Id, ActingUserId, null, CancellationToken.None);

        Assert.False(result.DifferedFromPreview);
    }
}
