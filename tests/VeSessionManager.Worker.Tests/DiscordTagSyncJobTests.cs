using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Discord;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Jobs;
using VeSessionManager.Core.VolunteerExaminers;

namespace VeSessionManager.Worker.Tests;

/// <summary>
/// <see cref="DiscordTagSyncJob"/>'s per-team loop (#519 step 4) — the unattended form of the button
/// on the Discord Tags screen.
///
/// <para>The thing worth testing hardest is what it declines to do. This job <b>removes</b> tags, so
/// every guard between "Discord said something odd" and "a real VE quietly lost a tag" is load-bearing
/// in a way it is not for the on-demand check, where a human is looking at the result.</para>
/// </summary>
public class DiscordTagSyncJobTests
{
    private static readonly DateTime Now = new(2026, 9, 2, 3, 0, 0, DateTimeKind.Utc);

    private const ulong Guild = 900000000000000001;
    private const ulong MemberRole = 1170000000000000001;

    private sealed class FakeGuildClient : IDiscordGuildClient
    {
        public Dictionary<ulong, List<DiscordGuildMember>> MembersByGuild { get; } = [];
        public bool Throws { get; set; }
        public bool IsConfigured { get; set; } = true;

        public Task<IReadOnlyList<DiscordRoleSummary>> ListRolesAsync(ulong guildId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DiscordRoleSummary>>([new(MemberRole, "Team Member")]);

        public Task<IReadOnlyList<DiscordGuildMember>> ListMembersAsync(ulong guildId, CancellationToken cancellationToken) =>
            Throws
                ? Task.FromException<IReadOnlyList<DiscordGuildMember>>(new InvalidOperationException("unreachable"))
                : Task.FromResult<IReadOnlyList<DiscordGuildMember>>(
                    MembersByGuild.TryGetValue(guildId, out var members) ? [.. members] : []);
    }

    private static async Task<WorkerTickHarness> CreateHarnessAsync(FakeGuildClient client) =>
        await WorkerTickHarness.CreateAsync(services =>
        {
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
            services.AddSingleton<IDiscordGuildClient>(client);
            services.AddScoped<JobRunHistoryLogger>();
            services.AddScoped<DiscordTagSyncService>();
        });

    private static DiscordTagSyncJob CreateJob(WorkerTickHarness harness) =>
        new(harness.ScopeFactory, harness.Configuration, Quiet.Logger<DiscordTagSyncJob>());

    /// <summary>A team with one mapped tag, one VE holding it, and that VE present in Discord holding no role — i.e. one removal, unless something stops it.</summary>
    private static async Task<(int TeamId, int MembershipId, int TagId)> SeedAsync(
        WorkerTickHarness harness, FakeGuildClient client, bool enabled, ulong? guildId = Guild)
    {
        await using var dbContext = harness.NewContext();
        var team = new Team
        {
            Name = "HRCC",
            ExamToolsTeamCode = "HRCC",
            DiscordGuildId = guildId,
            DiscordTagSyncEnabled = enabled,
            CreatedUtc = Now,
        };
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();

        var tag = new VeTag { TeamId = team.Id, Name = "Team member", DiscordRoleId = MemberRole, DiscordRoleName = "Team Member", CreatedUtc = Now };
        var person = new VolunteerExaminer { Name = "Mike", CallSign = "WX0MIK", CreatedUtc = Now };
        dbContext.VeTags.Add(tag);
        dbContext.VolunteerExaminers.Add(person);
        await dbContext.SaveChangesAsync();

        var membership = new VeTeamMembership { VolunteerExaminerId = person.Id, TeamId = team.Id, IsActive = true, CreatedUtc = Now };
        dbContext.VeTeamMemberships.Add(membership);
        await dbContext.SaveChangesAsync();

        dbContext.VeTagAssignments.Add(new VeTagAssignment { VeTeamMembershipId = membership.Id, VeTagId = tag.Id, CreatedUtc = Now });
        await dbContext.SaveChangesAsync();

        client.MembersByGuild[Guild] = [new(1, "user1", "Mike - WX0MIK", "Mike - WX0MIK", [])];
        return (team.Id, membership.Id, tag.Id);
    }

    private static async Task<int> TagCountAsync(WorkerTickHarness harness, int membershipId)
    {
        await using var verify = harness.NewContext();
        return await verify.VeTagAssignments.CountAsync(a => a.VeTeamMembershipId == membershipId);
    }

    private static async Task<List<JobRunHistory>> HistoryAsync(WorkerTickHarness harness)
    {
        await using var verify = harness.NewContext();
        return await verify.JobRunHistories.AsNoTracking()
            .Where(h => h.JobName == JobSchedules.DiscordTagSync)
            .OrderBy(h => h.Id).ToListAsync();
    }

    // ---- the switch -----------------------------------------------------------------------------

    /// <summary>
    /// The whole point of the opt-in. A team that has mapped its tags but not turned the schedule on
    /// is using the on-demand check, and must not have its tags rewritten overnight.
    /// </summary>
    [Fact]
    public async Task ATeamThatHasNotTurnedItOnIsNotTouched()
    {
        var client = new FakeGuildClient();
        await using var harness = await CreateHarnessAsync(client);
        var (_, membershipId, _) = await SeedAsync(harness, client, enabled: false);

        await CreateJob(harness).RunTickAsync(CancellationToken.None);

        Assert.Equal(1, await TagCountAsync(harness, membershipId));
    }

    /// <summary>Skipped is still a run: the history row is what says the job looked and chose not to act, rather than never having run.</summary>
    [Fact]
    public async Task ASkippedTeamStillRecordsWhyInItsHistoryRow()
    {
        var client = new FakeGuildClient();
        await using var harness = await CreateHarnessAsync(client);
        await SeedAsync(harness, client, enabled: false);

        await CreateJob(harness).RunTickAsync(CancellationToken.None);

        var row = Assert.Single(await HistoryAsync(harness));
        Assert.True(row.Success);
        Assert.Contains("Not turned on", row.ResultSummary);
    }

    [Fact]
    public async Task AnEnabledTeamHasItsTagsBroughtIntoLine()
    {
        var client = new FakeGuildClient();
        await using var harness = await CreateHarnessAsync(client);
        var (_, membershipId, _) = await SeedAsync(harness, client, enabled: true);

        await CreateJob(harness).RunTickAsync(CancellationToken.None);

        Assert.Equal(0, await TagCountAsync(harness, membershipId));
    }

    /// <summary>
    /// Nobody clicked, so the audit row carries no user. Naming a real admin would put their name on a
    /// change they did not make, and a hardcoded stand-in id would break the foreign key on a
    /// deployment that lacks it.
    /// </summary>
    [Fact]
    public async Task AnUnattendedChangeIsAuditedAgainstNobody()
    {
        var client = new FakeGuildClient();
        await using var harness = await CreateHarnessAsync(client);
        await SeedAsync(harness, client, enabled: true);

        await CreateJob(harness).RunTickAsync(CancellationToken.None);

        await using var verify = harness.NewContext();
        var audit = await verify.AuditLogs.AsNoTracking().ToListAsync();
        Assert.NotEmpty(audit);
        Assert.All(audit, a => Assert.Null(a.UserId));
    }

    // ---- what an odd answer from Discord must not do --------------------------------------------

    /// <summary>
    /// The failure this job most needs to survive, and the one a human would have caught on the
    /// screen: Discord unreachable reads as "nobody holds any role", which under the rule means
    /// "remove every mapped tag on the team".
    /// </summary>
    [Fact]
    public async Task DiscordBeingUnreachableRemovesNothing()
    {
        var client = new FakeGuildClient { Throws = true };
        await using var harness = await CreateHarnessAsync(client);
        var (_, membershipId, _) = await SeedAsync(harness, client, enabled: true);

        await CreateJob(harness).RunTickAsync(CancellationToken.None);

        Assert.Equal(1, await TagCountAsync(harness, membershipId));
        var row = Assert.Single(await HistoryAsync(harness));
        Assert.Contains("Skipped", row.ResultSummary);
    }

    /// <summary>The same failure in its quiet form — what Discord returns when the privileged intent is off.</summary>
    [Fact]
    public async Task AnEmptyMemberListRemovesNothing()
    {
        var client = new FakeGuildClient();
        await using var harness = await CreateHarnessAsync(client);
        var (_, membershipId, _) = await SeedAsync(harness, client, enabled: true);
        client.MembersByGuild[Guild] = [];

        await CreateJob(harness).RunTickAsync(CancellationToken.None);

        Assert.Equal(1, await TagCountAsync(harness, membershipId));
    }

    /// <summary>An unreachable Discord must not take the whole tick down — the tick keeps going and the row records the failure. Same reasoning as JobTick.GuardedAsync exists for.</summary>
    [Fact]
    public async Task OneTeamsFailureDoesNotStopTheTick()
    {
        var client = new FakeGuildClient { Throws = true };
        await using var harness = await CreateHarnessAsync(client);
        await SeedAsync(harness, client, enabled: true);

        var exception = await Record.ExceptionAsync(() => CreateJob(harness).RunTickAsync(CancellationToken.None));

        Assert.Null(exception);
    }

    /// <summary>Running twice must be a no-op the second time. That idempotence is the property that makes an unattended daily schedule reasonable at all.</summary>
    [Fact]
    public async Task ASecondRunChangesNothing()
    {
        var client = new FakeGuildClient();
        await using var harness = await CreateHarnessAsync(client);
        var (_, membershipId, _) = await SeedAsync(harness, client, enabled: true);

        await CreateJob(harness).RunTickAsync(CancellationToken.None);
        await CreateJob(harness).RunTickAsync(CancellationToken.None);

        Assert.Equal(0, await TagCountAsync(harness, membershipId));
        var rows = await HistoryAsync(harness);
        Assert.Equal(2, rows.Count);
        Assert.Contains("0 tag(s) added, 0 removed", rows[1].ResultSummary);
    }

    /// <summary>The summary is the whole output on the Job Run History page — a blank one is the failure DUP-11 records for this base class.</summary>
    [Fact]
    public async Task TheRunSummarySaysWhatHappened()
    {
        var client = new FakeGuildClient();
        await using var harness = await CreateHarnessAsync(client);
        await SeedAsync(harness, client, enabled: true);

        await CreateJob(harness).RunTickAsync(CancellationToken.None);

        var row = Assert.Single(await HistoryAsync(harness));
        Assert.False(string.IsNullOrWhiteSpace(row.ResultSummary));
        Assert.Contains("1 removed", row.ResultSummary);
        Assert.Contains("exception(s)", row.ResultSummary);
    }
}
