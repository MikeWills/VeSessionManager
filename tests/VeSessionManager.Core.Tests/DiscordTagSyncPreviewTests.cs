using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Discord;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.VolunteerExaminers;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// The Discord tag sync's preview (#519 step 2) — what <i>would</i> change, and everything it could
/// not account for. Nothing here writes; see docs/discord-tag-sync.md for the decided rule.
/// </summary>
public class DiscordTagSyncPreviewTests
{
    private static readonly DateTime Now = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    private const ulong Guild = 900000000000000001;
    private const ulong MemberRole = 1170000000000000001;
    private const ulong LeadRole = 1170000000000000002;

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    /// <summary>A stand-in guild. <see cref="Throws"/> and an empty member list are the two failure shapes the service has to tell apart from "nobody holds a role".</summary>
    private sealed class FakeGuildClient : IDiscordGuildClient
    {
        public List<DiscordGuildMember> Members { get; } = [];
        public bool Throws { get; set; }
        public bool Configured { get; set; } = true;
        public bool IsConfigured => Configured;

        public Task<IReadOnlyList<DiscordRoleSummary>> ListRolesAsync(ulong guildId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DiscordRoleSummary>>([new(MemberRole, "Team Member"), new(LeadRole, "Team Lead")]);

        public Task<IReadOnlyList<DiscordGuildMember>> ListMembersAsync(ulong guildId, CancellationToken cancellationToken) =>
            Throws
                ? Task.FromException<IReadOnlyList<DiscordGuildMember>>(new InvalidOperationException("Missing the GUILD_MEMBERS intent"))
                : Task.FromResult<IReadOnlyList<DiscordGuildMember>>([.. Members]);
    }

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static DiscordTagSyncService CreateService(AppDbContext dbContext, FakeGuildClient client) =>
        new(dbContext, client, new FixedTimeProvider(Now), NullLogger<DiscordTagSyncService>.Instance);

    private static DiscordGuildMember Member(ulong id, string displayName, params ulong[] roles) =>
        new(id, $"user{id}", displayName, displayName, roles);

    private sealed class World
    {
        public required AppDbContext Db { get; init; }
        public required FakeGuildClient Client { get; init; }
        public required Team Team { get; init; }
        public required DiscordTagSyncService Service { get; init; }
        public VeTag MemberTag { get; set; } = null!;
        public VeTag LeadTag { get; set; } = null!;
        public VeTag UnmappedTag { get; set; } = null!;

        public async Task<VeTeamMembership> AddVeAsync(string name, string? callSign, params VeTag[] tags)
        {
            var person = new VolunteerExaminer { Name = name, CallSign = callSign, CreatedUtc = Now };
            Db.VolunteerExaminers.Add(person);
            await Db.SaveChangesAsync();

            var membership = new VeTeamMembership { VolunteerExaminerId = person.Id, TeamId = Team.Id, IsActive = true, CreatedUtc = Now };
            Db.VeTeamMemberships.Add(membership);
            await Db.SaveChangesAsync();

            foreach (var tag in tags)
            {
                Db.VeTagAssignments.Add(new VeTagAssignment { VeTeamMembershipId = membership.Id, VeTagId = tag.Id, CreatedUtc = Now });
            }

            await Db.SaveChangesAsync();
            return membership;
        }
    }

    private static async Task<World> SeedAsync()
    {
        var db = CreateContext();
        var client = new FakeGuildClient();
        var team = new Team { Name = "HRCC", DiscordGuildId = Guild };
        db.Teams.Add(team);
        await db.SaveChangesAsync();

        var world = new World { Db = db, Client = client, Team = team, Service = CreateService(db, client) };
        world.MemberTag = new VeTag { TeamId = team.Id, Name = "Team member", DiscordRoleId = MemberRole, DiscordRoleName = "Team Member", CreatedUtc = Now };
        world.LeadTag = new VeTag { TeamId = team.Id, Name = "Team lead", DiscordRoleId = LeadRole, DiscordRoleName = "Team Lead", CreatedUtc = Now };
        world.UnmappedTag = new VeTag { TeamId = team.Id, Name = "Auditioning", CreatedUtc = Now };
        db.VeTags.AddRange(world.MemberTag, world.LeadTag, world.UnmappedTag);
        await db.SaveChangesAsync();
        return world;
    }

    // ---- the rule table -------------------------------------------------------------------------

    [Fact]
    public async Task HoldsTheRoleButNotTheTag_TheTagIsAdded()
    {
        var world = await SeedAsync();
        await world.AddVeAsync("Mike", "WX0MIK");
        world.Client.Members.Add(Member(1, "Mike - WX0MIK", MemberRole));

        var plan = await world.Service.BuildPreviewAsync(world.Team.Id, CancellationToken.None);

        var change = Assert.Single(plan.Changes);
        Assert.Equal("WX0MIK", change.CallSign);
        Assert.Equal(["Team member"], change.TagsToAdd.Select(t => t.Name));
        Assert.Empty(change.TagsToRemove);
    }

    /// <summary>The half that makes this worth building: Discord is how a team says someone is no longer a member.</summary>
    [Fact]
    public async Task HoldsTheTagButNotTheRole_TheTagIsRemoved()
    {
        var world = await SeedAsync();
        await world.AddVeAsync("Mike", "WX0MIK", world.MemberTag);
        world.Client.Members.Add(Member(1, "Mike - WX0MIK"));

        var plan = await world.Service.BuildPreviewAsync(world.Team.Id, CancellationToken.None);

        var change = Assert.Single(plan.Changes);
        Assert.Equal(["Team member"], change.TagsToRemove.Select(t => t.Name));
        Assert.Empty(change.TagsToAdd);
    }

    [Fact]
    public async Task HoldsBoth_NothingChanges()
    {
        var world = await SeedAsync();
        await world.AddVeAsync("Mike", "WX0MIK", world.MemberTag);
        world.Client.Members.Add(Member(1, "Mike - WX0MIK", MemberRole));

        var plan = await world.Service.BuildPreviewAsync(world.Team.Id, CancellationToken.None);

        Assert.Empty(plan.Changes);
    }

    /// <summary>
    /// The opt-in. A tag with no role mapped is hand-managed, and a matched VE holding no roles at all
    /// must not lose it — this is the difference between "Discord owns the mapped tags" and "Discord
    /// owns the tag list".
    /// </summary>
    [Fact]
    public async Task AnUnmappedTagIsNeverTouched()
    {
        var world = await SeedAsync();
        await world.AddVeAsync("Mike", "WX0MIK", world.UnmappedTag);
        world.Client.Members.Add(Member(1, "Mike - WX0MIK"));

        var plan = await world.Service.BuildPreviewAsync(world.Team.Id, CancellationToken.None);

        Assert.Empty(plan.Changes);
    }

    /// <summary>
    /// Learning who someone is on Discord is worth storing, but it is not a tag change — it belongs in
    /// its own list, or a screen showing "3 changes" would mean three different things depending on
    /// whether anyone had been matched before.
    /// </summary>
    [Fact]
    public async Task RecognisingSomeoneForTheFirstTimeIsALinkNotAChange()
    {
        var world = await SeedAsync();
        await world.AddVeAsync("Mike", "WX0MIK", world.MemberTag);
        world.Client.Members.Add(Member(7, "Mike - WX0MIK", MemberRole));

        var plan = await world.Service.BuildPreviewAsync(world.Team.Id, CancellationToken.None);

        Assert.Empty(plan.Changes);
        var link = Assert.Single(plan.NewLinks);
        Assert.Equal(7ul, link.DiscordUserId);
        Assert.Equal("WX0MIK", link.CallSign);
    }

    /// <summary>Already linked and already correct is the steady state, and it must produce a completely empty plan.</summary>
    [Fact]
    public async Task AnAlreadyLinkedAndCorrectVeProducesNothingAtAll()
    {
        var world = await SeedAsync();
        var membership = await world.AddVeAsync("Mike", "WX0MIK", world.MemberTag);
        var person = await world.Db.VolunteerExaminers.SingleAsync(v => v.Id == membership.VolunteerExaminerId);
        person.DiscordUserId = 7;
        await world.Db.SaveChangesAsync();
        world.Client.Members.Add(Member(7, "Mike - WX0MIK", MemberRole));

        var plan = await world.Service.BuildPreviewAsync(world.Team.Id, CancellationToken.None);

        Assert.True(plan.Ran);
        Assert.False(plan.HasAnythingToShow);
    }

    // ---- the two do-nothing filters -------------------------------------------------------------

    /// <summary>Not in Discord at all: untouched forever, not stripped — and reported so a drifted name is findable.</summary>
    [Fact]
    public async Task AVeWhoIsNotInTheServerKeepsEveryTag()
    {
        var world = await SeedAsync();
        await world.AddVeAsync("Mike", "WX0MIK", world.MemberTag);

        // Somebody has to be in the server, or the run is refused as "no data" before any of this is
        // reached — which is a different test, below.
        world.Client.Members.Add(Member(50, "Someone Else Entirely"));

        var plan = await world.Service.BuildPreviewAsync(world.Team.Id, CancellationToken.None);

        Assert.Empty(plan.Changes);
        var unmatched = Assert.Single(plan.VolunteerExaminersWithoutMember);
        Assert.Equal("WX0MIK", unmatched.CallSign);
    }

    [Fact]
    public async Task AMemberWhoIsNotAVeIsNeverGivenOne()
    {
        var world = await SeedAsync();
        world.Client.Members.Add(Member(99, "Some Candidate", MemberRole));

        var plan = await world.Service.BuildPreviewAsync(world.Team.Id, CancellationToken.None);

        Assert.Empty(plan.Changes);
        Assert.Equal(0, await world.Db.VolunteerExaminers.CountAsync());
    }

    /// <summary>
    /// The exceptions filter that keeps the list readable: a server is full of candidates and club
    /// members, and only someone holding a mapped role would have synced had they matched.
    /// </summary>
    [Fact]
    public async Task OnlyUnmatchedMembersHoldingAMappedRoleAreReported()
    {
        var world = await SeedAsync();
        world.Client.Members.Add(Member(98, "Just A Club Member"));
        world.Client.Members.Add(Member(99, "Someone With The Role", MemberRole));

        var plan = await world.Service.BuildPreviewAsync(world.Team.Id, CancellationToken.None);

        var reported = Assert.Single(plan.MembersWithoutVolunteerExaminer);
        Assert.Equal(99ul, reported.DiscordUserId);
    }

    // ---- matching -------------------------------------------------------------------------------

    /// <summary>A stored id is the identity; a display name that no longer carries the call must not undo a known match.</summary>
    [Fact]
    public async Task AStoredDiscordUserIdWinsOverTheDisplayName()
    {
        var world = await SeedAsync();
        var membership = await world.AddVeAsync("Mike", "WX0MIK");
        var person = await world.Db.VolunteerExaminers.SingleAsync(v => v.Id == membership.VolunteerExaminerId);
        person.DiscordUserId = 1;
        await world.Db.SaveChangesAsync();
        world.Client.Members.Add(Member(1, "just some nickname", MemberRole));

        var plan = await world.Service.BuildPreviewAsync(world.Team.Id, CancellationToken.None);

        var change = Assert.Single(plan.Changes);
        Assert.Equal(["Team member"], change.TagsToAdd.Select(t => t.Name));
    }

    /// <summary>A call sign in the display name is a match the first time, and is what backfills the id on apply.</summary>
    [Fact]
    public async Task AMatchByCallSignRecordsTheDiscordUserIdToStore()
    {
        var world = await SeedAsync();
        await world.AddVeAsync("Mike", "WX0MIK");
        world.Client.Members.Add(Member(7, "Mike - WX0MIK", MemberRole));

        var plan = await world.Service.BuildPreviewAsync(world.Team.Id, CancellationToken.None);

        Assert.Equal(7ul, Assert.Single(plan.Changes).DiscordUserId);
    }

    /// <summary>
    /// A vanity call comes through and the server name lags. The person is the same person, and
    /// VeCallSignHistory is what already stops a rename from creating a second one.
    /// </summary>
    [Fact]
    public async Task AFormerCallSignStillMatches()
    {
        var world = await SeedAsync();
        var membership = await world.AddVeAsync("Mike", "WX0MIK");
        world.Db.VeCallSignHistories.Add(new VeCallSignHistory
        {
            VolunteerExaminerId = membership.VolunteerExaminerId,
            CallSign = "KD0OLD",
            FirstSeenUtc = Now,
            ReplacedUtc = Now,
        });
        await world.Db.SaveChangesAsync();
        world.Client.Members.Add(Member(1, "Mike - KD0OLD", MemberRole));

        var plan = await world.Service.BuildPreviewAsync(world.Team.Id, CancellationToken.None);

        Assert.Single(plan.Changes);
    }

    /// <summary>
    /// Two VEs named in one display name: nothing is guessed, nothing changes, and it is reported —
    /// picking the first would assign someone else's tags by string order.
    /// </summary>
    [Fact]
    public async Task ADisplayNameNamingTwoVEsChangesNothingAndIsReported()
    {
        var world = await SeedAsync();
        await world.AddVeAsync("Mike", "WX0MIK");
        await world.AddVeAsync("Alaric", "KF0JZP");
        world.Client.Members.Add(Member(1, "WX0MIK and KF0JZP", MemberRole));

        var plan = await world.Service.BuildPreviewAsync(world.Team.Id, CancellationToken.None);

        Assert.Empty(plan.Changes);
        Assert.Single(plan.AmbiguousMembers);
    }

    /// <summary>Another team's VE is not this team's business, even in a shared server.</summary>
    [Fact]
    public async Task AVeOnAnotherTeamIsNotMatched()
    {
        var world = await SeedAsync();
        var other = new Team { Name = "MARC" };
        world.Db.Teams.Add(other);
        await world.Db.SaveChangesAsync();
        var person = new VolunteerExaminer { Name = "Elsewhere", CallSign = "N0OTH", CreatedUtc = Now };
        world.Db.VolunteerExaminers.Add(person);
        await world.Db.SaveChangesAsync();
        world.Db.VeTeamMemberships.Add(new VeTeamMembership { VolunteerExaminerId = person.Id, TeamId = other.Id, IsActive = true, CreatedUtc = Now });
        await world.Db.SaveChangesAsync();
        world.Client.Members.Add(Member(1, "N0OTH", MemberRole));

        var plan = await world.Service.BuildPreviewAsync(world.Team.Id, CancellationToken.None);

        Assert.Empty(plan.Changes);
        Assert.Single(plan.MembersWithoutVolunteerExaminer);
    }

    /// <summary>Retired from the team: not synced, and not reported as missing from Discord either — they are not expected to be there.</summary>
    [Fact]
    public async Task AnInactiveMembershipIsLeftAlone()
    {
        var world = await SeedAsync();
        var membership = await world.AddVeAsync("Retired", "N0RET", world.MemberTag);
        membership.IsActive = false;
        await world.Db.SaveChangesAsync();

        // In the server, holding no role — so an active membership here would be a removal.
        world.Client.Members.Add(Member(1, "Retired - N0RET"));

        var plan = await world.Service.BuildPreviewAsync(world.Team.Id, CancellationToken.None);

        Assert.Empty(plan.Changes);
        Assert.Empty(plan.VolunteerExaminersWithoutMember);
    }

    // ---- no data is not "no roles" --------------------------------------------------------------

    /// <summary>
    /// The guard that stops a bad afternoon from stripping every mapped tag in the deployment: a
    /// failed fetch reads as "nobody holds any role" unless it is refused outright.
    /// </summary>
    [Fact]
    public async Task AFailedFetchChangesNothingAndSaysWhy()
    {
        var world = await SeedAsync();
        await world.AddVeAsync("Mike", "WX0MIK", world.MemberTag);
        world.Client.Throws = true;

        var plan = await world.Service.BuildPreviewAsync(world.Team.Id, CancellationToken.None);

        Assert.False(plan.Ran);
        Assert.Empty(plan.Changes);
        Assert.Empty(plan.VolunteerExaminersWithoutMember);
        Assert.NotNull(plan.SkippedReason);
    }

    /// <summary>
    /// An empty member list is the shape a missing GUILD_MEMBERS intent takes — no error, no members.
    /// A real server always has at least the bot in it, so empty means "could not read", never
    /// "everybody left".
    /// </summary>
    [Fact]
    public async Task AnEmptyMemberListIsTreatedAsNoData()
    {
        var world = await SeedAsync();
        await world.AddVeAsync("Mike", "WX0MIK", world.MemberTag);

        var plan = await world.Service.BuildPreviewAsync(world.Team.Id, CancellationToken.None);

        Assert.False(plan.Ran);
        Assert.Empty(plan.Changes);
    }

    [Fact]
    public async Task ATeamWithNoGuildDoesNotRun()
    {
        var world = await SeedAsync();
        world.Team.DiscordGuildId = null;
        await world.Db.SaveChangesAsync();
        await world.AddVeAsync("Mike", "WX0MIK", world.MemberTag);

        var plan = await world.Service.BuildPreviewAsync(world.Team.Id, CancellationToken.None);

        Assert.False(plan.Ran);
        Assert.Empty(plan.Changes);
    }

    /// <summary>Nothing mapped yet is not a failure — it is a team that has not opted in, and it must not read as one.</summary>
    [Fact]
    public async Task NoMappedTagsMeansNoChangesAndNoExceptions()
    {
        var world = await SeedAsync();
        world.Db.VeTags.RemoveRange(world.MemberTag, world.LeadTag);
        await world.Db.SaveChangesAsync();
        await world.AddVeAsync("Mike", "WX0MIK");
        world.Client.Members.Add(Member(1, "Mike - WX0MIK", MemberRole));

        var plan = await world.Service.BuildPreviewAsync(world.Team.Id, CancellationToken.None);

        Assert.Empty(plan.Changes);
        Assert.Empty(plan.MembersWithoutVolunteerExaminer);
    }
}
