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
/// Sending a hand-composed email to candidates chosen on a session (#144).
///
/// <para>Unlike every other method on <see cref="CandidateNotificationService"/>, the message here is
/// not a stored template — it is whatever the sender typed, starting from one. That is the whole
/// feature, and it moves two things this app has already got wrong once into a new code path: the
/// recipient list arrives from a form, and the body is rendered outside
/// <c>EmailTemplateRenderer.RenderAsync</c>. Both have their own test below.</para>
/// </summary>
public class CandidateComposedEmailTests
{
    private static readonly DateTime Now = new(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    /// <summary>
    /// Overrides <see cref="IEmailSender.SendManyAsync"/> rather than only <c>SendAsync</c>, because
    /// one of the assertions here is that the batch travels as a single call — the default interface
    /// implementation loops, which would make that assertion pass against a per-message send.
    /// </summary>
    private sealed class FakeBatchEmailSender : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];
        public List<EmailCredentials> CredentialsUsed { get; } = [];
        public int BatchCalls { get; private set; }

        /// <summary>Addresses that will fail, to exercise per-recipient isolation.</summary>
        public HashSet<string> FailFor { get; } = [];

        public Task SendAsync(EmailCredentials credentials, EmailMessage message, CancellationToken cancellationToken)
        {
            CredentialsUsed.Add(credentials);
            if (FailFor.Contains(message.ToAddress))
            {
                throw new InvalidOperationException($"Refused: {message.ToAddress}");
            }

            Sent.Add(message);
            return Task.CompletedTask;
        }

        public async Task<IReadOnlyList<EmailSendOutcome>> SendManyAsync(
            EmailCredentials credentials, IReadOnlyList<EmailMessage> messages, CancellationToken cancellationToken)
        {
            BatchCalls++;
            var outcomes = new List<EmailSendOutcome>(messages.Count);
            foreach (var message in messages)
            {
                try
                {
                    await SendAsync(credentials, message, cancellationToken);
                    outcomes.Add(EmailSendOutcome.Success);
                }
                catch (Exception ex)
                {
                    outcomes.Add(new EmailSendOutcome(false, ex));
                }
            }

            return outcomes;
        }
    }

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static CandidateNotificationService CreateService(AppDbContext dbContext, IEmailSender emailSender) => new(
        dbContext,
        new EmailTemplateRenderer(dbContext, NullLogger<EmailTemplateRenderer>.Instance),
        emailSender,
        new TeamIntegrationState(NullLogger<TeamIntegrationState>.Instance),
        new FixedTimeProvider(Now),
        Options.Create(new AppOptions { PublicBaseUrl = "https://test.example" }),
        NullLogger<CandidateNotificationService>.Instance);

    private sealed record Fixture(Team Team, Session Session, User User);

    private static async Task<Fixture> SeedAsync(
        AppDbContext dbContext, bool emailConfigured = true, bool emailSettings = true, bool emailEnabled = true)
    {
        var team = new Team
        {
            Name = "TESTTEAM",
            SmtpHost = emailConfigured ? "smtp.example.org" : null,
            SmtpUsername = emailConfigured ? "smtp-user" : null,
            SmtpPassword = emailConfigured ? "smtp-pass" : null,
            // Both, or neither mutes anything: the per-integration switches are only consulted while
            // IntegrationOverridesEnabled is true (#64). Setting EmailEnabled alone produces a team
            // that looks muted in the fixture and is not — which is how this test first passed
            // against a service that never checked.
            IntegrationOverridesEnabled = !emailEnabled,
            EmailEnabled = emailEnabled,
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
            VecId = vec.Id,
            EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            FeeCollectionEnabled = true,
            ExamFeeAmount = 15m,
            CreatedByUserId = user.Id,
            CreatedUtc = Now
        };
        dbContext.FeeConfigurations.Add(feeConfiguration);
        await dbContext.SaveChangesAsync();

        var session = new Session
        {
            ExamToolsSessionId = "session-1",
            Title = "August Session",
            ScheduledStartUtc = Now.AddDays(-2),
            DurationMinutes = 60,
            TeamId = team.Id,
            VecId = vec.Id,
            FeeConfigurationId = feeConfiguration.Id,
            Status = SessionStatus.Active,
            CreatedUtc = Now
        };
        dbContext.Sessions.Add(session);

        if (emailSettings)
        {
            dbContext.EmailSettings.Add(new EmailSettings
            {
                TeamId = team.Id,
                FromAddress = "noreply@example.org",
                FromDisplayName = "Test Team VEs",
                ReplyToAddress = "reply@example.org",
                PrivacyPolicyUrl = "https://example.org/privacy",
                AdminNotificationEmail = "admin@example.org",
                BccAddress = "watch@example.org"
            });
        }

        await dbContext.SaveChangesAsync();
        return new Fixture(team, session, user);
    }

    private static async Task<Candidate> AddCandidateAsync(
        AppDbContext dbContext, Session session, string name, string? email, string? callSign = null)
    {
        var candidate = new Candidate
        {
            SessionId = session.Id,
            ExamToolsApplicantId = Guid.NewGuid().ToString(),
            Name = name,
            FirstName = name.Split(' ')[0],
            Email = email,
            CallSign = callSign,
            DateRegisteredUtc = Now.AddDays(-10)
        };
        dbContext.Candidates.Add(candidate);
        await dbContext.SaveChangesAsync();
        return candidate;
    }

    private const string Subject = "Getting started on the air, {{CandidateFirstName}}";
    private const string Body = "<p>Hi {{CandidateFirstName}},</p><p>Our club meets Tuesdays. — {{TeamName}}</p>";

    [Fact]
    public async Task SendsToEveryChosenCandidate_RenderedForEachOne()
    {
        await using var dbContext = CreateContext();
        var fixture = await SeedAsync(dbContext);
        var ana = await AddCandidateAsync(dbContext, fixture.Session, "Ana Ruiz", "ana@example.com");
        var bo = await AddCandidateAsync(dbContext, fixture.Session, "Bo Chen", "bo@example.com");
        var sender = new FakeBatchEmailSender();

        var result = await CreateService(dbContext, sender).SendComposedAsync(
            fixture.Session.Id, [ana.Id, bo.Id], Subject, Body, "Getting started locally", fixture.User.Id, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(2, result.Sent);
        Assert.Equal(2, sender.Sent.Count);

        // The point of the whole feature: one draft, resolved per person.
        var toAna = sender.Sent.Single(m => m.ToAddress == "ana@example.com");
        Assert.Equal("Getting started on the air, Ana", toAna.Subject);
        Assert.Contains("Hi Ana,", toAna.HtmlBody);
        Assert.Contains("TESTTEAM", toAna.HtmlBody);
        Assert.Contains("Hi Bo,", sender.Sent.Single(m => m.ToAddress == "bo@example.com").HtmlBody);
    }

    [Fact]
    public async Task ACandidateFromAnotherSession_IsDroppedNotMailed()
    {
        // #238, in a new place. The ids arrive from a posted form, so "the screen only offered this
        // session's candidates" is a default, not a constraint. Unscoped, this sends attacker-authored
        // text from the team's own SMTP to any candidate row on the deployment — indistinguishable
        // from genuine mail because it is genuine.
        await using var dbContext = CreateContext();
        var fixture = await SeedAsync(dbContext);
        var mine = await AddCandidateAsync(dbContext, fixture.Session, "Ana Ruiz", "ana@example.com");

        var otherSession = new Session
        {
            ExamToolsSessionId = "session-2",
            Title = "Someone else's session",
            ScheduledStartUtc = Now.AddDays(-3),
            DurationMinutes = 60,
            TeamId = fixture.Session.TeamId,
            VecId = fixture.Session.VecId,
            FeeConfigurationId = fixture.Session.FeeConfigurationId,
            Status = SessionStatus.Active,
            CreatedUtc = Now
        };
        dbContext.Sessions.Add(otherSession);
        await dbContext.SaveChangesAsync();
        var theirs = await AddCandidateAsync(dbContext, otherSession, "Not Mine", "elsewhere@example.com");

        var sender = new FakeBatchEmailSender();
        var result = await CreateService(dbContext, sender).SendComposedAsync(
            fixture.Session.Id, [mine.Id, theirs.Id], Subject, Body, "Getting started locally", fixture.User.Id, CancellationToken.None);

        Assert.Equal(1, result.Sent);
        Assert.Equal(1, result.NotOnSession);
        // Asserted on what actually left the building, not only on the counter.
        Assert.DoesNotContain(sender.Sent, m => m.ToAddress == "elsewhere@example.com");
    }

    [Fact]
    public async Task ACandidateNameCarryingMarkup_IsEncodedInTheBody_AndStrippedOfLineBreaksInTheSubject()
    {
        // #260/#261, reached through the shared renderer rather than a second hand-rolled one. Names
        // come from ExamTools' public registration intake.
        await using var dbContext = CreateContext();
        var fixture = await SeedAsync(dbContext);
        var candidate = await AddCandidateAsync(dbContext, fixture.Session, "Ana", "ana@example.com");
        candidate.FirstName = "</p><a href=\"https://evil/\">Click here</a>\r\nBcc: someone";
        await dbContext.SaveChangesAsync();

        var sender = new FakeBatchEmailSender();
        await CreateService(dbContext, sender).SendComposedAsync(
            fixture.Session.Id, [candidate.Id], Subject, Body, "Getting started locally", fixture.User.Id, CancellationToken.None);

        var message = Assert.Single(sender.Sent);
        Assert.DoesNotContain("<a href=\"https://evil/\">", message.HtmlBody);
        Assert.Contains("&lt;a href=", message.HtmlBody);
        Assert.DoesNotContain('\r', message.Subject);
        Assert.DoesNotContain('\n', message.Subject);
    }

    [Theory]
    [InlineData("", "<p>body</p>")]
    [InlineData("   ", "<p>body</p>")]
    [InlineData("Subject", "")]
    [InlineData("Subject", "  ")]
    public async Task ABlankSubjectOrBody_SendsNothing(string subject, string body)
    {
        await using var dbContext = CreateContext();
        var fixture = await SeedAsync(dbContext);
        var candidate = await AddCandidateAsync(dbContext, fixture.Session, "Ana Ruiz", "ana@example.com");
        var sender = new FakeBatchEmailSender();

        var result = await CreateService(dbContext, sender).SendComposedAsync(
            fixture.Session.Id, [candidate.Id], subject, body, "Getting started locally", fixture.User.Id, CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task AMutedTeam_SendsNothing_AndSaysSo()
    {
        // TrySendAsync answers true for a muted team — the settle-without-doing rule that stops
        // scan-based jobs building a backlog. Right for a job, wrong for someone watching a button:
        // "sent 4" while nothing left would be the worst possible answer here.
        await using var dbContext = CreateContext();
        var fixture = await SeedAsync(dbContext, emailEnabled: false);
        var candidate = await AddCandidateAsync(dbContext, fixture.Session, "Ana Ruiz", "ana@example.com");
        var sender = new FakeBatchEmailSender();

        var result = await CreateService(dbContext, sender).SendComposedAsync(
            fixture.Session.Id, [candidate.Id], Subject, Body, "Getting started locally", fixture.User.Id, CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Equal(0, result.Sent);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task UnconfiguredSmtp_SendsNothing_AndSaysSo()
    {
        await using var dbContext = CreateContext();
        var fixture = await SeedAsync(dbContext, emailConfigured: false);
        var candidate = await AddCandidateAsync(dbContext, fixture.Session, "Ana Ruiz", "ana@example.com");
        var sender = new FakeBatchEmailSender();

        var result = await CreateService(dbContext, sender).SendComposedAsync(
            fixture.Session.Id, [candidate.Id], Subject, Body, "Getting started locally", fixture.User.Id, CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task NoEmailSettingsRow_SendsNothing_AndSaysSo()
    {
        await using var dbContext = CreateContext();
        var fixture = await SeedAsync(dbContext, emailSettings: false);
        var candidate = await AddCandidateAsync(dbContext, fixture.Session, "Ana Ruiz", "ana@example.com");
        var sender = new FakeBatchEmailSender();

        var result = await CreateService(dbContext, sender).SendComposedAsync(
            fixture.Session.Id, [candidate.Id], Subject, Body, "Getting started locally", fixture.User.Id, CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task ACandidateWithNoAddress_IsCountedRatherThanSilentlySkipped()
    {
        // "Sent 1 of 2" with no explanation is worse than a number someone can act on.
        await using var dbContext = CreateContext();
        var fixture = await SeedAsync(dbContext);
        var ana = await AddCandidateAsync(dbContext, fixture.Session, "Ana Ruiz", "ana@example.com");
        var noAddress = await AddCandidateAsync(dbContext, fixture.Session, "Bo Chen", null);
        var sender = new FakeBatchEmailSender();

        var result = await CreateService(dbContext, sender).SendComposedAsync(
            fixture.Session.Id, [ana.Id, noAddress.Id], Subject, Body, "Getting started locally", fixture.User.Id, CancellationToken.None);

        Assert.Equal(1, result.Sent);
        Assert.Equal(1, result.NoEmailAddress);
    }

    [Fact]
    public async Task OneRefusedAddress_DoesNotStopTheRest_AndTravelsAsOneBatch()
    {
        await using var dbContext = CreateContext();
        var fixture = await SeedAsync(dbContext);
        var ana = await AddCandidateAsync(dbContext, fixture.Session, "Ana Ruiz", "ana@example.com");
        var bo = await AddCandidateAsync(dbContext, fixture.Session, "Bo Chen", "bo@example.com");
        var sender = new FakeBatchEmailSender();
        sender.FailFor.Add("ana@example.com");

        var result = await CreateService(dbContext, sender).SendComposedAsync(
            fixture.Session.Id, [ana.Id, bo.Id], Subject, Body, "Getting started locally", fixture.User.Id, CancellationToken.None);

        Assert.Equal(1, result.Sent);
        Assert.Equal(1, result.Failed);
        Assert.Single(sender.Sent);
        // One SMTP handshake for the batch (#293), not one per recipient.
        Assert.Equal(1, sender.BatchCalls);
    }

    [Fact]
    public async Task SendsFromTheTeamsOwnAddresses_AndCarriesTheMonitoringBcc()
    {
        await using var dbContext = CreateContext();
        var fixture = await SeedAsync(dbContext);
        var candidate = await AddCandidateAsync(dbContext, fixture.Session, "Ana Ruiz", "ana@example.com");
        var sender = new FakeBatchEmailSender();

        await CreateService(dbContext, sender).SendComposedAsync(
            fixture.Session.Id, [candidate.Id], Subject, Body, "Getting started locally", fixture.User.Id, CancellationToken.None);

        var message = Assert.Single(sender.Sent);
        Assert.Equal("noreply@example.org", message.FromAddress);
        Assert.Equal("Test Team VEs", message.FromDisplayName);
        Assert.Equal("reply@example.org", message.ReplyToAddress);
        Assert.Equal("watch@example.org", message.BccAddress);
        Assert.Equal("smtp.example.org", Assert.Single(sender.CredentialsUsed.Distinct()).Host);
    }

    [Fact]
    public async Task TheLogoPlaceholder_BecomesTheTeamsInlineImage()
    {
        // The reason this send goes through EmailTemplateRenderer rather than a private Replace loop:
        // {{Logo}} is the one raw-HTML placeholder, and it needs the attachment alongside it.
        await using var dbContext = CreateContext();
        var fixture = await SeedAsync(dbContext);
        fixture.Team.LogoBytes = [1, 2, 3];
        fixture.Team.LogoContentType = "image/png";
        await dbContext.SaveChangesAsync();
        var candidate = await AddCandidateAsync(dbContext, fixture.Session, "Ana Ruiz", "ana@example.com");
        var sender = new FakeBatchEmailSender();

        await CreateService(dbContext, sender).SendComposedAsync(
            fixture.Session.Id, [candidate.Id], Subject, "<p>{{Logo}}</p><p>Hi {{CandidateFirstName}}</p>",
            "Getting started locally", fixture.User.Id, CancellationToken.None);

        var message = Assert.Single(sender.Sent);
        Assert.NotNull(message.InlineLogo);
        Assert.Contains("cid:", message.HtmlBody);
        Assert.DoesNotContain("{{Logo}}", message.HtmlBody);
    }

    [Fact]
    public async Task RecordsOneSendPerRecipient_AndOneAuditRowForTheBatch()
    {
        await using var dbContext = CreateContext();
        var fixture = await SeedAsync(dbContext);
        var ana = await AddCandidateAsync(dbContext, fixture.Session, "Ana Ruiz", "ana@example.com");
        var bo = await AddCandidateAsync(dbContext, fixture.Session, "Bo Chen", "bo@example.com");
        var sender = new FakeBatchEmailSender();

        await CreateService(dbContext, sender).SendComposedAsync(
            fixture.Session.Id, [ana.Id, bo.Id], Subject, Body, "Getting started locally", fixture.User.Id, CancellationToken.None);

        var sends = await dbContext.CandidateEmailSends.ToListAsync();
        Assert.Equal(2, sends.Count);
        Assert.All(sends, s => Assert.Equal("Getting started locally", s.TemplateLabel));
        Assert.All(sends, s => Assert.Equal(Now, s.SentUtc));
        Assert.All(sends, s => Assert.Equal(fixture.User.Id, s.SentByUserId));

        var audit = Assert.Single(await dbContext.AuditLogs.ToListAsync());
        Assert.Equal(fixture.User.Id, audit.UserId);
        Assert.Equal(nameof(Session), audit.EntityType);
        Assert.Equal(fixture.Session.Id, audit.EntityId);
    }

    [Fact]
    public async Task ARecipientWhoseSendFailed_IsNotRecordedAsHavingHadIt()
    {
        // The history answers "who has already had one". A failed send that logged a row would make
        // the second pass over a session skip exactly the person it exists to catch.
        await using var dbContext = CreateContext();
        var fixture = await SeedAsync(dbContext);
        var ana = await AddCandidateAsync(dbContext, fixture.Session, "Ana Ruiz", "ana@example.com");
        var bo = await AddCandidateAsync(dbContext, fixture.Session, "Bo Chen", "bo@example.com");
        var sender = new FakeBatchEmailSender();
        sender.FailFor.Add("ana@example.com");

        await CreateService(dbContext, sender).SendComposedAsync(
            fixture.Session.Id, [ana.Id, bo.Id], Subject, Body, "Getting started locally", fixture.User.Id, CancellationToken.None);

        var send = Assert.Single(await dbContext.CandidateEmailSends.ToListAsync());
        Assert.Equal(bo.Id, send.CandidateId);
    }
}
