using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Email;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.VolunteerExaminers;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>Inviting a team's VEs to an upcoming session (issue #142 phase 6).</summary>
public class VeSessionInvitationServiceTests
{
    private static readonly DateTime Now = new(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private sealed class FakeEmailSender : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];
        public HashSet<string> FailFor { get; } = [];
        public bool IsConfigured => true;

        public Task SendAsync(EmailCredentials credentials, EmailMessage message, CancellationToken cancellationToken)
        {
            if (FailFor.Contains(message.ToAddress))
            {
                throw new InvalidOperationException("Simulated SMTP failure");
            }

            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static (VeSessionInvitationService Service, FakeEmailSender Email) Create(AppDbContext dbContext)
    {
        var email = new FakeEmailSender();
        return (new VeSessionInvitationService(dbContext, email, new FixedTimeProvider(Now),
            NullLogger<VeSessionInvitationService>.Instance), email);
    }

    private static async Task<(Team Team, Session Session)> SeedSessionAsync(AppDbContext dbContext, bool withEmailSettings = true, string? zoomUrl = "https://zoom.example/j/1")
    {
        var team = new Team
        {
            Name = "HRCC",
            ExamToolsTeamCode = "HRCC",
            SmtpHost = "smtp.example.com",
            SmtpUsername = "team@example.com",
            SmtpPassword = "secret",
            CreatedUtc = Now
        };
        var vec = new Vec { Name = "ARRL" };
        var user = new User { Name = "System", Email = "system@localhost", Role = UserRole.SystemAdmin };
        var session = new Session
        {
            ExamToolsSessionId = "session-1",
            Title = "August Session",
            ScheduledStartUtc = Now.AddDays(14),
            DurationMinutes = 60,
            Team = team,
            Vec = vec,
            ZoomJoinUrl = zoomUrl,
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

        if (withEmailSettings)
        {
            dbContext.EmailSettings.Add(new EmailSettings
            {
                Team = team,
                FromAddress = "team@example.com",
                FromDisplayName = "HRCC VE Team",
                ReplyToAddress = "team@example.com",
                AdminNotificationEmail = "admin@example.com",
                PrivacyPolicyUrl = "https://example.com/privacy"
            });
        }

        await dbContext.SaveChangesAsync();
        return (team, session);
    }

    private static async Task<VolunteerExaminer> SeedVeAsync(
        AppDbContext dbContext, Team team, string callSign, string? email,
        VeContactPreference preference = VeContactPreference.Email, bool active = true)
    {
        var person = new VolunteerExaminer
        {
            Name = $"VE {callSign}",
            CallSign = callSign,
            Email = email,
            ContactPreference = preference,
            CreatedUtc = Now
        };
        dbContext.VolunteerExaminers.Add(person);
        dbContext.VeTeamMemberships.Add(new VeTeamMembership
        {
            VolunteerExaminer = person, Team = team, IsActive = active, CreatedUtc = Now
        });
        await dbContext.SaveChangesAsync();
        return person;
    }

    [Fact]
    public async Task CandidatesAreTheTeamsActiveVes()
    {
        await using var dbContext = CreateContext();
        var (team, session) = await SeedSessionAsync(dbContext);
        await SeedVeAsync(dbContext, team, "N2SPG", "a@example.com");
        await SeedVeAsync(dbContext, team, "NP2UU", "b@example.com", active: false);
        var (service, _) = Create(dbContext);

        var candidates = await service.GetCandidatesAsync(session.Id, CancellationToken.None);

        Assert.Equal("N2SPG", Assert.Single(candidates).VolunteerExaminer.CallSign);
    }

    /// <summary>The point of the feature: nobody should have to go and find the Zoom link.</summary>
    [Fact]
    public async Task ZoomLinkIsSubstitutedIntoTheBody()
    {
        await using var dbContext = CreateContext();
        var (team, session) = await SeedSessionAsync(dbContext);
        var person = await SeedVeAsync(dbContext, team, "N2SPG", "a@example.com");
        var (service, email) = Create(dbContext);

        await service.SendAsync(session.Id, [person.Id], "Can you work {{SessionTitle}}?",
            "<p>Hi {{VeName}}, join at {{ZoomJoinUrl}} on {{SessionDate}}.</p>", 1, CancellationToken.None);

        var sent = Assert.Single(email.Sent);
        Assert.Equal("Can you work August Session?", sent.Subject);
        Assert.Contains("https://zoom.example/j/1", sent.HtmlBody);
        Assert.Contains("VE N2SPG", sent.HtmlBody);
        Assert.DoesNotContain("{{", sent.HtmlBody);
    }

    /// <summary>Counted rather than silently dropped — "8 of 10" with no explanation is worse than a number someone can act on.</summary>
    [Fact]
    public async Task VeWithNoEmail_IsCountedNotSkippedSilently()
    {
        await using var dbContext = CreateContext();
        var (team, session) = await SeedSessionAsync(dbContext);
        var withEmail = await SeedVeAsync(dbContext, team, "N2SPG", "a@example.com");
        var without = await SeedVeAsync(dbContext, team, "NP2UU", null);
        var (service, email) = Create(dbContext);

        var result = await service.SendAsync(session.Id, [withEmail.Id, without.Id], "Subject", "Body", 1, CancellationToken.None);

        Assert.Equal(1, result.Sent);
        Assert.Equal(1, result.NoEmailAddress);
        Assert.Single(email.Sent);
    }

    /// <summary>Text-only is unreachable until SMS exists. Honoured now so the loop does not need remembering later.</summary>
    [Fact]
    public async Task TextOnlyVe_IsSkippedAndCounted()
    {
        await using var dbContext = CreateContext();
        var (team, session) = await SeedSessionAsync(dbContext);
        var person = await SeedVeAsync(dbContext, team, "N2SPG", "a@example.com", VeContactPreference.Text);
        var (service, email) = Create(dbContext);

        var result = await service.SendAsync(session.Id, [person.Id], "Subject", "Body", 1, CancellationToken.None);

        Assert.Equal(0, result.Sent);
        Assert.Equal(1, result.TextOnlySkipped);
        Assert.Empty(email.Sent);
    }

    [Fact]
    public async Task BothPreference_StillGetsTheEmail()
    {
        await using var dbContext = CreateContext();
        var (team, session) = await SeedSessionAsync(dbContext);
        var person = await SeedVeAsync(dbContext, team, "N2SPG", "a@example.com", VeContactPreference.Both);
        var (service, email) = Create(dbContext);

        await service.SendAsync(session.Id, [person.Id], "Subject", "Body", 1, CancellationToken.None);

        Assert.Single(email.Sent);
    }

    /// <summary>One bad address must not stop the rest of the invitations, same as every other fan-out here.</summary>
    [Fact]
    public async Task OneFailedSend_DoesNotStopTheOthers()
    {
        await using var dbContext = CreateContext();
        var (team, session) = await SeedSessionAsync(dbContext);
        var first = await SeedVeAsync(dbContext, team, "N2SPG", "bad@example.com");
        var second = await SeedVeAsync(dbContext, team, "NP2UU", "good@example.com");
        var (service, email) = Create(dbContext);
        email.FailFor.Add("bad@example.com");

        var result = await service.SendAsync(session.Id, [first.Id, second.Id], "Subject", "Body", 1, CancellationToken.None);

        Assert.Equal(1, result.Sent);
        Assert.Equal(1, result.Failed);
        Assert.Equal("good@example.com", Assert.Single(email.Sent).ToAddress);
    }

    [Fact]
    public async Task TeamWithNoSmtp_SaysSoRatherThanThrowing()
    {
        await using var dbContext = CreateContext();
        var (team, session) = await SeedSessionAsync(dbContext);
        team.SmtpHost = null;
        team.SmtpUsername = null;
        await dbContext.SaveChangesAsync();
        var person = await SeedVeAsync(dbContext, team, "N2SPG", "a@example.com");
        var (service, email) = Create(dbContext);

        var result = await service.SendAsync(session.Id, [person.Id], "Subject", "Body", 1, CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Empty(email.Sent);
    }

    [Fact]
    public async Task EmptySubjectOrBody_IsRefused()
    {
        await using var dbContext = CreateContext();
        var (team, session) = await SeedSessionAsync(dbContext);
        var person = await SeedVeAsync(dbContext, team, "N2SPG", "a@example.com");
        var (service, email) = Create(dbContext);

        Assert.NotNull((await service.SendAsync(session.Id, [person.Id], "", "Body", 1, CancellationToken.None)).Error);
        Assert.NotNull((await service.SendAsync(session.Id, [person.Id], "Subject", "  ", 1, CancellationToken.None)).Error);
        Assert.Empty(email.Sent);
    }

    /// <summary>The team's own From/Reply-To, so an invitation does not arrive from a different address than everything else the team sends.</summary>
    [Fact]
    public async Task SendsFromTheTeamsConfiguredAddress()
    {
        await using var dbContext = CreateContext();
        var (team, session) = await SeedSessionAsync(dbContext);
        var person = await SeedVeAsync(dbContext, team, "N2SPG", "a@example.com");
        var (service, email) = Create(dbContext);

        await service.SendAsync(session.Id, [person.Id], "Subject", "Body", 1, CancellationToken.None);

        var sent = Assert.Single(email.Sent);
        Assert.Equal("team@example.com", sent.FromAddress);
        Assert.Equal("HRCC VE Team", sent.FromDisplayName);
    }

    /// <summary>The phase 3 eligibility check surfaces here, where it is most useful — inviting someone who cannot serve on the day is a wasted seat.</summary>
    [Fact]
    public async Task CandidatesCarryTheirEligibilityForThisSession()
    {
        await using var dbContext = CreateContext();
        var (team, session) = await SeedSessionAsync(dbContext);
        var person = await SeedVeAsync(dbContext, team, "N2SPG", "a@example.com");
        person.LicenseLastCheckedUtc = Now;
        person.OperatorClass = LicenseClass.Technician;      // below the General minimum
        person.LicenseExpiresUtc = Now.AddYears(3);
        await dbContext.SaveChangesAsync();
        var (service, _) = Create(dbContext);

        var candidate = Assert.Single(await service.GetCandidatesAsync(session.Id, CancellationToken.None));

        Assert.True(candidate.Eligibility.HasProblem);
    }

    [Fact]
    public async Task SendIsAudited()
    {
        await using var dbContext = CreateContext();
        var (team, session) = await SeedSessionAsync(dbContext);
        var person = await SeedVeAsync(dbContext, team, "N2SPG", "a@example.com");
        var (service, _) = Create(dbContext);

        await service.SendAsync(session.Id, [person.Id], "Subject", "Body", 1, CancellationToken.None);

        var audit = await dbContext.AuditLogs.SingleAsync(a => a.Action == "VeSessionInvitationsSent");
        Assert.Contains("1 sent", audit.Details);
    }

    // ---- #238: recipients must be scoped to the session's team ----

    /// <summary>
    /// The finding. The ids arrive from the posted form, so "the compose screen only offered team
    /// members" is a default, not a constraint — unscoped, a tampered POST sent attacker-authored
    /// subject and body from THIS team's SMTP credentials to any VolunteerExaminer on the
    /// deployment. The mail is indistinguishable from a real invitation because it is one: same
    /// From, same Reply-To, same server.
    /// </summary>
    [Fact]
    public async Task SendAsync_RefusesAVeFromAnotherTeam()
    {
        await using var dbContext = CreateContext();
        var (team, session) = await SeedSessionAsync(dbContext);
        var mine = await SeedVeAsync(dbContext, team, "W0AAA", "mine@example.org");

        var otherTeam = new Team { Name = "OTHER", ExamToolsTeamCode = "OTHER", CreatedUtc = Now };
        dbContext.Teams.Add(otherTeam);
        await dbContext.SaveChangesAsync();
        var theirs = await SeedVeAsync(dbContext, otherTeam, "W0BBB", "victim@example.org");

        var (service, email) = Create(dbContext);
        var result = await service.SendAsync(
            session.Id, [mine.Id, theirs.Id], "Subject", "Body", userId: 1, CancellationToken.None);

        Assert.Equal(1, result.Sent);
        Assert.Equal(1, result.NotOnTeam);
        Assert.Equal(["mine@example.org"], email.Sent.Select(m => m.ToAddress));
        Assert.DoesNotContain(email.Sent, m => m.ToAddress == "victim@example.org");
    }

    /// <summary>
    /// A VE with no team membership at all — the retired/orphaned rows the deployment accumulates.
    /// A scope written as "not a member of a DIFFERENT team" would let these through.
    /// </summary>
    [Fact]
    public async Task SendAsync_RefusesAVeWithNoTeamMembership()
    {
        await using var dbContext = CreateContext();
        var (_, session) = await SeedSessionAsync(dbContext);

        var orphan = new VolunteerExaminer { Name = "Orphan", CallSign = "W0CCC", Email = "orphan@example.org", CreatedUtc = Now };
        dbContext.VolunteerExaminers.Add(orphan);
        await dbContext.SaveChangesAsync();

        var (service, email) = Create(dbContext);
        var result = await service.SendAsync(
            session.Id, [orphan.Id], "Subject", "Body", userId: 1, CancellationToken.None);

        Assert.Equal(0, result.Sent);
        Assert.Equal(1, result.NotOnTeam);
        Assert.Empty(email.Sent);
    }

    /// <summary>
    /// The recipient scope must match what GetCandidatesAsync offers, which filters on IsActive. If
    /// the two disagree, either the screen offers people the send drops, or the send reaches people
    /// the screen never showed.
    /// </summary>
    [Fact]
    public async Task SendAsync_RefusesAnInactiveMemberOfTheRightTeam()
    {
        await using var dbContext = CreateContext();
        var (team, session) = await SeedSessionAsync(dbContext);
        var retired = await SeedVeAsync(dbContext, team, "W0DDD", "retired@example.org", active: false);

        var (service, email) = Create(dbContext);
        var result = await service.SendAsync(
            session.Id, [retired.Id], "Subject", "Body", userId: 1, CancellationToken.None);

        Assert.Equal(0, result.Sent);
        Assert.Equal(1, result.NotOnTeam);
        Assert.Empty(email.Sent);

        var candidates = await service.GetCandidatesAsync(session.Id, CancellationToken.None);
        Assert.DoesNotContain(candidates, c => c.VolunteerExaminer.Id == retired.Id);
    }

    /// <summary>An ordinary send must be unaffected — a scope that also blocks the legitimate path
    /// is not a fix.</summary>
    [Fact]
    public async Task SendAsync_StillSendsToTheTeamsOwnActiveVes()
    {
        await using var dbContext = CreateContext();
        var (team, session) = await SeedSessionAsync(dbContext);
        var a = await SeedVeAsync(dbContext, team, "W0AAA", "a@example.org");
        var b = await SeedVeAsync(dbContext, team, "W0BBB", "b@example.org");

        var (service, email) = Create(dbContext);
        var result = await service.SendAsync(
            session.Id, [a.Id, b.Id], "Subject", "Body", userId: 1, CancellationToken.None);

        Assert.Equal(2, result.Sent);
        Assert.Equal(0, result.NotOnTeam);
        Assert.Equal(2, email.Sent.Count);
    }
}
