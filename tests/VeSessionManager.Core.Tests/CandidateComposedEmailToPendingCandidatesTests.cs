using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Email;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Integrations;
using VeSessionManager.Core.Notifications;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// <see cref="CandidateNotificationService.SendComposedToPendingCandidatesAsync"/> (2026-08-26) — the
/// bulk-email screen off Applicant Status. Everything about rendering, batching and failure isolation
/// is shared with <see cref="CandidateComposedEmailTests"/>'s <c>SendComposedAsync</c> coverage via
/// the same private helpers this method calls; these tests are only about what is actually new here:
/// team scope instead of session scope, and the <c>AwaitingFccGrant</c> population filter.
/// </summary>
public class CandidateComposedEmailToPendingCandidatesTests
{
    private static readonly DateTime Now = new(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private sealed class FakeBatchEmailSender : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];

        public Task SendAsync(EmailCredentials credentials, EmailMessage message, CancellationToken cancellationToken)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }

        public async Task<IReadOnlyList<EmailSendOutcome>> SendManyAsync(
            EmailCredentials credentials, IReadOnlyList<EmailMessage> messages, CancellationToken cancellationToken)
        {
            var outcomes = new List<EmailSendOutcome>(messages.Count);
            foreach (var message in messages)
            {
                await SendAsync(credentials, message, cancellationToken);
                outcomes.Add(EmailSendOutcome.Success);
            }

            return outcomes;
        }
    }

    private static AppDbContext CreateContext() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static CandidateNotificationService CreateService(AppDbContext dbContext, IEmailSender emailSender) => new(
        dbContext,
        new EmailTemplateRenderer(dbContext, NullLogger<EmailTemplateRenderer>.Instance),
        emailSender,
        new TeamIntegrationState(NullLogger<TeamIntegrationState>.Instance),
        new FixedTimeProvider(Now),
        Options.Create(new AppOptions { PublicBaseUrl = "https://test.example" }),
        NullLogger<CandidateNotificationService>.Instance);

    private sealed record Fixture(Team Team, Session Session, User User);

    private static async Task<Fixture> SeedAsync(AppDbContext dbContext, bool emailConfigured = true)
    {
        var team = new Team
        {
            Name = "TESTTEAM",
            SmtpHost = emailConfigured ? "smtp.example.org" : null,
            SmtpUsername = emailConfigured ? "smtp-user" : null,
            SmtpPassword = emailConfigured ? "smtp-pass" : null,
            CreatedUtc = Now
        };
        var user = new User { Name = "Sender", Email = "sender@example.org", Role = UserRole.SystemAdmin };
        var vec = new Vec { Name = "ARRL" };
        dbContext.Teams.Add(team);
        dbContext.Users.Add(user);
        dbContext.Vecs.Add(vec);
        await dbContext.SaveChangesAsync();

        var feeConfiguration = new FeeConfiguration
        {
            VecId = vec.Id, EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            FeeCollectionEnabled = true, ExamFeeAmount = 15m, CreatedByUserId = user.Id, CreatedUtc = Now
        };
        dbContext.FeeConfigurations.Add(feeConfiguration);
        await dbContext.SaveChangesAsync();

        var session = new Session
        {
            ExamToolsSessionId = "session-1", Title = "August Session", ScheduledStartUtc = Now.AddDays(-2),
            DurationMinutes = 60, TeamId = team.Id, VecId = vec.Id, FeeConfigurationId = feeConfiguration.Id,
            Status = SessionStatus.Active, CreatedUtc = Now
        };
        dbContext.Sessions.Add(session);

        dbContext.EmailSettings.Add(new EmailSettings
        {
            TeamId = team.Id, FromAddress = "noreply@example.org", ReplyToAddress = "reply@example.org",
            PrivacyPolicyUrl = "https://example.org/privacy", AdminNotificationEmail = "admin@example.org"
        });

        await dbContext.SaveChangesAsync();
        return new Fixture(team, session, user);
    }

    private static async Task<Candidate> AddPendingCandidateAsync(AppDbContext dbContext, Session session, string name, string? email)
    {
        var candidate = new Candidate
        {
            SessionId = session.Id, ExamToolsApplicantId = Guid.NewGuid().ToString(), Name = name,
            FirstName = name.Split(' ')[0], Email = email, DateRegisteredUtc = Now.AddDays(-10),
            Tested = true, ApplicationStatus = CandidateApplicationStatus.Received
        };
        dbContext.Candidates.Add(candidate);
        await dbContext.SaveChangesAsync();
        return candidate;
    }

    private const string Subject = "An update on your application, {{CandidateFirstName}}";
    private const string Body = "<p>Hi {{CandidateFirstName}},</p><p>The FCC is experiencing a known delay. — {{TeamName}}</p>";

    [Fact]
    public async Task SendsAcrossEverySessionOnTheTeam_NotJustOne()
    {
        await using var dbContext = CreateContext();
        var fixture = await SeedAsync(dbContext);
        var otherSession = new Session
        {
            ExamToolsSessionId = "session-2", Title = "A different session", ScheduledStartUtc = Now.AddDays(-30),
            DurationMinutes = 60, TeamId = fixture.Team.Id, VecId = fixture.Session.VecId,
            FeeConfigurationId = fixture.Session.FeeConfigurationId, Status = SessionStatus.Active, CreatedUtc = Now
        };
        dbContext.Sessions.Add(otherSession);
        await dbContext.SaveChangesAsync();

        var ana = await AddPendingCandidateAsync(dbContext, fixture.Session, "Ana Ruiz", "ana@example.com");
        var bo = await AddPendingCandidateAsync(dbContext, otherSession, "Bo Chen", "bo@example.com");
        var sender = new FakeBatchEmailSender();

        var result = await CreateService(dbContext, sender).SendComposedToPendingCandidatesAsync(
            fixture.Team.Id, [ana.Id, bo.Id], Subject, Body, "FCC delay update", fixture.User.Id, CancellationToken.None);

        Assert.Equal(2, result.Sent);
        Assert.Equal(2, sender.Sent.Count);
    }

    [Fact]
    public async Task ACandidateFromAnotherTeam_IsDroppedNotMailed()
    {
        await using var dbContext = CreateContext();
        var fixture = await SeedAsync(dbContext);
        var mine = await AddPendingCandidateAsync(dbContext, fixture.Session, "Ana Ruiz", "ana@example.com");

        var otherTeam = new Team { Name = "OTHERTEAM", SmtpHost = "smtp2.example.org", SmtpUsername = "u2", SmtpPassword = "p2", CreatedUtc = Now };
        dbContext.Teams.Add(otherTeam);
        await dbContext.SaveChangesAsync();
        var otherSession = new Session
        {
            ExamToolsSessionId = "session-2", Title = "Someone else's team", ScheduledStartUtc = Now.AddDays(-2),
            DurationMinutes = 60, TeamId = otherTeam.Id, VecId = fixture.Session.VecId,
            FeeConfigurationId = fixture.Session.FeeConfigurationId, Status = SessionStatus.Active, CreatedUtc = Now
        };
        dbContext.Sessions.Add(otherSession);
        await dbContext.SaveChangesAsync();
        var theirs = await AddPendingCandidateAsync(dbContext, otherSession, "Not Mine", "elsewhere@example.com");

        var sender = new FakeBatchEmailSender();
        var result = await CreateService(dbContext, sender).SendComposedToPendingCandidatesAsync(
            fixture.Team.Id, [mine.Id, theirs.Id], Subject, Body, "FCC delay update", fixture.User.Id, CancellationToken.None);

        Assert.Equal(1, result.Sent);
        Assert.Equal(1, result.NotOnSession);
        Assert.DoesNotContain(sender.Sent, m => m.ToAddress == "elsewhere@example.com");
    }

    /// <summary>
    /// The population filter, not just the team filter: a candidate already Granted has left the
    /// worklist this screen is built around, even if their id somehow still arrives in the posted
    /// form (they were checked, then granted, before the sender pressed Send).
    /// </summary>
    [Fact]
    public async Task ACandidateAlreadyGranted_IsDroppedNotMailed()
    {
        await using var dbContext = CreateContext();
        var fixture = await SeedAsync(dbContext);
        var pending = await AddPendingCandidateAsync(dbContext, fixture.Session, "Ana Ruiz", "ana@example.com");
        var granted = await AddPendingCandidateAsync(dbContext, fixture.Session, "Bo Chen", "bo@example.com");
        granted.ApplicationStatus = CandidateApplicationStatus.Granted;
        await dbContext.SaveChangesAsync();

        var sender = new FakeBatchEmailSender();
        var result = await CreateService(dbContext, sender).SendComposedToPendingCandidatesAsync(
            fixture.Team.Id, [pending.Id, granted.Id], Subject, Body, "FCC delay update", fixture.User.Id, CancellationToken.None);

        Assert.Equal(1, result.Sent);
        Assert.Equal(1, result.NotOnSession);
        Assert.DoesNotContain(sender.Sent, m => m.ToAddress == "bo@example.com");
    }

    [Fact]
    public async Task UnconfiguredTeam_SendsNothing_AndSaysSo()
    {
        await using var dbContext = CreateContext();
        var fixture = await SeedAsync(dbContext, emailConfigured: false);
        var candidate = await AddPendingCandidateAsync(dbContext, fixture.Session, "Ana Ruiz", "ana@example.com");
        var sender = new FakeBatchEmailSender();

        var result = await CreateService(dbContext, sender).SendComposedToPendingCandidatesAsync(
            fixture.Team.Id, [candidate.Id], Subject, Body, "FCC delay update", fixture.User.Id, CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task RecordsTheAuditAgainstTheTeam_NotASession()
    {
        await using var dbContext = CreateContext();
        var fixture = await SeedAsync(dbContext);
        var candidate = await AddPendingCandidateAsync(dbContext, fixture.Session, "Ana Ruiz", "ana@example.com");
        var sender = new FakeBatchEmailSender();

        await CreateService(dbContext, sender).SendComposedToPendingCandidatesAsync(
            fixture.Team.Id, [candidate.Id], Subject, Body, "FCC delay update", fixture.User.Id, CancellationToken.None);

        var audit = Assert.Single(await dbContext.AuditLogs.ToListAsync());
        Assert.Equal(nameof(Team), audit.EntityType);
        Assert.Equal(fixture.Team.Id, audit.EntityId);
    }
}
