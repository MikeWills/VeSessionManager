using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VeSessionManager.Core;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Email;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Notifications;
using Xunit;

namespace VeSessionManager.Core.Tests;

public class CandidateNotificationServiceTests
{
    private static readonly DateTime Now = new(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);
    private const string TestPublicBaseUrl = "https://test.example";

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private sealed class FakeEmailSender : IEmailSender
    {
        public List<EmailMessage> SentMessages { get; } = [];
        public List<EmailCredentials> CredentialsUsed { get; } = [];
        public Exception? ThrowOnNextSend { get; set; }

        public Task SendAsync(EmailCredentials credentials, EmailMessage message, CancellationToken cancellationToken)
        {
            CredentialsUsed.Add(credentials);
            if (ThrowOnNextSend is not null)
            {
                var ex = ThrowOnNextSend;
                ThrowOnNextSend = null;
                throw ex;
            }
            SentMessages.Add(message);
            return Task.CompletedTask;
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
        new FixedTimeProvider(Now),
        Options.Create(new AppOptions { PublicBaseUrl = TestPublicBaseUrl }),
        NullLogger<CandidateNotificationService>.Instance);

    /// <summary>Seeds a Team. emailConfigured=true (default) sets SmtpHost/Username so Team.IsEmailConfigured is true.</summary>
    private static async Task<Team> SeedTeamAsync(AppDbContext dbContext, bool emailConfigured = true)
    {
        var team = new Team
        {
            Name = "TESTTEAM",
            SmtpHost = emailConfigured ? "smtp.example.org" : null,
            SmtpUsername = emailConfigured ? "smtp-user" : null,
            SmtpPassword = emailConfigured ? "smtp-pass" : null,
            CreatedUtc = Now
        };
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        return team;
    }

    private static async Task SeedEmailSettingsAndTemplatesAsync(AppDbContext dbContext, Team team)
    {
        dbContext.EmailSettings.Add(new EmailSettings
        {
            TeamId = team.Id,
            FromAddress = "noreply@example.org",
            FromDisplayName = "VE Session Manager",
            ReplyToAddress = "reply@example.org",
            PrivacyPolicyUrl = "https://example.org/privacy",
            AdminNotificationEmail = "admin@example.org"
        });
        dbContext.EmailTemplates.Add(new EmailTemplate
        {
            TeamId = team.Id,
            Key = "RegistrationConfirmation",
            Subject = "Registered for {{SessionDate}}",
            Body = "Hi {{CandidateFirstName}} ({{CandidateName}}), Zoom: {{ZoomJoinUrl}}, Pay: {{PaymentLinkUrl}}, Privacy: {{PrivacyPolicyUrl}}"
        });
        dbContext.EmailTemplates.Add(new EmailTemplate
        {
            TeamId = team.Id,
            Key = "DayBeforeReminder",
            Subject = "Reminder for {{SessionDate}}",
            Body = "Hi {{CandidateFirstName}}, Zoom: {{ZoomJoinUrl}}, Outstanding: {{OutstandingPaymentLinkUrl}}"
        });
        await dbContext.SaveChangesAsync();
    }

    /// <summary>Seeds Vec/User/FeeConfiguration/Session, returning the Session for further per-test customization.</summary>
    private static async Task<Session> SeedSessionAsync(
        AppDbContext dbContext, Team team, DateTime scheduledStartUtc, bool feeCollectionEnabled = true,
        SessionStatus status = SessionStatus.Active, string? zoomJoinUrl = "https://zoom.us/j/123", bool supportsYouthProgram = false)
    {
        var vec = new Vec { Name = "ARRL", SupportsYouthProgram = supportsYouthProgram };
        var user = new User { Name = "System", Email = "system@localhost", Role = UserRole.SystemAdmin };
        var feeConfiguration = new FeeConfiguration
        {
            Vec = vec, EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            FeeCollectionEnabled = feeCollectionEnabled, ExamFeeAmount = feeCollectionEnabled ? 15m : null,
            CreatedByUser = user, CreatedUtc = Now
        };
        var session = new Session
        {
            ExamToolsSessionId = "session-1", Title = "July Session", ScheduledStartUtc = scheduledStartUtc,
            DurationMinutes = 60, Vec = vec, TeamId = team.Id, FeeConfiguration = feeConfiguration, Status = status,
            ZoomJoinUrl = zoomJoinUrl, CreatedUtc = Now
        };
        dbContext.Sessions.Add(session);
        await dbContext.SaveChangesAsync();
        return session;
    }

    private static Candidate NewCandidate(Session session, string applicantId = "applicant-1", string firstName = "Roana", string lastName = "Glory") => new()
    {
        ExamToolsApplicantId = applicantId,
        SessionId = session.Id,
        Name = $"{firstName} {lastName}",
        FirstName = firstName,
        Email = $"{firstName.ToLower()}@example.com",
        DateRegisteredUtc = Now
    };

    // ---- Registration confirmation ----

    [Fact]
    public async Task RegistrationConfirmation_SendsWithCorrectPlaceholders_AndMarksSent()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        var session = await SeedSessionAsync(dbContext, team, Now.AddDays(4));
        var candidate = NewCandidate(session);
        dbContext.Candidates.Add(candidate);
        await dbContext.SaveChangesAsync();
        dbContext.Payments.Add(new Payment
        {
            CandidateId = candidate.Id, Reason = PaymentReason.InitialExam, Amount = 15m,
            Status = PaymentStatus.Unpaid, PaymentLinkUrl = "https://square.link/u/abc", CreatedUtc = Now
        });
        await dbContext.SaveChangesAsync();

        var sender = new FakeEmailSender();
        var result = await CreateService(dbContext, sender).SendRegistrationConfirmationsAsync(team, CancellationToken.None);

        Assert.Equal(1, result.Sent);
        Assert.Equal(0, result.Failed);
        var message = Assert.Single(sender.SentMessages);
        Assert.Equal("roana@example.com", message.ToAddress);
        Assert.Equal("noreply@example.org", message.FromAddress);
        Assert.Equal("reply@example.org", message.ReplyToAddress);
        Assert.Contains("Hi Roana (Roana Glory)", message.HtmlBody);
        Assert.Contains("Zoom: https://zoom.us/j/123", message.HtmlBody);
        Assert.Contains("Pay: https://square.link/u/abc", message.HtmlBody);
        Assert.Contains("Privacy: https://example.org/privacy", message.HtmlBody);
        Assert.Equal(Now, dbContext.Candidates.Single().RegistrationConfirmationSentUtc);
        Assert.Equal(team.Id, Assert.Single(sender.CredentialsUsed).TeamId);
    }

    [Fact]
    public async Task RegistrationConfirmation_FeeCollectionDisabled_PaymentLinkUrlIsBlank()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        var session = await SeedSessionAsync(dbContext, team, Now.AddDays(4), feeCollectionEnabled: false);
        dbContext.Candidates.Add(NewCandidate(session));
        await dbContext.SaveChangesAsync();

        var sender = new FakeEmailSender();
        await CreateService(dbContext, sender).SendRegistrationConfirmationsAsync(team, CancellationToken.None);

        var message = Assert.Single(sender.SentMessages);
        Assert.Contains("Pay: ,", message.HtmlBody); // blank, per "read sensibly either way"
    }

    [Fact]
    public async Task RegistrationConfirmation_YouthProgramVec_IncludesYouthPaymentLinkUrl()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        dbContext.EmailSettings.Add(new EmailSettings
        {
            TeamId = team.Id, FromAddress = "noreply@example.org", FromDisplayName = "VE Session Manager",
            ReplyToAddress = "reply@example.org", PrivacyPolicyUrl = "https://example.org/privacy", AdminNotificationEmail = "admin@example.org"
        });
        dbContext.EmailTemplates.Add(new EmailTemplate
        {
            TeamId = team.Id, Key = "RegistrationConfirmation", Subject = "Registered for {{SessionDate}}",
            Body = "Pay: {{PaymentLinkUrl}}, Youth: {{YouthPaymentLinkUrl}}"
        });
        await dbContext.SaveChangesAsync();
        var session = await SeedSessionAsync(dbContext, team, Now.AddDays(4), supportsYouthProgram: true);
        var candidate = NewCandidate(session);
        dbContext.Candidates.Add(candidate);
        await dbContext.SaveChangesAsync();
        var payment = new Payment
        {
            CandidateId = candidate.Id, Reason = PaymentReason.InitialExam, Amount = 15m,
            Status = PaymentStatus.Unpaid, PaymentLinkUrl = "https://square.link/u/abc",
            YouthConfirmationToken = Guid.NewGuid(), CreatedUtc = Now
        };
        dbContext.Payments.Add(payment);
        await dbContext.SaveChangesAsync();

        var sender = new FakeEmailSender();
        await CreateService(dbContext, sender).SendRegistrationConfirmationsAsync(team, CancellationToken.None);

        var message = Assert.Single(sender.SentMessages);
        Assert.Contains($"Youth: {TestPublicBaseUrl}/youth-confirm/{payment.YouthConfirmationToken}", message.HtmlBody);
    }

    [Fact]
    public async Task RegistrationConfirmation_NonYouthProgramVec_YouthPaymentLinkUrlIsBlank()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        dbContext.EmailSettings.Add(new EmailSettings
        {
            TeamId = team.Id, FromAddress = "noreply@example.org", FromDisplayName = "VE Session Manager",
            ReplyToAddress = "reply@example.org", PrivacyPolicyUrl = "https://example.org/privacy", AdminNotificationEmail = "admin@example.org"
        });
        dbContext.EmailTemplates.Add(new EmailTemplate
        {
            TeamId = team.Id, Key = "RegistrationConfirmation", Subject = "Registered for {{SessionDate}}",
            Body = "Youth: {{YouthPaymentLinkUrl}}."
        });
        await dbContext.SaveChangesAsync();
        var session = await SeedSessionAsync(dbContext, team, Now.AddDays(4), supportsYouthProgram: false);
        dbContext.Candidates.Add(NewCandidate(session));
        await dbContext.SaveChangesAsync();

        var sender = new FakeEmailSender();
        await CreateService(dbContext, sender).SendRegistrationConfirmationsAsync(team, CancellationToken.None);

        var message = Assert.Single(sender.SentMessages);
        Assert.Contains("Youth: .", message.HtmlBody);
    }

    [Fact]
    public async Task RegistrationConfirmation_AlreadySent_IsNotResent()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        var session = await SeedSessionAsync(dbContext, team, Now.AddDays(4));
        var candidate = NewCandidate(session);
        candidate.RegistrationConfirmationSentUtc = Now.AddDays(-1);
        dbContext.Candidates.Add(candidate);
        await dbContext.SaveChangesAsync();

        var sender = new FakeEmailSender();
        var result = await CreateService(dbContext, sender).SendRegistrationConfirmationsAsync(team, CancellationToken.None);

        Assert.Equal(0, result.Sent);
        Assert.Empty(sender.SentMessages);
    }

    [Fact]
    public async Task RegistrationConfirmation_CancelledSession_IsExcluded()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        var session = await SeedSessionAsync(dbContext, team, Now.AddDays(4), status: SessionStatus.Cancelled);
        dbContext.Candidates.Add(NewCandidate(session));
        await dbContext.SaveChangesAsync();

        var sender = new FakeEmailSender();
        var result = await CreateService(dbContext, sender).SendRegistrationConfirmationsAsync(team, CancellationToken.None);

        Assert.Equal(0, result.Sent);
        Assert.Empty(sender.SentMessages);
    }

    [Fact]
    public async Task RegistrationConfirmation_NoEmailSettingsRow_SkipsGracefully()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        // Templates exist, but no EmailSettings row seeded.
        dbContext.EmailTemplates.Add(new EmailTemplate { TeamId = team.Id, Key = "RegistrationConfirmation", Subject = "s", Body = "b" });
        await dbContext.SaveChangesAsync();
        var session = await SeedSessionAsync(dbContext, team, Now.AddDays(4));
        dbContext.Candidates.Add(NewCandidate(session));
        await dbContext.SaveChangesAsync();

        var sender = new FakeEmailSender();
        var result = await CreateService(dbContext, sender).SendRegistrationConfirmationsAsync(team, CancellationToken.None);

        Assert.Equal(0, result.Sent);
        Assert.Empty(sender.SentMessages);
    }

    [Fact]
    public async Task RegistrationConfirmation_OneSendFailing_DoesNotBlockOthers_AndIsRetryable()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        var session = await SeedSessionAsync(dbContext, team, Now.AddDays(4));
        dbContext.Candidates.Add(NewCandidate(session, "applicant-1", "Roana", "Glory"));
        dbContext.Candidates.Add(NewCandidate(session, "applicant-2", "Tomasina", "Susanna"));
        await dbContext.SaveChangesAsync();

        var sender = new FakeEmailSender { ThrowOnNextSend = new InvalidOperationException("SMTP down") };
        var result = await CreateService(dbContext, sender).SendRegistrationConfirmationsAsync(team, CancellationToken.None);

        Assert.Equal(1, result.Sent);
        Assert.Equal(1, result.Failed);
        var candidates = dbContext.Candidates.ToList();
        Assert.Single(candidates, c => c.RegistrationConfirmationSentUtc == Now);
        Assert.Single(candidates, c => c.RegistrationConfirmationSentUtc == null); // retryable next run
    }

    // ---- Day-before reminder ----

    [Fact]
    public async Task DayBeforeReminder_SessionTomorrow_IsIncluded()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        var session = await SeedSessionAsync(dbContext, team, Now.Date.AddDays(1).AddHours(17)); // tomorrow, 5pm UTC
        dbContext.Candidates.Add(NewCandidate(session));
        await dbContext.SaveChangesAsync();

        var sender = new FakeEmailSender();
        var result = await CreateService(dbContext, sender).SendDayBeforeRemindersAsync(team, CancellationToken.None);

        Assert.Equal(1, result.Sent);
        Assert.Equal(Now, dbContext.Candidates.Single().DayBeforeReminderSentUtc);
    }

    [Fact]
    public async Task DayBeforeReminder_SessionToday_IsExcluded()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        var session = await SeedSessionAsync(dbContext, team, Now.Date.AddHours(17)); // today
        dbContext.Candidates.Add(NewCandidate(session));
        await dbContext.SaveChangesAsync();

        var sender = new FakeEmailSender();
        var result = await CreateService(dbContext, sender).SendDayBeforeRemindersAsync(team, CancellationToken.None);

        Assert.Equal(0, result.Sent);
        Assert.Empty(sender.SentMessages);
    }

    [Fact]
    public async Task DayBeforeReminder_SessionDayAfterTomorrow_IsExcluded()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        var session = await SeedSessionAsync(dbContext, team, Now.Date.AddDays(2).AddHours(17));
        dbContext.Candidates.Add(NewCandidate(session));
        await dbContext.SaveChangesAsync();

        var sender = new FakeEmailSender();
        var result = await CreateService(dbContext, sender).SendDayBeforeRemindersAsync(team, CancellationToken.None);

        Assert.Equal(0, result.Sent);
        Assert.Empty(sender.SentMessages);
    }

    [Fact]
    public async Task DayBeforeReminder_CancelledSession_IsExcludedEvenIfTomorrow()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        var session = await SeedSessionAsync(dbContext, team, Now.Date.AddDays(1).AddHours(17), status: SessionStatus.Cancelled);
        dbContext.Candidates.Add(NewCandidate(session));
        await dbContext.SaveChangesAsync();

        var sender = new FakeEmailSender();
        var result = await CreateService(dbContext, sender).SendDayBeforeRemindersAsync(team, CancellationToken.None);

        Assert.Equal(0, result.Sent);
    }

    [Fact]
    public async Task DayBeforeReminder_AlreadySent_IsNotResent()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        var session = await SeedSessionAsync(dbContext, team, Now.Date.AddDays(1).AddHours(17));
        var candidate = NewCandidate(session);
        candidate.DayBeforeReminderSentUtc = Now.AddHours(-1);
        dbContext.Candidates.Add(candidate);
        await dbContext.SaveChangesAsync();

        var sender = new FakeEmailSender();
        var result = await CreateService(dbContext, sender).SendDayBeforeRemindersAsync(team, CancellationToken.None);

        Assert.Equal(0, result.Sent);
        Assert.Empty(sender.SentMessages);
    }

    [Fact]
    public async Task DayBeforeReminder_OutstandingUnpaidPayment_IncludesItsLink()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        var session = await SeedSessionAsync(dbContext, team, Now.Date.AddDays(1).AddHours(17));
        var candidate = NewCandidate(session);
        dbContext.Candidates.Add(candidate);
        await dbContext.SaveChangesAsync();
        dbContext.Payments.Add(new Payment
        {
            CandidateId = candidate.Id, Reason = PaymentReason.InitialExam, Amount = 15m,
            Status = PaymentStatus.Unpaid, PaymentLinkUrl = "https://square.link/u/xyz", CreatedUtc = Now
        });
        await dbContext.SaveChangesAsync();

        var sender = new FakeEmailSender();
        await CreateService(dbContext, sender).SendDayBeforeRemindersAsync(team, CancellationToken.None);

        var message = Assert.Single(sender.SentMessages);
        Assert.Contains("Outstanding: https://square.link/u/xyz", message.HtmlBody);
    }

    [Fact]
    public async Task DayBeforeReminder_NoOutstandingPayment_OutstandingPlaceholderIsBlank()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        var session = await SeedSessionAsync(dbContext, team, Now.Date.AddDays(1).AddHours(17));
        dbContext.Candidates.Add(NewCandidate(session));
        await dbContext.SaveChangesAsync();

        var sender = new FakeEmailSender();
        await CreateService(dbContext, sender).SendDayBeforeRemindersAsync(team, CancellationToken.None);

        var message = Assert.Single(sender.SentMessages);
        Assert.EndsWith("Outstanding: ", message.HtmlBody);
    }

    [Fact]
    public async Task MissingTemplate_CountsAsFailed_DoesNotMarkSent_IsRetryable()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        dbContext.EmailSettings.Add(new EmailSettings
        {
            TeamId = team.Id,
            FromAddress = "noreply@example.org", ReplyToAddress = "reply@example.org", PrivacyPolicyUrl = "https://example.org/privacy", AdminNotificationEmail = "admin@example.org"
        });
        // No RegistrationConfirmation template seeded at all.
        await dbContext.SaveChangesAsync();
        var session = await SeedSessionAsync(dbContext, team, Now.AddDays(4));
        dbContext.Candidates.Add(NewCandidate(session));
        await dbContext.SaveChangesAsync();

        var sender = new FakeEmailSender();
        var result = await CreateService(dbContext, sender).SendRegistrationConfirmationsAsync(team, CancellationToken.None);

        Assert.Equal(0, result.Sent);
        Assert.Equal(1, result.Failed);
        Assert.Null(dbContext.Candidates.Single().RegistrationConfirmationSentUtc);
        Assert.Empty(sender.SentMessages);
    }

    // ---- SMTP not configured (optional integration, same pattern as Square) ----

    [Fact]
    public async Task SmtpNotConfigured_RegistrationConfirmation_SkipsQuietly_NoFailureCounted()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, emailConfigured: false);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        var session = await SeedSessionAsync(dbContext, team, Now.AddDays(4));
        dbContext.Candidates.Add(NewCandidate(session));
        await dbContext.SaveChangesAsync();

        var sender = new FakeEmailSender();
        var result = await CreateService(dbContext, sender).SendRegistrationConfirmationsAsync(team, CancellationToken.None);

        Assert.Equal(0, result.Sent);
        Assert.Equal(0, result.Failed); // not attempted, so not a "failure"
        Assert.Empty(sender.SentMessages);
        Assert.Null(dbContext.Candidates.Single().RegistrationConfirmationSentUtc);

        // Once SMTP becomes configured, the very next poll must send the backlog automatically.
        team.SmtpHost = "smtp.example.org";
        team.SmtpUsername = "smtp-user";
        team.SmtpPassword = "smtp-pass";
        await dbContext.SaveChangesAsync();
        var retryResult = await CreateService(dbContext, sender).SendRegistrationConfirmationsAsync(team, CancellationToken.None);

        Assert.Equal(1, retryResult.Sent);
        Assert.Single(sender.SentMessages);
    }

    [Fact]
    public async Task SmtpNotConfigured_DayBeforeReminder_SkipsQuietly_NoFailureCounted()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, emailConfigured: false);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        var session = await SeedSessionAsync(dbContext, team, Now.Date.AddDays(1).AddHours(17));
        dbContext.Candidates.Add(NewCandidate(session));
        await dbContext.SaveChangesAsync();

        var sender = new FakeEmailSender();
        var result = await CreateService(dbContext, sender).SendDayBeforeRemindersAsync(team, CancellationToken.None);

        Assert.Equal(0, result.Sent);
        Assert.Equal(0, result.Failed);
        Assert.Empty(sender.SentMessages);
        Assert.Null(dbContext.Candidates.Single().DayBeforeReminderSentUtc);
    }

    // ---- Multi-team ----

    [Fact]
    public async Task TwoTeams_EachSendsWithItsOwnTemplateContentAndCredentials()
    {
        await using var dbContext = CreateContext();
        var teamA = await SeedTeamAsync(dbContext);
        var teamB = await SeedTeamAsync(dbContext);
        dbContext.EmailSettings.Add(new EmailSettings { TeamId = teamA.Id, FromAddress = "a@example.org", ReplyToAddress = "a@example.org", PrivacyPolicyUrl = "https://a.example.org/privacy", AdminNotificationEmail = "admin@a.example.org" });
        dbContext.EmailSettings.Add(new EmailSettings { TeamId = teamB.Id, FromAddress = "b@example.org", ReplyToAddress = "b@example.org", PrivacyPolicyUrl = "https://b.example.org/privacy", AdminNotificationEmail = "admin@b.example.org" });
        dbContext.EmailTemplates.Add(new EmailTemplate { TeamId = teamA.Id, Key = "RegistrationConfirmation", Subject = "A subject", Body = "Team A body" });
        dbContext.EmailTemplates.Add(new EmailTemplate { TeamId = teamB.Id, Key = "RegistrationConfirmation", Subject = "B subject", Body = "Team B body" });
        await dbContext.SaveChangesAsync();
        var sessionA = await SeedSessionAsync(dbContext, teamA, Now.AddDays(4));
        var sessionB = await SeedSessionAsync(dbContext, teamB, Now.AddDays(4));
        dbContext.Candidates.Add(NewCandidate(sessionA, "applicant-a"));
        dbContext.Candidates.Add(NewCandidate(sessionB, "applicant-b"));
        await dbContext.SaveChangesAsync();

        var sender = new FakeEmailSender();
        await CreateService(dbContext, sender).SendRegistrationConfirmationsAsync(teamA, CancellationToken.None);
        await CreateService(dbContext, sender).SendRegistrationConfirmationsAsync(teamB, CancellationToken.None);

        Assert.Equal(2, sender.SentMessages.Count);
        Assert.Contains(sender.SentMessages, m => m.FromAddress == "a@example.org" && m.HtmlBody == "Team A body");
        Assert.Contains(sender.SentMessages, m => m.FromAddress == "b@example.org" && m.HtmlBody == "Team B body");
    }

    // ---- Youth Program instructions ----

    [Fact]
    public async Task YouthProgramInstructions_VecSupportsIt_SendsAndMarksSent()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, team, Now.AddDays(4));
        session.Vec.SupportsYouthProgram = true;
        dbContext.EmailSettings.Add(new EmailSettings
        {
            TeamId = team.Id, FromAddress = "noreply@example.org", ReplyToAddress = "reply@example.org",
            PrivacyPolicyUrl = "https://example.org/privacy", AdminNotificationEmail = "admin@example.org"
        });
        dbContext.EmailTemplates.Add(new EmailTemplate
        {
            TeamId = team.Id, Key = "ArrlYouthProgramInstructions", Subject = "Youth Program",
            Body = "Hi {{CandidateName}} ({{CallSign}})"
        });
        var candidate = NewCandidate(session);
        candidate.CallSign = "KE0ABC";
        dbContext.Candidates.Add(candidate);
        await dbContext.SaveChangesAsync();

        var sender = new FakeEmailSender();
        var result = await CreateService(dbContext, sender).SendYouthProgramInstructionsAsync(candidate.Id, CancellationToken.None);

        Assert.Equal(CandidateEmailSendResult.Sent, result);
        var message = Assert.Single(sender.SentMessages);
        Assert.Contains("KE0ABC", message.HtmlBody);
        // Display-only for the session detail page's "Email history" modal — this action has no
        // send cap, so unlike RegistrationConfirmationSentUtc this always holds the latest send.
        Assert.Equal(Now, dbContext.Candidates.Single().YouthProgramInstructionsSentUtc);
    }

    [Fact]
    public async Task YouthProgramInstructions_VecDoesNotSupportIt_NotSent()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, team, Now.AddDays(4));
        var candidate = NewCandidate(session);
        dbContext.Candidates.Add(candidate);
        await dbContext.SaveChangesAsync();

        var sender = new FakeEmailSender();
        var result = await CreateService(dbContext, sender).SendYouthProgramInstructionsAsync(candidate.Id, CancellationToken.None);

        Assert.Equal(CandidateEmailSendResult.VecDoesNotSupportYouthProgram, result);
        Assert.Empty(sender.SentMessages);
        Assert.Null(dbContext.Candidates.Single().YouthProgramInstructionsSentUtc);
    }
}
