using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.VolunteerExaminers;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// The VE directory and the writes behind it (issue #142 phase 2). Covers the two things most
/// likely to be got wrong quietly: "last worked" counting a session that has not happened yet, and
/// a tag from one team being applied to another team's row.
/// </summary>
public class VolunteerExaminerDirectoryServiceTests
{
    private static readonly DateTime Now = new(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static VolunteerExaminerManagementService CreateManagement(AppDbContext dbContext) =>
        new(dbContext, new FixedTimeProvider(Now));

    private static async Task<Team> SeedTeamAsync(AppDbContext dbContext, string name)
    {
        var team = new Team { Name = name, ExamToolsTeamCode = name, CreatedUtc = Now };
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        return team;
    }

    private static async Task<(VolunteerExaminer Person, VeTeamMembership Membership)> SeedVeAsync(
        AppDbContext dbContext, Team team, string callSign, string name)
    {
        var person = new VolunteerExaminer { Name = name, CallSign = callSign, CreatedUtc = Now };
        var membership = new VeTeamMembership { VolunteerExaminer = person, Team = team, IsActive = true, CreatedUtc = Now };
        dbContext.VolunteerExaminers.Add(person);
        dbContext.VeTeamMemberships.Add(membership);
        await dbContext.SaveChangesAsync();
        return (person, membership);
    }

    private static async Task<Session> SeedSessionAsync(AppDbContext dbContext, Team team, DateTime startUtc, bool finished)
    {
        var vec = new Vec { Name = $"VEC-{Guid.NewGuid()}" };
        var user = new User { Name = "System", Email = $"{Guid.NewGuid()}@localhost", Role = UserRole.SystemAdmin };
        var session = new Session
        {
            ExamToolsSessionId = Guid.NewGuid().ToString(),
            Title = "Session",
            ScheduledStartUtc = startUtc,
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
            ExamToolsClosedUtc = finished ? startUtc.AddHours(2) : null,
            CreatedUtc = Now
        };
        dbContext.Sessions.Add(session);
        await dbContext.SaveChangesAsync();
        return session;
    }

    /// <summary>
    /// The Status trap, third instance. Session.Status only ever means "not cancelled", so a
    /// scheduled-but-unrun session would report as worked — a VE booked for next month would show a
    /// "last worked" date in the future.
    /// </summary>
    [Fact]
    public async Task LastWorked_IgnoresASessionThatHasNotHappenedYet()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, "TEAM-A");
        var (person, _) = await SeedVeAsync(dbContext, team, "N2SPG", "Sam Granger");

        var past = await SeedSessionAsync(dbContext, team, Now.AddDays(-30), finished: true);
        var future = await SeedSessionAsync(dbContext, team, Now.AddDays(30), finished: false);
        dbContext.SessionVolunteerExaminers.Add(new SessionVolunteerExaminer { Session = past, VolunteerExaminer = person });
        dbContext.SessionVolunteerExaminers.Add(new SessionVolunteerExaminer { Session = future, VolunteerExaminer = person });
        await dbContext.SaveChangesAsync();

        var rows = await new VolunteerExaminerDirectoryService(dbContext)
            .GetDirectoryAsync([team.Id], new VeDirectoryFilter(), Now, CancellationToken.None);

        Assert.Equal(past.ScheduledStartUtc, Assert.Single(rows).LastWorkedUtc);
    }

    /// <summary>
    /// The row is per person now, but "last worked" is still computed per team and collapsed over the
    /// teams in scope — so filtering to a team answers "when did they last work for YOU", while
    /// unfiltered takes the most recent anywhere. A single global MAX would silently answer the wrong
    /// question the moment someone filtered.
    /// </summary>
    [Fact]
    public async Task LastWorked_NarrowsWhenFilteredToOneTeam()
    {
        await using var dbContext = CreateContext();
        var teamA = await SeedTeamAsync(dbContext, "TEAM-A");
        var teamB = await SeedTeamAsync(dbContext, "TEAM-B");
        var (person, _) = await SeedVeAsync(dbContext, teamA, "N2SPG", "Sam Granger");
        dbContext.VeTeamMemberships.Add(new VeTeamMembership { VolunteerExaminer = person, Team = teamB, IsActive = true, CreatedUtc = Now });

        var forA = await SeedSessionAsync(dbContext, teamA, Now.AddDays(-60), finished: true);
        var forB = await SeedSessionAsync(dbContext, teamB, Now.AddDays(-5), finished: true);
        dbContext.SessionVolunteerExaminers.Add(new SessionVolunteerExaminer { Session = forA, VolunteerExaminer = person });
        dbContext.SessionVolunteerExaminers.Add(new SessionVolunteerExaminer { Session = forB, VolunteerExaminer = person });
        await dbContext.SaveChangesAsync();

        var service = new VolunteerExaminerDirectoryService(dbContext);

        // One person, one row, both teams named on it.
        var merged = Assert.Single(await service.GetDirectoryAsync(null, new VeDirectoryFilter(), Now, CancellationToken.None));
        Assert.Equal(2, merged.Teams.Count);
        Assert.Equal(forB.ScheduledStartUtc, merged.LastWorkedUtc);   // the more recent of the two

        // Filtered to team A, it answers team A's question.
        var scoped = Assert.Single(await service.GetDirectoryAsync([teamA.Id], new VeDirectoryFilter(), Now, CancellationToken.None));
        Assert.Equal(forA.ScheduledStartUtc, scoped.LastWorkedUtc);
        Assert.Equal("TEAM-A", Assert.Single(scoped.Teams).Name);
    }

    [Fact]
    public async Task NoTags_MeansGuest_AndIsDerivedNotStored()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, "TEAM-A");
        await SeedVeAsync(dbContext, team, "N2SPG", "Sam Granger");

        var rows = await new VolunteerExaminerDirectoryService(dbContext)
            .GetDirectoryAsync([team.Id], new VeDirectoryFilter(), Now, CancellationToken.None);

        Assert.True(Assert.Single(rows).IsGuest);
    }

    [Fact]
    public async Task RetiredMembership_IsHiddenUnlessAskedFor()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, "TEAM-A");
        var (_, membership) = await SeedVeAsync(dbContext, team, "N2SPG", "Sam Granger");

        Assert.Equal(VeManagementResult.Success,
            await CreateManagement(dbContext).SetMembershipActiveAsync(membership.Id, false, userId: 1, CancellationToken.None));

        var service = new VolunteerExaminerDirectoryService(dbContext);
        Assert.Empty(await service.GetDirectoryAsync([team.Id], new VeDirectoryFilter(), Now, CancellationToken.None));
        Assert.Single(await service.GetDirectoryAsync([team.Id], new VeDirectoryFilter { IncludeInactive = true }, Now, CancellationToken.None));
    }

    /// <summary>Retiring someone must never remove the row — their session history references them by id.</summary>
    [Fact]
    public async Task Inactivating_KeepsThePersonAndTheMembership()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, "TEAM-A");
        var (_, membership) = await SeedVeAsync(dbContext, team, "N2SPG", "Sam Granger");

        await CreateManagement(dbContext).SetMembershipActiveAsync(membership.Id, false, userId: 1, CancellationToken.None);

        Assert.Single(dbContext.VolunteerExaminers);
        var stored = Assert.Single(dbContext.VeTeamMemberships);
        Assert.False(stored.IsActive);
        Assert.Equal(Now, stored.InactivatedUtc);
    }

    /// <summary>
    /// Filtering by tag name spans teams, because the tag NAME is the thing with a shared meaning.
    ///
    /// <para>Tags are per-team vocabulary, so two teams each defining "Member" are two rows. The
    /// filter used to key on the id, which meant the dropdown listed "Member" twice — identical and
    /// unlabelled — and choosing either one silently excluded the other team's people. The rows had
    /// always collapsed same-named tags into a single chip, so the filter disagreed with the column
    /// it was filtering.</para>
    /// </summary>
    [Fact]
    public async Task FilteringByTagName_MatchesThatNameOnEveryTeam()
    {
        await using var dbContext = CreateContext();
        var teamA = await SeedTeamAsync(dbContext, "TEAM-A");
        var teamB = await SeedTeamAsync(dbContext, "TEAM-B");
        var (_, onA) = await SeedVeAsync(dbContext, teamA, "N2SPG", "Sam Granger");
        var (_, onB) = await SeedVeAsync(dbContext, teamB, "W7QQQ", "Dana Reeve");
        await SeedVeAsync(dbContext, teamA, "K4ZZZ", "Untagged Person");

        var management = CreateManagement(dbContext);
        var (_, tagOnA) = await management.CreateTagAsync(teamA.Id, "Member", 0, null, null, null, 1, CancellationToken.None);
        var (_, tagOnB) = await management.CreateTagAsync(teamB.Id, "Member", 0, null, null, null, 1, CancellationToken.None);
        await management.SetTagsAsync(onA.Id, [tagOnA!.Id], 1, CancellationToken.None);
        await management.SetTagsAsync(onB.Id, [tagOnB!.Id], 1, CancellationToken.None);

        var rows = await new VolunteerExaminerDirectoryService(dbContext)
            .GetDirectoryAsync(null, new VeDirectoryFilter { TagName = "Member" }, Now, CancellationToken.None);

        // Both, not just whichever team's tag happened to be picked from the menu.
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.VolunteerExaminer.CallSign == "N2SPG");
        Assert.Contains(rows, r => r.VolunteerExaminer.CallSign == "W7QQQ");
    }

    /// <summary>
    /// "Guest" means no tag at all, and it is derived rather than stored — so it can't be picked the
    /// way a tag name is, and gets a sentinel value instead.
    /// </summary>
    [Fact]
    public async Task FilteringByTheGuestSentinel_ReturnsOnlyPeopleWithNoTags()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, "TEAM-A");
        var (_, tagged) = await SeedVeAsync(dbContext, team, "N2SPG", "Sam Granger");
        await SeedVeAsync(dbContext, team, "K4ZZZ", "Untagged Person");

        var management = CreateManagement(dbContext);
        var (_, tag) = await management.CreateTagAsync(team.Id, "Member", 0, null, null, null, 1, CancellationToken.None);
        await management.SetTagsAsync(tagged.Id, [tag!.Id], 1, CancellationToken.None);

        var rows = await new VolunteerExaminerDirectoryService(dbContext).GetDirectoryAsync(
            null, new VeDirectoryFilter { TagName = VolunteerExaminerDirectoryService.GuestTagFilter },
            Now, CancellationToken.None);

        Assert.Equal("K4ZZZ", Assert.Single(rows).VolunteerExaminer.CallSign);
    }

    /// <summary>
    /// The case that decided where this filter is applied. Someone tagged on HRCC and untagged on
    /// MARC is <b>not</b> a guest — the row shows their HRCC tags and no Guest chip.
    ///
    /// <para>Filtering the membership query would have matched their untagged MARC membership and
    /// returned them anyway, producing a row in a guests-only list that visibly carries tags. The
    /// filter therefore runs after the grouping, where IsGuest actually exists.</para>
    /// </summary>
    [Fact]
    public async Task SomeoneTaggedOnOneTeamAndUntaggedOnAnotherIsNotAGuest()
    {
        await using var dbContext = CreateContext();
        var teamA = await SeedTeamAsync(dbContext, "TEAM-A");
        var teamB = await SeedTeamAsync(dbContext, "TEAM-B");
        var (person, onA) = await SeedVeAsync(dbContext, teamA, "N2SPG", "Sam Granger");

        // Same person, second team, no tags there.
        dbContext.VeTeamMemberships.Add(new VeTeamMembership { VolunteerExaminerId = person.Id, TeamId = teamB.Id, IsActive = true });
        await dbContext.SaveChangesAsync();

        var management = CreateManagement(dbContext);
        var (_, tag) = await management.CreateTagAsync(teamA.Id, "Member", 0, null, null, null, 1, CancellationToken.None);
        await management.SetTagsAsync(onA.Id, [tag!.Id], 1, CancellationToken.None);

        var acrossBothTeams = await new VolunteerExaminerDirectoryService(dbContext).GetDirectoryAsync(
            null, new VeDirectoryFilter { TagName = VolunteerExaminerDirectoryService.GuestTagFilter },
            Now, CancellationToken.None);

        Assert.Empty(acrossBothTeams);

        // Scoped to the team where they hold no tag, they ARE a guest — the row's tags narrow to
        // that team, so the answer narrows with it. That is the collapse being scope-relative, not
        // a contradiction.
        var scopedToTeamB = await new VolunteerExaminerDirectoryService(dbContext).GetDirectoryAsync(
            [teamB.Id], new VeDirectoryFilter { TagName = VolunteerExaminerDirectoryService.GuestTagFilter },
            Now, CancellationToken.None);

        Assert.Single(scopedToTeamB);
    }

    /// <summary>SQLite's `=` on TEXT is case-sensitive, so a team that typed "member" would drop out of a "Member" filter without this.</summary>
    [Fact]
    public async Task FilteringByTagName_IgnoresCase()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, "TEAM-A");
        var (_, membership) = await SeedVeAsync(dbContext, team, "N2SPG", "Sam Granger");

        var management = CreateManagement(dbContext);
        var (_, tag) = await management.CreateTagAsync(team.Id, "Member", 0, null, null, null, 1, CancellationToken.None);
        await management.SetTagsAsync(membership.Id, [tag!.Id], 1, CancellationToken.None);

        var rows = await new VolunteerExaminerDirectoryService(dbContext)
            .GetDirectoryAsync(null, new VeDirectoryFilter { TagName = "  MEMBER  " }, Now, CancellationToken.None);

        Assert.Single(rows);
    }

    /// <summary>Tags are a team's private vocabulary; an id from another team must be rejected rather than quietly applied.</summary>
    [Fact]
    public async Task SetTags_RejectsATagBelongingToAnotherTeam()
    {
        await using var dbContext = CreateContext();
        var teamA = await SeedTeamAsync(dbContext, "TEAM-A");
        var teamB = await SeedTeamAsync(dbContext, "TEAM-B");
        var (_, membershipOnA) = await SeedVeAsync(dbContext, teamA, "N2SPG", "Sam Granger");

        var management = CreateManagement(dbContext);
        var (_, teamBTag) = await management.CreateTagAsync(teamB.Id, "Team member", 0, null, null, null, userId: 1, CancellationToken.None);

        var result = await management.SetTagsAsync(membershipOnA.Id, [teamBTag!.Id], userId: 1, CancellationToken.None);

        Assert.Equal(VeManagementResult.TagNotOnThisTeam, result);
        Assert.Empty(dbContext.VeTagAssignments);
    }

    [Fact]
    public async Task SetTags_ReplacesTheWholeSet()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, "TEAM-A");
        var (_, membership) = await SeedVeAsync(dbContext, team, "N2SPG", "Sam Granger");

        var management = CreateManagement(dbContext);
        var (_, member) = await management.CreateTagAsync(team.Id, "Team member", 0, null, null, null, 1, CancellationToken.None);
        var (_, lead) = await management.CreateTagAsync(team.Id, "Team lead", 1, null, null, null, 1, CancellationToken.None);

        await management.SetTagsAsync(membership.Id, [member!.Id, lead!.Id], 1, CancellationToken.None);
        Assert.Equal(2, dbContext.VeTagAssignments.Count());

        await management.SetTagsAsync(membership.Id, [lead.Id], 1, CancellationToken.None);
        Assert.Equal(lead.Id, Assert.Single(dbContext.VeTagAssignments).VeTagId);
    }

    /// <summary>
    /// The phase 1 merge deliberately leaves same-call-sign-different-name rows alone, because
    /// merging two people cannot be undone. They have to be visible, or the data quietly stays wrong.
    /// </summary>
    [Fact]
    public async Task RowsSharingACallSign_AreFlaggedAsPossibleDuplicates()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, "TEAM-A");
        await SeedVeAsync(dbContext, team, "N2SPG", "Sam Granger");
        await SeedVeAsync(dbContext, team, "N2SPG", "Someone Else");
        await SeedVeAsync(dbContext, team, "NP2UU", "Uma Unwin");

        var rows = await new VolunteerExaminerDirectoryService(dbContext)
            .GetDirectoryAsync([team.Id], new VeDirectoryFilter(), Now, CancellationToken.None);

        Assert.Equal(2, rows.Count(r => r.HasDuplicateCallSign));
        Assert.False(rows.Single(r => r.VolunteerExaminer.CallSign == "NP2UU").HasDuplicateCallSign);
    }

    /// <summary>
    /// ExamTools' "&lt;UNKNOWN&gt;" placeholder is shared by every VE it has no call sign for, so
    /// those rows always collide — but they are known to be different people, not suspected
    /// duplicates. Flagging them is noise that trains people to ignore the marker where it matters.
    /// </summary>
    [Fact]
    public async Task PlaceholderCallSigns_AreNotFlaggedAsDuplicates()
    {
        await using var dbContext = CreateContext();
        var teamA = await SeedTeamAsync(dbContext, "TEAM-A");
        var teamB = await SeedTeamAsync(dbContext, "TEAM-B");
        await SeedVeAsync(dbContext, teamA, "<UNKNOWN>", "<UNKNOWN>");
        await SeedVeAsync(dbContext, teamB, "<UNKNOWN>", "<UNKNOWN>");

        var rows = await new VolunteerExaminerDirectoryService(dbContext)
            .GetDirectoryAsync(null, new VeDirectoryFilter(), Now, CancellationToken.None);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.False(r.HasDuplicateCallSign));
    }

    /// <summary>
    /// An admin must be able to set the address, or a VE with none can never start self-service and
    /// nobody can fix it — the hole found the first time the flow was tried for real (2026-08-07).
    /// </summary>
    [Fact]
    public async Task AdminCanSetTheEmail()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, "TEAM-A");
        var (person, _) = await SeedVeAsync(dbContext, team, "N2SPG", "Sam Granger");

        var result = await CreateManagement(dbContext).UpdateContactDetailsAsync(
            person.Id,
            new VeContactDetails("Sam Granger", "sam@example.com", null, null, null, null, null, null, null,
                VeContactPreference.Email, null),
            userId: 1, CancellationToken.None);

        Assert.Equal(VeManagementResult.Success, result);
        Assert.Equal("sam@example.com", (await dbContext.VolunteerExaminers.FirstAsync(v => v.Id == person.Id)).Email);
    }

    /// <summary>Sign-in resolves an address to one person, so the admin path needs the same uniqueness rule as the self-service one.</summary>
    [Fact]
    public async Task AdminCannotGiveTwoVesTheSameEmail()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, "TEAM-A");
        var (first, _) = await SeedVeAsync(dbContext, team, "N2SPG", "Sam Granger");
        var (second, _) = await SeedVeAsync(dbContext, team, "NP2UU", "Uma Unwin");

        var management = CreateManagement(dbContext);
        await management.UpdateContactDetailsAsync(first.Id,
            new VeContactDetails("Sam Granger", "shared@example.com", null, null, null, null, null, null, null,
                VeContactPreference.Email, null), 1, CancellationToken.None);

        var result = await management.UpdateContactDetailsAsync(second.Id,
            new VeContactDetails("Uma Unwin", "shared@example.com", null, null, null, null, null, null, null,
                VeContactPreference.Email, null), 1, CancellationToken.None);

        Assert.Equal(VeManagementResult.EmailAlreadyInUse, result);
    }

    /// <summary>An admin changing the sign-in address is worth finding later, so it is called out rather than folded into "details updated".</summary>
    [Fact]
    public async Task AdminEmailChange_IsCalledOutInTheAudit()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, "TEAM-A");
        var (person, _) = await SeedVeAsync(dbContext, team, "N2SPG", "Sam Granger");

        await CreateManagement(dbContext).UpdateContactDetailsAsync(person.Id,
            new VeContactDetails("Sam Granger", "sam@example.com", null, null, null, null, null, null, null,
                VeContactPreference.Email, null), 1, CancellationToken.None);

        var audit = dbContext.AuditLogs.Single(a => a.Action == "VeContactDetailsUpdated");
        Assert.Contains("Email address was changed by an admin", audit.Details);
    }

    [Fact]
    public async Task Accreditation_IsRecordedOncePerVec()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, "TEAM-A");
        var (person, _) = await SeedVeAsync(dbContext, team, "N2SPG", "Sam Granger");
        var vec = new Vec { Name = "ARRL" };
        dbContext.Vecs.Add(vec);
        await dbContext.SaveChangesAsync();

        var management = CreateManagement(dbContext);
        Assert.Equal(VeManagementResult.Success,
            await management.AddAccreditationAsync(person.Id, vec.Id, 1, CancellationToken.None));
        Assert.Equal(VeManagementResult.AlreadyAccredited,
            await management.AddAccreditationAsync(person.Id, vec.Id, 1, CancellationToken.None));

        Assert.Single(dbContext.VeVecAccreditations);
    }

    /// <summary>
    /// The audition report's whole point: how many sessions has this person actually worked.
    ///
    /// <para><b>A future session must not count.</b> That is not hypothetical — this exact figure
    /// counted scheduled-but-unrun sessions until 2026-08-06, because the filter used
    /// <c>Status == Active</c>, which only ever means "not cancelled". Someone rostered onto next
    /// month's session already had it in their total.</para>
    /// </summary>
    [Fact]
    public async Task SessionsWorkedCountsFinishedSessionsOnly()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, "HRCC");
        var (person, _) = await SeedVeAsync(dbContext, team, "K1CEH", "Charles E. Hale");

        var first = await SeedSessionAsync(dbContext, team, Now.AddDays(-60), finished: true);
        var second = await SeedSessionAsync(dbContext, team, Now.AddDays(-30), finished: true);
        var upcoming = await SeedSessionAsync(dbContext, team, Now.AddDays(30), finished: false);
        foreach (var session in new[] { first, second, upcoming })
        {
            dbContext.SessionVolunteerExaminers.Add(new SessionVolunteerExaminer { Session = session, VolunteerExaminer = person });
        }

        await dbContext.SaveChangesAsync();

        var row = Assert.Single(await new VolunteerExaminerDirectoryService(dbContext)
            .GetDirectoryAsync([team.Id], new VeDirectoryFilter(), Now, CancellationToken.None));

        Assert.Equal(2, row.SessionsWorked);
    }

    /// <summary>
    /// A hand-added prospect — someone the team is watching who has never worked a session — reports
    /// zero rather than being absent or null. On an audition list that row is the whole point.
    /// </summary>
    [Fact]
    public async Task SomeoneWhoHasNeverWorkedReportsZero()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, "HRCC");
        await SeedVeAsync(dbContext, team, "AI5ZZ", "AI5ZZ");

        var row = Assert.Single(await new VolunteerExaminerDirectoryService(dbContext)
            .GetDirectoryAsync([team.Id], new VeDirectoryFilter(), Now, CancellationToken.None));

        Assert.Equal(0, row.SessionsWorked);
        Assert.Null(row.LastWorkedUtc);
    }

    /// <summary>
    /// Scoped like LastWorkedUtc: filtered to one team the count answers "how many did they work for
    /// YOU", and only widens when both teams are in view. A global count would quietly answer a
    /// different question than the filter above it claims to ask.
    /// </summary>
    [Fact]
    public async Task SessionsWorkedFollowsTheTeamsInScope()
    {
        await using var dbContext = CreateContext();
        var teamA = await SeedTeamAsync(dbContext, "HRCC");
        var teamB = await SeedTeamAsync(dbContext, "MARC");
        var person = new VolunteerExaminer { Name = "Shared Person", CallSign = "W1AW", CreatedUtc = Now };
        dbContext.VolunteerExaminers.Add(person);
        dbContext.VeTeamMemberships.Add(new VeTeamMembership { VolunteerExaminer = person, Team = teamA, IsActive = true, CreatedUtc = Now });
        dbContext.VeTeamMemberships.Add(new VeTeamMembership { VolunteerExaminer = person, Team = teamB, IsActive = true, CreatedUtc = Now });

        var forA = await SeedSessionAsync(dbContext, teamA, Now.AddDays(-60), finished: true);
        var forB1 = await SeedSessionAsync(dbContext, teamB, Now.AddDays(-30), finished: true);
        var forB2 = await SeedSessionAsync(dbContext, teamB, Now.AddDays(-10), finished: true);
        foreach (var session in new[] { forA, forB1, forB2 })
        {
            dbContext.SessionVolunteerExaminers.Add(new SessionVolunteerExaminer { Session = session, VolunteerExaminer = person });
        }

        await dbContext.SaveChangesAsync();
        var service = new VolunteerExaminerDirectoryService(dbContext);

        Assert.Equal(1, Assert.Single(await service.GetDirectoryAsync([teamA.Id], new VeDirectoryFilter(), Now, CancellationToken.None)).SessionsWorked);
        Assert.Equal(2, Assert.Single(await service.GetDirectoryAsync([teamB.Id], new VeDirectoryFilter(), Now, CancellationToken.None)).SessionsWorked);
        Assert.Equal(3, Assert.Single(await service.GetDirectoryAsync(null, new VeDirectoryFilter(), Now, CancellationToken.None)).SessionsWorked);
    }
}
