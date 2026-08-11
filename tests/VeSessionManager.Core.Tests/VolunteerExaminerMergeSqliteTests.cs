using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.VolunteerExaminers;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Merging two VE records that turned out to be one person.
///
/// <para><b>Real SQLite, not InMemory.</b> Everything this service relies on is provider behaviour
/// InMemory does not have: it silently ignores transactions, and enforces neither the
/// <c>(SessionId, VolunteerExaminerId)</c> primary key collision nor the unique indexes on team
/// membership and accreditation. A passing InMemory test here would prove nothing.</para>
/// </summary>
public class VolunteerExaminerMergeSqliteTests
{
    private static readonly DateTime Now = new(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private static async Task<(SqliteConnection Connection, AppDbContext Context)> CreateAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var dbContext = new AppDbContext(options);
        await dbContext.Database.EnsureCreatedAsync();

        // The acting user for the audit entry. Real SQLite enforces AuditLog's foreign key, so a
        // test that never happens to seed a session (which creates one incidentally) would otherwise
        // fail on the FK rather than on anything it meant to assert.
        dbContext.Users.Add(new User { Name = "Acting admin", Email = "admin@localhost", Role = UserRole.SystemAdmin });
        await dbContext.SaveChangesAsync();

        return (connection, dbContext);
    }

    private static VolunteerExaminerMergeService CreateService(AppDbContext dbContext) =>
        new(dbContext, new FixedTimeProvider(Now), NullLogger<VolunteerExaminerMergeService>.Instance);

    private static async Task<Team> SeedTeamAsync(AppDbContext dbContext, string name)
    {
        var team = new Team { Name = name, ExamToolsTeamCode = name, CreatedUtc = Now };
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        return team;
    }

    private static async Task<Session> SeedSessionAsync(AppDbContext dbContext, Team team)
    {
        var vec = new Vec { Name = $"VEC-{Guid.NewGuid()}" };
        var user = new User { Name = "System", Email = $"{Guid.NewGuid()}@localhost", Role = UserRole.SystemAdmin };
        var session = new Session
        {
            ExamToolsSessionId = Guid.NewGuid().ToString(),
            Title = "Session",
            ScheduledStartUtc = Now.AddDays(-30),
            DurationMinutes = 60,
            Team = team,
            Vec = vec,
            FeeConfiguration = new FeeConfiguration
            {
                Vec = vec,
                EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                FeeCollectionEnabled = true,
                ExamFeeAmount = 15m,
                CreatedByUser = user,
                CreatedUtc = Now
            },
            Status = SessionStatus.Active,
            CreatedUtc = Now
        };
        dbContext.Sessions.Add(session);
        await dbContext.SaveChangesAsync();
        return session;
    }

    private static async Task<VolunteerExaminer> SeedVeAsync(
        AppDbContext dbContext, string name, string callSign, Team? team = null, string? frn = null)
    {
        var person = new VolunteerExaminer { Name = name, CallSign = callSign, Frn = frn, CreatedUtc = Now.AddYears(-1) };
        dbContext.VolunteerExaminers.Add(person);
        if (team is not null)
        {
            dbContext.VeTeamMemberships.Add(new VeTeamMembership
            {
                VolunteerExaminer = person, Team = team, IsActive = true, CreatedUtc = Now
            });
        }

        await dbContext.SaveChangesAsync();
        return person;
    }

    private static async Task LinkAsync(AppDbContext dbContext, Session session, VolunteerExaminer person)
    {
        dbContext.SessionVolunteerExaminers.Add(new SessionVolunteerExaminer
        {
            SessionId = session.Id, VolunteerExaminerId = person.Id
        });
        await dbContext.SaveChangesAsync();
    }

    /// <summary>
    /// The real WX0MIK case: two records, no overlapping sessions, so every one of them survives on
    /// the other side. This is the promise "it will not lose any session information" turned into a
    /// test.
    /// </summary>
    [Fact]
    public async Task NoOverlappingSessions_EveryLinkSurvivesOnTheSurvivor()
    {
        var (connection, dbContext) = await CreateAsync();
        await using var _ = connection;
        await using var __ = dbContext;

        var teamA = await SeedTeamAsync(dbContext, "HRCC");
        var teamB = await SeedTeamAsync(dbContext, "WX0MIK");
        var survivor = await SeedVeAsync(dbContext, "Michael N. Wills", "WX0MIK", teamA);
        var duplicate = await SeedVeAsync(dbContext, "Michael Wills", "WX0MIK", teamB);

        foreach (var _unused in Enumerable.Range(0, 3))
        {
            await LinkAsync(dbContext, await SeedSessionAsync(dbContext, teamA), survivor);
        }

        await LinkAsync(dbContext, await SeedSessionAsync(dbContext, teamB), duplicate);
        await LinkAsync(dbContext, await SeedSessionAsync(dbContext, teamB), duplicate);

        Assert.Equal(VeMergeResult.Success,
            await CreateService(dbContext).MergeAsync(survivor.Id, duplicate.Id, userId: 1, CancellationToken.None));

        dbContext.ChangeTracker.Clear();
        Assert.Equal(5, await dbContext.SessionVolunteerExaminers.CountAsync(l => l.VolunteerExaminerId == survivor.Id));
        Assert.Equal(5, await dbContext.SessionVolunteerExaminers.CountAsync());
    }

    /// <summary>
    /// The one case where a link row disappears. Not data loss: one person cannot be on a session's
    /// roster twice, so collapsing two records of the same fact into one is the correct answer.
    /// </summary>
    [Fact]
    public async Task ASessionBothRecordsWorked_CollapsesToOneLink()
    {
        var (connection, dbContext) = await CreateAsync();
        await using var _ = connection;
        await using var __ = dbContext;

        var team = await SeedTeamAsync(dbContext, "HRCC");
        var shared = await SeedSessionAsync(dbContext, team);
        var survivor = await SeedVeAsync(dbContext, "Sam Granger", "N2SPG", team);
        var duplicate = await SeedVeAsync(dbContext, "Samuel Granger", "N2SPG");

        await LinkAsync(dbContext, shared, survivor);
        await LinkAsync(dbContext, shared, duplicate);
        await LinkAsync(dbContext, await SeedSessionAsync(dbContext, team), duplicate);

        Assert.Equal(VeMergeResult.Success,
            await CreateService(dbContext).MergeAsync(survivor.Id, duplicate.Id, 1, CancellationToken.None));

        dbContext.ChangeTracker.Clear();
        var links = await dbContext.SessionVolunteerExaminers.ToListAsync();
        Assert.Equal(2, links.Count);                                   // 3 links, 2 distinct sessions
        Assert.All(links, l => Assert.Equal(survivor.Id, l.VolunteerExaminerId));
        Assert.Single(links, l => l.SessionId == shared.Id);
    }

    [Fact]
    public async Task MergedRecord_DisappearsFromEveryQuery()
    {
        var (connection, dbContext) = await CreateAsync();
        await using var _ = connection;
        await using var __ = dbContext;

        var team = await SeedTeamAsync(dbContext, "HRCC");
        var survivor = await SeedVeAsync(dbContext, "Sam Granger", "N2SPG", team);
        var duplicate = await SeedVeAsync(dbContext, "Samuel Granger", "N2SPG");

        await CreateService(dbContext).MergeAsync(survivor.Id, duplicate.Id, 1, CancellationToken.None);
        dbContext.ChangeTracker.Clear();

        Assert.Single(await dbContext.VolunteerExaminers.ToListAsync());

        // Still there for the audit trail and a future un-merge — hidden, not destroyed.
        var retired = await dbContext.VolunteerExaminers.IgnoreQueryFilters()
            .SingleAsync(v => v.Id == duplicate.Id);
        Assert.Equal(survivor.Id, retired.MergedIntoVolunteerExaminerId);
    }

    /// <summary>Unique on (VolunteerExaminerId, TeamId), so a shared team must fold into one row — and being active anywhere means they serve that team.</summary>
    [Fact]
    public async Task SharedTeam_FoldsIntoOneMembership_ActiveWins()
    {
        var (connection, dbContext) = await CreateAsync();
        await using var _ = connection;
        await using var __ = dbContext;

        var team = await SeedTeamAsync(dbContext, "HRCC");
        var survivor = await SeedVeAsync(dbContext, "Sam Granger", "N2SPG", team);
        var duplicate = await SeedVeAsync(dbContext, "Samuel Granger", "N2SPG", team);

        var survivorMembership = await dbContext.VeTeamMemberships.SingleAsync(m => m.VolunteerExaminerId == survivor.Id);
        survivorMembership.IsActive = false;
        survivorMembership.InactivatedUtc = Now.AddDays(-1);
        await dbContext.SaveChangesAsync();

        Assert.Equal(VeMergeResult.Success,
            await CreateService(dbContext).MergeAsync(survivor.Id, duplicate.Id, 1, CancellationToken.None));

        dbContext.ChangeTracker.Clear();
        var membership = Assert.Single(await dbContext.VeTeamMemberships.ToListAsync());
        Assert.Equal(survivor.Id, membership.VolunteerExaminerId);
        Assert.True(membership.IsActive);
        Assert.Null(membership.InactivatedUtc);
    }

    [Fact]
    public async Task SharedTeam_UnionsTheTags()
    {
        var (connection, dbContext) = await CreateAsync();
        await using var _ = connection;
        await using var __ = dbContext;

        var team = await SeedTeamAsync(dbContext, "HRCC");
        var memberTag = new VeTag { TeamId = team.Id, Name = "Team member", CreatedUtc = Now };
        var leadTag = new VeTag { TeamId = team.Id, Name = "Team lead", CreatedUtc = Now };
        dbContext.VeTags.AddRange(memberTag, leadTag);
        await dbContext.SaveChangesAsync();

        var survivor = await SeedVeAsync(dbContext, "Sam Granger", "N2SPG", team);
        var duplicate = await SeedVeAsync(dbContext, "Samuel Granger", "N2SPG", team);

        var survivorMembership = await dbContext.VeTeamMemberships.SingleAsync(m => m.VolunteerExaminerId == survivor.Id);
        var duplicateMembership = await dbContext.VeTeamMemberships.SingleAsync(m => m.VolunteerExaminerId == duplicate.Id);
        dbContext.VeTagAssignments.Add(new VeTagAssignment { VeTeamMembershipId = survivorMembership.Id, VeTagId = memberTag.Id, CreatedUtc = Now });
        dbContext.VeTagAssignments.Add(new VeTagAssignment { VeTeamMembershipId = duplicateMembership.Id, VeTagId = leadTag.Id, CreatedUtc = Now });
        await dbContext.SaveChangesAsync();

        await CreateService(dbContext).MergeAsync(survivor.Id, duplicate.Id, 1, CancellationToken.None);
        dbContext.ChangeTracker.Clear();

        var assignments = await dbContext.VeTagAssignments.ToListAsync();
        Assert.Equal(2, assignments.Count);
        Assert.All(assignments, a => Assert.Equal(survivorMembership.Id, a.VeTeamMembershipId));
    }

    /// <summary>Two different FRNs is FCC saying these are two people — stronger evidence against the merge than a matching name is for it.</summary>
    [Fact]
    public async Task DifferentFrns_AreRefused()
    {
        var (connection, dbContext) = await CreateAsync();
        await using var _ = connection;
        await using var __ = dbContext;

        var team = await SeedTeamAsync(dbContext, "HRCC");
        var survivor = await SeedVeAsync(dbContext, "Sam Granger", "N2SPG", team, frn: "111");
        var duplicate = await SeedVeAsync(dbContext, "Samuel Granger", "NP2UU", team, frn: "222");

        Assert.Equal(VeMergeResult.DifferentFrns,
            await CreateService(dbContext).MergeAsync(survivor.Id, duplicate.Id, 1, CancellationToken.None));

        dbContext.ChangeTracker.Clear();
        Assert.Equal(2, await dbContext.VolunteerExaminers.CountAsync());
    }

    /// <summary>Fill blanks, never overwrite — nothing a human typed on the survivor is silently replaced by whichever record happened to lose.</summary>
    [Fact]
    public async Task ContactDetails_FillBlanksWithoutOverwriting()
    {
        var (connection, dbContext) = await CreateAsync();
        await using var _ = connection;
        await using var __ = dbContext;

        var team = await SeedTeamAsync(dbContext, "HRCC");
        var survivor = await SeedVeAsync(dbContext, "Sam Granger", "N2SPG", team);
        var duplicate = await SeedVeAsync(dbContext, "Samuel Granger", "N2SPG");

        survivor.Phone = "555-0001";
        duplicate.Phone = "555-9999";
        duplicate.City = "Mankato";
        duplicate.Frn = "0004511143";
        await dbContext.SaveChangesAsync();

        await CreateService(dbContext).MergeAsync(survivor.Id, duplicate.Id, 1, CancellationToken.None);
        dbContext.ChangeTracker.Clear();

        var merged = await dbContext.VolunteerExaminers.SingleAsync();
        Assert.Equal("555-0001", merged.Phone);          // kept
        Assert.Equal("Mankato", merged.City);            // filled
        Assert.Equal("0004511143", merged.Frn);          // filled
    }

    /// <summary>
    /// MergedIntoVolunteerExaminerId records only THAT a merge happened. Without the moved session
    /// ids an un-merge could not tell whose history was whose, so calling this reversible would be
    /// an overclaim.
    /// </summary>
    [Fact]
    public async Task AuditEntry_RecordsWhichSessionsMoved()
    {
        var (connection, dbContext) = await CreateAsync();
        await using var _ = connection;
        await using var __ = dbContext;

        var team = await SeedTeamAsync(dbContext, "HRCC");
        var survivor = await SeedVeAsync(dbContext, "Sam Granger", "N2SPG", team);
        var duplicate = await SeedVeAsync(dbContext, "Samuel Granger", "N2SPG");
        var moved = await SeedSessionAsync(dbContext, team);
        await LinkAsync(dbContext, moved, duplicate);

        await CreateService(dbContext).MergeAsync(survivor.Id, duplicate.Id, 1, CancellationToken.None);
        dbContext.ChangeTracker.Clear();

        var audit = await dbContext.AuditLogs.SingleAsync(a => a.Action == "VeRecordsMerged");
        Assert.Contains($"[{moved.Id}]", audit.Details);
    }

    [Fact]
    public async Task Preview_ReportsTheRealCounts()
    {
        var (connection, dbContext) = await CreateAsync();
        await using var _ = connection;
        await using var __ = dbContext;

        var team = await SeedTeamAsync(dbContext, "HRCC");
        var shared = await SeedSessionAsync(dbContext, team);
        var survivor = await SeedVeAsync(dbContext, "Sam Granger", "N2SPG", team);
        var duplicate = await SeedVeAsync(dbContext, "Samuel Granger", "N2SPG", team);

        await LinkAsync(dbContext, shared, survivor);
        await LinkAsync(dbContext, shared, duplicate);
        await LinkAsync(dbContext, await SeedSessionAsync(dbContext, team), duplicate);

        var (result, preview) = await CreateService(dbContext).PreviewAsync(survivor.Id, duplicate.Id, CancellationToken.None);

        Assert.Equal(VeMergeResult.Success, result);
        Assert.Equal(1, preview!.SessionsMoving);
        Assert.Equal(1, preview.SessionsAlreadyShared);
        Assert.Equal(1, preview.TeamMembershipsMoving);
    }

    /// <summary>
    /// Issue #250. Three references identify a person to an <i>account</i> rather than to their
    /// roster history, and the merge left all three pointing at the retired row — which a global
    /// query filter then hides, so they do not merely go stale, their targets go invisible.
    ///
    /// <para>Real SQLite matters twice over here: the repoint uses <c>ExecuteUpdateAsync</c>, which
    /// EF InMemory does not support at all, and the token assertions depend on the query filter
    /// actually being applied to an INNER JOIN.</para>
    /// </summary>
    [Fact]
    public async Task Merge_RepointsTheAccountLink_SelfServiceTokens_AndEmailChangeRequests()
    {
        var (connection, dbContext) = await CreateAsync();
        await using var _ = connection;
        await using var __ = dbContext;

        var team = await SeedTeamAsync(dbContext, "HRCC");
        var survivor = await SeedVeAsync(dbContext, "Sam Granger", "N2SPG", team);
        var duplicate = await SeedVeAsync(dbContext, "Samuel Granger", "N2SPG", team);

        // The one VE of 176 who has an account, and an outstanding sign-in link.
        var account = new User
        {
            Name = "Sam Granger",
            Email = "sam@example.org",
            Role = UserRole.TeamLead,
            VolunteerExaminerId = duplicate.Id
        };
        dbContext.Users.Add(account);
        dbContext.VeSelfServiceTokens.Add(new VeSelfServiceToken
        {
            VolunteerExaminerId = duplicate.Id,
            TokenHash = "hash-1",
            CreatedUtc = Now,
            ExpiresUtc = Now.AddMinutes(30),
            SentToEmail = "sam@example.org"
        });
        dbContext.VeEmailChangeRequests.Add(new VeEmailChangeRequest
        {
            VolunteerExaminerId = duplicate.Id,
            TokenHash = "hash-2",
            NewEmail = "new@example.org",
            CreatedUtc = Now,
            ExpiresUtc = Now.AddHours(24),
            ConfirmationSentToEmail = "sam@example.org"
        });
        await dbContext.SaveChangesAsync();

        var result = await CreateService(dbContext).MergeAsync(survivor.Id, duplicate.Id, 1, CancellationToken.None);
        Assert.Equal(VeMergeResult.Success, result);

        dbContext.ChangeTracker.Clear();

        // Without the repoint this still reads duplicate.Id, and /Account/MyVeDetails resolves it
        // through the filtered DbSet — so the person loses access to their own record permanently,
        // and the unique filtered index blocks re-linking by hand.
        var reloadedAccount = await dbContext.Users.AsNoTracking().SingleAsync(u => u.Id == account.Id);
        Assert.Equal(survivor.Id, reloadedAccount.VolunteerExaminerId);

        // These two Include a *required* navigation, which EF renders as an INNER JOIN — so with the
        // reference left on the retired row the token row itself vanishes from the query and a live
        // link reports "invalid or expired". Loading them the way the services do is the assertion.
        var token = await dbContext.VeSelfServiceTokens
            .Include(t => t.VolunteerExaminer)
            .AsNoTracking()
            .SingleOrDefaultAsync(t => t.TokenHash == "hash-1");
        Assert.NotNull(token);
        Assert.Equal(survivor.Id, token!.VolunteerExaminerId);

        var changeRequest = await dbContext.VeEmailChangeRequests
            .Include(r => r.VolunteerExaminer)
            .AsNoTracking()
            .SingleOrDefaultAsync(r => r.TokenHash == "hash-2");
        Assert.NotNull(changeRequest);
        Assert.Equal(survivor.Id, changeRequest!.VolunteerExaminerId);
    }
}
