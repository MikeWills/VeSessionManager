using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Email;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Integrations;
using VeSessionManager.Core.Messaging;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// The three trigger points added in PR3 (#401): <c>CandidateTested</c>, <c>LicenseGranted</c> and
/// <c>FelonyDisclosureDeclared</c>.
///
/// <para>Unlike PR1's, these reproduce nothing — they are things the app could not do before — so
/// there was no prior behaviour to port. What each one owes instead is a moment it can be bounded by,
/// and the guards its subject matter demands: not congratulating somebody who walked in already
/// licensed, and not telling the wrong person their felony disclosure needs FCC paperwork.</para>
/// </summary>
public class NewTriggerScannerTests
{
    private static readonly DateTime Now = new(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private sealed class FakeEmailSender : IEmailSender
    {
        public List<EmailMessage> SentMessages { get; } = [];

        public Task SendAsync(EmailCredentials credentials, EmailMessage message, CancellationToken cancellationToken)
        {
            SentMessages.Add(message);
            return Task.CompletedTask;
        }
    }

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static Task<MessageRuleResult> RunAsync(AppDbContext dbContext, IEmailSender sender, Team team, MessageTrigger trigger) =>
        MessageRuleTestHarness.Create(dbContext, sender, new FixedTimeProvider(Now))
            .RunAsync(team, [trigger], null, CancellationToken.None);

    private static async Task<Team> SeedTeamAsync(AppDbContext dbContext)
    {
        var team = new Team
        {
            Name = "TESTTEAM",
            SmtpHost = "smtp.example.org",
            SmtpUsername = "smtp-user",
            SmtpPassword = "smtp-pass",
            CreatedUtc = Now
        };
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();

        dbContext.EmailSettings.Add(new EmailSettings
        {
            TeamId = team.Id,
            FromAddress = "noreply@example.org",
            ReplyToAddress = "reply@example.org",
            PrivacyPolicyUrl = "https://example.org/privacy",
            AdminNotificationEmail = "admin@example.org"
        });
        dbContext.EmailTemplates.Add(new EmailTemplate
        {
            TeamId = team.Id,
            Key = "Anything",
            Subject = "For {{CandidateName}}",
            Body = "Hi {{CandidateFirstName}}, call sign {{CallSign}}, session {{SessionDate}}."
        });
        await dbContext.SaveChangesAsync();
        return team;
    }

    /// <summary>Created a year back so <c>CreatedUtc</c> bounds nothing — the tests about that bound pass their own.</summary>
    private static async Task<MessageRule> SeedRuleAsync(
        AppDbContext dbContext, Team team, MessageTrigger trigger, DateTime? createdUtc = null)
    {
        var rule = MessageRuleTestHarness.NewRule(team, trigger, "Anything", null, createdUtc ?? Now.AddYears(-1));
        dbContext.MessageRules.Add(rule);
        await dbContext.SaveChangesAsync();
        return rule;
    }

    private static async Task<Session> SeedSessionAsync(AppDbContext dbContext, Team team, DateTime? startUtc = null)
    {
        var vec = new Vec { Name = "ARRL" };
        var user = new User { Name = "System", Email = $"system-{Guid.NewGuid():N}@localhost", Role = UserRole.SystemAdmin };
        var session = new Session
        {
            ExamToolsSessionId = $"s-{Guid.NewGuid():N}",
            Title = "August Session",
            ScheduledStartUtc = startUtc ?? Now.AddDays(2),
            DurationMinutes = 60,
            Vec = vec,
            TeamId = team.Id,
            FeeConfiguration = new FeeConfiguration
            {
                Vec = vec,
                EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                FeeCollectionEnabled = false,
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

    private static Candidate NewCandidate(Session session, string applicantId = "applicant-1") => new()
    {
        ExamToolsApplicantId = applicantId,
        SessionId = session.Id,
        Name = "Roana Glory",
        FirstName = "Roana",
        Email = "roana@example.com",
        DateRegisteredUtc = Now.AddDays(-3)
    };

    // ---- CandidateTested ----

    [Fact]
    public async Task CandidateTested_FiresForSomeoneWhoHasTested()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedRuleAsync(dbContext, team, MessageTrigger.CandidateTested);
        var session = await SeedSessionAsync(dbContext, team, Now.AddDays(-1));
        var candidate = NewCandidate(session);
        candidate.MarkTested(Now.AddHours(-2));
        dbContext.Candidates.Add(candidate);
        await dbContext.SaveChangesAsync();

        var sender = new FakeEmailSender();
        Assert.Equal(1, (await RunAsync(dbContext, sender, team, MessageTrigger.CandidateTested)).Sent);
        Assert.Single(sender.SentMessages);
    }

    [Fact]
    public async Task CandidateTested_DoesNotFireForSomeoneWhoHasNot()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedRuleAsync(dbContext, team, MessageTrigger.CandidateTested);
        var session = await SeedSessionAsync(dbContext, team);
        dbContext.Candidates.Add(NewCandidate(session));
        await dbContext.SaveChangesAsync();

        Assert.Equal(0, (await RunAsync(dbContext, new FakeEmailSender(), team, MessageTrigger.CandidateTested)).Sent);
    }

    /// <summary>
    /// Every candidate who tested before <c>TestedUtc</c> existed holds null, and a rule must never
    /// reach them — that null is what stops a new rule emailing a year of imported history on its
    /// first tick, in place of an age window.
    /// </summary>
    [Fact]
    public async Task CandidateTested_DoesNotFireForARowFromBeforeTheTimestampExisted()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedRuleAsync(dbContext, team, MessageTrigger.CandidateTested);
        var session = await SeedSessionAsync(dbContext, team, Now.AddMonths(-8));
        var candidate = NewCandidate(session);
        // The shape a pre-migration row has: tested, with no record of when.
        candidate.Tested = true;
        candidate.TestedUtc = null;
        dbContext.Candidates.Add(candidate);
        await dbContext.SaveChangesAsync();

        Assert.Equal(0, (await RunAsync(dbContext, new FakeEmailSender(), team, MessageTrigger.CandidateTested)).Sent);
    }

    [Fact]
    public async Task CandidateTested_DoesNotFireForSomeoneTestedBeforeTheRuleExisted()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedRuleAsync(dbContext, team, MessageTrigger.CandidateTested, createdUtc: Now.AddHours(-1));
        var session = await SeedSessionAsync(dbContext, team, Now.AddDays(-2));
        var candidate = NewCandidate(session);
        candidate.MarkTested(Now.AddDays(-2));
        dbContext.Candidates.Add(candidate);
        await dbContext.SaveChangesAsync();

        Assert.Equal(0, (await RunAsync(dbContext, new FakeEmailSender(), team, MessageTrigger.CandidateTested)).Sent);
    }

    /// <summary>A withdrawn candidate never sat anything, whatever a bulk "mark session completed" left on the row.</summary>
    [Fact]
    public async Task CandidateTested_DoesNotFireForAWithdrawnCandidate()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedRuleAsync(dbContext, team, MessageTrigger.CandidateTested);
        var session = await SeedSessionAsync(dbContext, team, Now.AddDays(-1));
        var candidate = NewCandidate(session);
        candidate.MarkTested(Now.AddHours(-2));
        candidate.ApplicationStatus = CandidateApplicationStatus.NotTested;
        dbContext.Candidates.Add(candidate);
        await dbContext.SaveChangesAsync();

        Assert.Equal(0, (await RunAsync(dbContext, new FakeEmailSender(), team, MessageTrigger.CandidateTested)).Sent);
    }

    /// <summary>The timestamp records the first time, so re-seeing an already-tested candidate does not move the moment out from under a rule.</summary>
    [Fact]
    public void MarkTested_KeepsTheFirstTimestamp()
    {
        var candidate = new Candidate { ExamToolsApplicantId = "a", SessionId = 1, DateRegisteredUtc = Now };

        candidate.MarkTested(Now);
        candidate.MarkTested(Now.AddDays(3));

        Assert.Equal(Now, candidate.TestedUtc);
        Assert.True(candidate.Tested);
    }

    // ---- LicenseGranted ----

    private static Candidate Granted(Session session, DateTime grantDateUtc, string applicantId = "applicant-1")
    {
        var candidate = NewCandidate(session, applicantId);
        candidate.MarkTested(Now.AddDays(-1));
        candidate.ApplicationStatus = CandidateApplicationStatus.Granted;
        candidate.CallSign = "KE0ABC";
        candidate.LicenseGrantDateUtc = grantDateUtc;
        return candidate;
    }

    [Fact]
    public async Task LicenseGranted_FiresOnceTheFccHasIssued_AndTheCallSignRenders()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedRuleAsync(dbContext, team, MessageTrigger.LicenseGranted);
        var session = await SeedSessionAsync(dbContext, team, Now.AddDays(-5));
        dbContext.Candidates.Add(Granted(session, Now.AddDays(-1)));
        await dbContext.SaveChangesAsync();

        var sender = new FakeEmailSender();
        Assert.Equal(1, (await RunAsync(dbContext, sender, team, MessageTrigger.LicenseGranted)).Sent);
        // Asserted on the rendered body, not on the placeholder being passed (#205) — this is the one
        // trigger where {{CallSign}} is the reason the email exists.
        Assert.Contains("call sign KE0ABC", Assert.Single(sender.SentMessages).HtmlBody);
    }

    /// <summary>
    /// Somebody who walked in already licensed did not earn a call sign here, and "congratulations on
    /// your new call sign" is wrong for them. The check needs the session loaded, so it runs in memory.
    /// </summary>
    [Fact]
    public async Task LicenseGranted_DoesNotFireForAnUpgraderWhoseLicensePredatesTheSession()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedRuleAsync(dbContext, team, MessageTrigger.LicenseGranted);
        var session = await SeedSessionAsync(dbContext, team, Now.AddDays(-5));
        dbContext.Candidates.Add(Granted(session, Now.AddYears(-3)));
        await dbContext.SaveChangesAsync();

        Assert.Equal(0, (await RunAsync(dbContext, new FakeEmailSender(), team, MessageTrigger.LicenseGranted)).Sent);
    }

    [Fact]
    public async Task LicenseGranted_DoesNotFireWithoutACallSign()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedRuleAsync(dbContext, team, MessageTrigger.LicenseGranted);
        var session = await SeedSessionAsync(dbContext, team, Now.AddDays(-5));
        var candidate = Granted(session, Now.AddDays(-1));
        candidate.CallSign = null;
        dbContext.Candidates.Add(candidate);
        await dbContext.SaveChangesAsync();

        Assert.Equal(0, (await RunAsync(dbContext, new FakeEmailSender(), team, MessageTrigger.LicenseGranted)).Sent);
    }

    [Fact]
    public async Task LicenseGranted_DoesNotFireForALicenseGrantedBeforeTheRuleExisted()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedRuleAsync(dbContext, team, MessageTrigger.LicenseGranted, createdUtc: Now);
        var session = await SeedSessionAsync(dbContext, team, Now.AddDays(-10));
        dbContext.Candidates.Add(Granted(session, Now.AddDays(-2)));
        await dbContext.SaveChangesAsync();

        Assert.Equal(0, (await RunAsync(dbContext, new FakeEmailSender(), team, MessageTrigger.LicenseGranted)).Sent);
    }

    // ---- FelonyDisclosureDeclared ----

    [Fact]
    public async Task FelonyDisclosure_FiresBeforeTheSession_WithoutWaitingForAnyResult()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedRuleAsync(dbContext, team, MessageTrigger.FelonyDisclosureDeclared);
        var session = await SeedSessionAsync(dbContext, team, Now.AddDays(2));
        var candidate = NewCandidate(session);
        candidate.HasFelonyDisclosure = true;
        dbContext.Candidates.Add(candidate);
        await dbContext.SaveChangesAsync();

        var sender = new FakeEmailSender();
        Assert.Equal(1, (await RunAsync(dbContext, sender, team, MessageTrigger.FelonyDisclosureDeclared)).Sent);
        // The whole reason #221 moved it off "mark session completed": it is only useful while there
        // is still somebody to ask.
        Assert.False(dbContext.Candidates.Single().Tested);
    }

    /// <summary>
    /// Null means ExamTools told us nothing, which is not the same as "no" — and telling the wrong
    /// person their felony disclosure needs FCC paperwork is the mistake worth guarding twice.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public async Task FelonyDisclosure_DoesNotFireWithoutADeclaration(bool? hasFelonyDisclosure)
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedRuleAsync(dbContext, team, MessageTrigger.FelonyDisclosureDeclared);
        var session = await SeedSessionAsync(dbContext, team);
        var candidate = NewCandidate(session);
        candidate.HasFelonyDisclosure = hasFelonyDisclosure;
        dbContext.Candidates.Add(candidate);
        await dbContext.SaveChangesAsync();

        Assert.Equal(0, (await RunAsync(dbContext, new FakeEmailSender(), team, MessageTrigger.FelonyDisclosureDeclared)).Sent);
    }

    [Fact]
    public async Task FelonyDisclosure_DoesNotFireForASessionAlreadyOver()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedRuleAsync(dbContext, team, MessageTrigger.FelonyDisclosureDeclared);
        var session = await SeedSessionAsync(dbContext, team, Now.AddHours(-3));
        var candidate = NewCandidate(session);
        candidate.HasFelonyDisclosure = true;
        dbContext.Candidates.Add(candidate);
        await dbContext.SaveChangesAsync();

        Assert.Equal(0, (await RunAsync(dbContext, new FakeEmailSender(), team, MessageTrigger.FelonyDisclosureDeclared)).Sent);
    }

    /// <summary>It still writes the display timestamp the candidate's email history renders, exactly as the button does.</summary>
    [Fact]
    public async Task FelonyDisclosure_StampsTheSameColumnTheButtonDoes()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedRuleAsync(dbContext, team, MessageTrigger.FelonyDisclosureDeclared);
        var session = await SeedSessionAsync(dbContext, team, Now.AddDays(2));
        var candidate = NewCandidate(session);
        candidate.HasFelonyDisclosure = true;
        dbContext.Candidates.Add(candidate);
        await dbContext.SaveChangesAsync();

        await RunAsync(dbContext, new FakeEmailSender(), team, MessageTrigger.FelonyDisclosureDeclared);

        Assert.Equal(Now, dbContext.Candidates.Single().FelonyDisclosureInstructionsSentUtc);
    }

    // ---- All three ----

    /// <summary>
    /// None of the three is seeded. They are things this app could not do before, not reproductions of
    /// prior behaviour, so an existing team's outgoing mail is unchanged until somebody says otherwise.
    /// </summary>
    [Theory]
    [InlineData(MessageTrigger.CandidateTested)]
    [InlineData(MessageTrigger.LicenseGranted)]
    [InlineData(MessageTrigger.FelonyDisclosureDeclared)]
    public async Task WithNoRule_NothingIsSent(MessageTrigger trigger)
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, team, Now.AddDays(-1));
        var candidate = Granted(session, Now.AddDays(-1));
        candidate.HasFelonyDisclosure = true;
        dbContext.Candidates.Add(candidate);
        await dbContext.SaveChangesAsync();

        var sender = new FakeEmailSender();
        Assert.Equal(0, (await RunAsync(dbContext, sender, team, trigger)).Sent);
        Assert.Empty(sender.SentMessages);
    }

    /// <summary>
    /// And the seeder does not add them either — a team set up after PR3 gets the same four rules a
    /// team set up before it has.
    /// </summary>
    [Fact]
    public async Task ANewTeamIsNotGivenAnyOfTheNewTriggers()
    {
        await using var dbContext = CreateContext();
        var team = new Team { Name = "FRESH", CreatedUtc = Now };
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();

        await EmailDefaultsSeeder.SeedForTeamAsync(dbContext, NullLogger.Instance, team);
        await dbContext.SaveChangesAsync();

        var triggers = await dbContext.MessageRules.Select(r => r.Trigger).ToListAsync();
        Assert.DoesNotContain(MessageTrigger.CandidateTested, triggers);
        Assert.DoesNotContain(MessageTrigger.LicenseGranted, triggers);
        Assert.DoesNotContain(MessageTrigger.FelonyDisclosureDeclared, triggers);
    }
}
