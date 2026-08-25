using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Messaging;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// The recipient axis of `docs/trigger-recipient-matrix.md` — every trigger point should be able to
/// send to a candidate, the session lead, or an admin role, rather than a trigger owning its
/// recipients.
///
/// <para>Two of these were declared in the model and refused outright by the dispatcher
/// (<c>SessionLead</c>, <c>DiscordChannel</c>), and the role recipients did not exist at all.</para>
///
/// <para>⚠️ <b>Session lead and "all SMs" are different populations from different systems.</b> The
/// lead comes from ExamTools (<c>Session.TeamLeadCallSign</c> → VE record → that VE's email) and may
/// have no app account; the role recipients come from Identity and those users need not be VEs.
/// "Team Lead = SM" is true of the people and false of the plumbing, so these must not be merged.</para>
/// </summary>
public class MessageRecipientResolutionTests
{
    private static readonly DateTime Now = new(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<Team> SeedTeamAsync(AppDbContext dbContext, string name = "HRCC")
    {
        var team = new Team { Name = name, CreatedUtc = Now };
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        return team;
    }

    private static async Task<User> SeedUserAsync(AppDbContext dbContext, Team team, UserRole role, string email, bool onTeam = true)
    {
        var user = new User { Name = email, Email = email, UserName = email, Role = role };
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();

        if (onTeam)
        {
            dbContext.UserTeams.Add(new UserTeam { UserId = user.Id, TeamId = team.Id });
            await dbContext.SaveChangesAsync();
        }

        return user;
    }

    // ---- Session lead ------------------------------------------------------------------------

    /// <summary>
    /// The cheapest recipient on the board, and the one I first reported as absent: the call sign →
    /// VE → email lookup already existed for Reply-To. This wires the same resolution to the To line.
    /// </summary>
    [Fact]
    public async Task SessionLead_ResolvesThroughTheVeRecord()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        dbContext.VolunteerExaminers.Add(new VolunteerExaminer { CallSign = "W9NB", Name = "Lead VE", Email = "lead@example.org" });
        await dbContext.SaveChangesAsync();

        var addresses = await MessageRecipientResolver.ResolveAsync(
            dbContext, team, MessageRecipient.SessionLead, sessionLeadCallSign: "w9nb", candidateEmail: null,
            teamAdminAddress: "admin@example.org", CancellationToken.None);

        Assert.Equal(["lead@example.org"], addresses);
    }

    /// <summary>
    /// ⚠️ ExamTools puts a literal <c>&lt;UNKNOWN&gt;</c> in this field, which once fused two people
    /// into one VE record. <c>CallSign.Normalize</c> refuses a placeholder rather than looking one up,
    /// and that behaviour has to survive being reused here.
    /// </summary>
    [Theory]
    [InlineData("<UNKNOWN>")]
    [InlineData("")]
    [InlineData(null)]
    public async Task AnUnusableLeadCallSign_ResolvesToNobody(string? callSign)
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);

        var addresses = await MessageRecipientResolver.ResolveAsync(
            dbContext, team, MessageRecipient.SessionLead, callSign, candidateEmail: null,
            teamAdminAddress: "admin@example.org", CancellationToken.None);

        Assert.Empty(addresses);
    }

    /// <summary>A lead with a VE record but no email on it is nobody, not the team address — silently redirecting a message to a different person is worse than not sending it.</summary>
    [Fact]
    public async Task ALeadWithNoEmail_ResolvesToNobody_RatherThanFallingBackToTheTeam()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        dbContext.VolunteerExaminers.Add(new VolunteerExaminer { CallSign = "W9NB", Name = "Lead VE", Email = null });
        await dbContext.SaveChangesAsync();

        var addresses = await MessageRecipientResolver.ResolveAsync(
            dbContext, team, MessageRecipient.SessionLead, "W9NB", candidateEmail: null,
            teamAdminAddress: "admin@example.org", CancellationToken.None);

        Assert.Empty(addresses);
    }

    // ---- Admin roles -------------------------------------------------------------------------

    [Fact]
    public async Task TeamAdmins_ResolveToEveryTeamAdminOnTheTeam()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedUserAsync(dbContext, team, UserRole.TeamAdmin, "ta1@example.org");
        await SeedUserAsync(dbContext, team, UserRole.TeamAdmin, "ta2@example.org");
        await SeedUserAsync(dbContext, team, UserRole.SessionManager, "sm@example.org");

        var addresses = await MessageRecipientResolver.ResolveAsync(
            dbContext, team, MessageRecipient.TeamAdmins, null, null, "admin@example.org", CancellationToken.None);

        Assert.Equal(["ta1@example.org", "ta2@example.org"], addresses.Order());
    }

    /// <summary>Mike, 2026-08-20: "All SMs is a third role option." Distinct from the session lead.</summary>
    [Fact]
    public async Task SessionManagers_ResolveToEverySessionManagerOnTheTeam()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedUserAsync(dbContext, team, UserRole.SessionManager, "sm1@example.org");
        await SeedUserAsync(dbContext, team, UserRole.SessionManager, "sm2@example.org");
        await SeedUserAsync(dbContext, team, UserRole.TeamAdmin, "ta@example.org");

        var addresses = await MessageRecipientResolver.ResolveAsync(
            dbContext, team, MessageRecipient.SessionManagers, null, null, "admin@example.org", CancellationToken.None);

        Assert.Equal(["sm1@example.org", "sm2@example.org"], addresses.Order());
    }

    /// <summary>
    /// ⚠️ Team scoping is the whole safety property here. A rule on one team resolving another team's
    /// admins would mail one team's candidate data to another team's staff.
    /// </summary>
    [Fact]
    public async Task AnotherTeamsAdmins_AreNeverResolved()
    {
        await using var dbContext = CreateContext();
        var mine = await SeedTeamAsync(dbContext);
        var theirs = await SeedTeamAsync(dbContext, "MARC");
        await SeedUserAsync(dbContext, theirs, UserRole.TeamAdmin, "theirs@example.org");

        var addresses = await MessageRecipientResolver.ResolveAsync(
            dbContext, mine, MessageRecipient.TeamAdmins, null, null, "admin@example.org", CancellationToken.None);

        Assert.Empty(addresses);
    }

    /// <summary>
    /// SystemAdmins are deliberately not team-scoped — they span every team by definition, and
    /// <c>GetEffectiveTeamIds</c> returns null for them meaning "all teams" rather than "none".
    /// </summary>
    [Fact]
    public async Task SystemAdmins_ResolveWithoutATeamMembership()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedUserAsync(dbContext, team, UserRole.SystemAdmin, "sysadmin@example.org", onTeam: false);

        var addresses = await MessageRecipientResolver.ResolveAsync(
            dbContext, team, MessageRecipient.SystemAdmins, null, null, "admin@example.org", CancellationToken.None);

        Assert.Equal(["sysadmin@example.org"], addresses);
    }

    /// <summary>A user with no email address is skipped rather than producing an empty To line.</summary>
    [Fact]
    public async Task AUserWithNoEmail_IsSkipped()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var user = await SeedUserAsync(dbContext, team, UserRole.TeamAdmin, "ta@example.org");
        user.Email = null;
        await dbContext.SaveChangesAsync();

        var addresses = await MessageRecipientResolver.ResolveAsync(
            dbContext, team, MessageRecipient.TeamAdmins, null, null, "admin@example.org", CancellationToken.None);

        Assert.Empty(addresses);
    }

    /// <summary>Two roles resolving the same person send one message, not two.</summary>
    [Fact]
    public async Task DuplicateAddresses_AreCollapsed()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedUserAsync(dbContext, team, UserRole.TeamAdmin, "same@example.org");
        await SeedUserAsync(dbContext, team, UserRole.TeamAdmin, "SAME@example.org");

        var addresses = await MessageRecipientResolver.ResolveAsync(
            dbContext, team, MessageRecipient.TeamAdmins, null, null, "admin@example.org", CancellationToken.None);

        Assert.Single(addresses);
    }

    // ---- The unchanged two -------------------------------------------------------------------

    [Fact]
    public async Task Candidate_StillResolvesToTheCandidate()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);

        var addresses = await MessageRecipientResolver.ResolveAsync(
            dbContext, team, MessageRecipient.Candidate, null, "candidate@example.org", "admin@example.org", CancellationToken.None);

        Assert.Equal(["candidate@example.org"], addresses);
    }

    [Fact]
    public async Task TeamAdminAddress_StillResolvesToTheConfiguredAddress()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);

        var addresses = await MessageRecipientResolver.ResolveAsync(
            dbContext, team, MessageRecipient.TeamAdminAddress, null, null, "admin@example.org", CancellationToken.None);

        Assert.Equal(["admin@example.org"], addresses);
    }

    /// <summary>
    /// A channel post is not an address, and must never be resolved into one — the Discord path
    /// deliberately builds no <c>EmailMessage</c> at all.
    /// </summary>
    [Fact]
    public async Task DiscordChannel_ResolvesToNoEmailAddress()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);

        var addresses = await MessageRecipientResolver.ResolveAsync(
            dbContext, team, MessageRecipient.DiscordChannel, null, "candidate@example.org", "admin@example.org", CancellationToken.None);

        Assert.Empty(addresses);
    }
}

/// <summary>
/// `LegalRecipients` versus the decided matrix in `docs/trigger-recipient-matrix.md`.
///
/// <para>The doc says the table <b>is</b> the spec, which is only true if something checks. Without
/// this, the table and the code drift the moment a trigger is added — and the drift is silent,
/// because an over-permissive `LegalRecipients` just quietly offers a recipient nobody agreed to.</para>
/// </summary>
public class LegalRecipientMatrixTests
{
    /// <summary>Every trigger may address the staff recipients — that is the whole generalization.</summary>
    [Theory]
    [InlineData(MessageTrigger.CandidateRegistered)]
    [InlineData(MessageTrigger.BeforeSessionStart)]
    [InlineData(MessageTrigger.FccFeeOutstanding)]
    [InlineData(MessageTrigger.PaymentUnpaidBeforeSession)]
    [InlineData(MessageTrigger.CandidateTested)]
    [InlineData(MessageTrigger.LicenseGranted)]
    [InlineData(MessageTrigger.FelonyDisclosureDeclared)]
    public void EveryTrigger_MayAddressTheStaffRecipients(MessageTrigger trigger)
    {
        var legal = MessageTriggerDefinitions.For(trigger).LegalRecipients;

        Assert.Contains(MessageRecipient.SessionLead, legal);
        Assert.Contains(MessageRecipient.TeamAdmins, legal);
        Assert.Contains(MessageRecipient.SystemAdmins, legal);
        Assert.Contains(MessageRecipient.SessionManagers, legal);
    }

    /// <summary>
    /// ⚠️ The one row that is a privacy decision rather than a preference. A felony disclosure
    /// reaching a channel is a disclosure about a person to an audience with no need for it — a
    /// different class of mistake from an over-chatty reminder.
    /// </summary>
    [Fact]
    public void FelonyDisclosure_MayNeverGoToADiscordChannel()
        => Assert.DoesNotContain(MessageRecipient.DiscordChannel,
            MessageTriggerDefinitions.For(MessageTrigger.FelonyDisclosureDeclared).LegalRecipients);

    /// <summary>
    /// Only the session reminder is VE-facing. Every other trigger was marked N for the channel
    /// column — and under Mike's ruling, unmarked means No, so this is a decision rather than an
    /// omission.
    /// </summary>
    [Theory]
    [InlineData(MessageTrigger.BeforeSessionStart, true)]
    [InlineData(MessageTrigger.CandidateRegistered, false)]
    [InlineData(MessageTrigger.FccFeeOutstanding, false)]
    [InlineData(MessageTrigger.PaymentUnpaidBeforeSession, false)]
    [InlineData(MessageTrigger.CandidateTested, false)]
    [InlineData(MessageTrigger.LicenseGranted, false)]
    public void OnlyTheSessionReminder_MayPostToADiscordChannel(MessageTrigger trigger, bool allowed)
        => Assert.Equal(allowed,
            MessageTriggerDefinitions.For(trigger).LegalRecipients.Contains(MessageRecipient.DiscordChannel));

    /// <summary>Every recipient a trigger declares legal must have a label, or the picker renders an enum name at somebody.</summary>
    [Fact]
    public void EveryLegalRecipient_HasALabel()
    {
        foreach (var recipient in Enum.GetValues<MessageRecipient>())
        {
            Assert.NotEqual(recipient.ToString(), MessageTriggerLabels.Label(recipient));
        }
    }
}
