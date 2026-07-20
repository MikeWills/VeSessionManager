using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Email;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Notifications;
using Xunit;

namespace VeSessionManager.Core.Tests;

public class CandidateNotificationServiceTests
{
    private static readonly DateTime Now = new(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private sealed class FakeEmailSender : IEmailSender
    {
        public List<EmailMessage> SentMessages { get; } = [];
        public Exception? ThrowOnNextSend { get; set; }
        public bool IsConfigured { get; set; } = true;

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
        {
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
        NullLogger<CandidateNotificationService>.Instance);

    private static async Task SeedEmailSettingsAndTemplatesAsync(AppDbContext dbContext)
    {
        dbContext.EmailSettings.Add(new EmailSettings
        {
            FromAddress = "noreply@example.org",
            FromDisplayName = "VE Session Manager",
            ReplyToAddress = "reply@example.org",
            PrivacyPolicyUrl = "https://example.org/privacy",
            AdminNotificationEmail = "admin@example.org"
        });
        dbContext.EmailTemplates.Add(new EmailTemplate
        {
            Key = "RegistrationConfirmation",
            Subject = "Registered for {{SessionDate}}",
            Body = "Hi {{CandidateFirstName}} ({{CandidateName}}), Zoom: {{ZoomJoinUrl}}, Pay: {{PaymentLinkUrl}}, Privacy: {{PrivacyPolicyUrl}}"
        });
        dbContext.EmailTemplates.Add(new EmailTemplate
        {
            Key = "DayBeforeReminder",
            Subject = "Reminder for {{SessionDate}}",
            Body = "Hi {{CandidateFirstName}}, Zoom: {{ZoomJoinUrl}}, Outstanding: {{OutstandingPaymentLinkUrl}}"
        });
        await dbContext.SaveChangesAsync();
    }

    /// <summary>Seeds Vec/User/FeeConfiguration/Session, returning the Session for further per-test customization.</summary>
    private static async Task<Session> SeedSessionAsync(
        AppDbContext dbContext, DateTime scheduledStartUtc, bool feeCollectionEnabled = true,
        SessionStatus status = SessionStatus.Active, string? zoomJoinUrl = "https://zoom.us/j/123")
    {
        var vec = new Vec { Name = "ARRL" };
        var user = new User { Name = "System", Email = "system@localhost", Role = UserRole.Admin };
        var feeConfiguration = new FeeConfiguration
        {
            Vec = vec, EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            FeeCollectionEnabled = feeCollectionEnabled, ExamFeeAmount = feeCollectionEnabled ? 15m : null,
            CreatedByUser = user, CreatedUtc = Now
        };
        var session = new Session
        {
            ExamToolsSessionId = "session-1", Title = "July Session", ScheduledStartUtc = scheduledStartUtc,
            DurationMinutes = 60, Vec = vec, FeeConfiguration = feeConfiguration, Status = status,
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
        await SeedEmailSettingsAndTemplatesAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, Now.AddDays(4));
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
        var result = await CreateService(dbContext, sender).SendRegistrationConfirmationsAsync(CancellationToken.None);

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
    }

    [Fact]
    public async Task RegistrationConfirmation_FeeCollectionDisabled_PaymentLinkUrlIsBlank()
    {
        await using var dbContext = CreateContext();
        await SeedEmailSettingsAndTemplatesAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, Now.AddDays(4), feeCollectionEnabled: false);
        dbContext.Candidates.Add(NewCandidate(session));
        await dbContext.SaveChangesAsync();

        var sender = new FakeEmailSender();
        await CreateService(dbContext, sender).SendRegistrationConfirmationsAsync(CancellationToken.None);

        var message = Assert.Single(sender.SentMessages);
        Assert.Contains("Pay: ,", message.HtmlBody); // blank, per "read sensibly either way"
    }

    [Fact]
    public async Task RegistrationConfirmation_AlreadySent_IsNotResent()
    {
        await using var dbContext = CreateContext();
        await SeedEmailSettingsAndTemplatesAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, Now.AddDays(4));
        var candidate = NewCandidate(session);
        candidate.RegistrationConfirmationSentUtc = Now.AddDays(-1);
        dbContext.Candidates.Add(candidate);
        await dbContext.SaveChangesAsync();

        var sender = new FakeEmailSender();
        var result = await CreateService(dbContext, sender).SendRegistrationConfirmationsAsync(CancellationToken.None);

        Assert.Equal(0, result.Sent);
        Assert.Empty(sender.SentMessages);
    }

    [Fact]
    public async Task RegistrationConfirmation_CancelledSession_IsExcluded()
    {
        await using var dbContext = CreateContext();
        await SeedEmailSettingsAndTemplatesAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, Now.AddDays(4), status: SessionStatus.Cancelled);
        dbContext.Candidates.Add(NewCandidate(session));
        await dbContext.SaveChangesAsync();

        var sender = new FakeEmailSender();
        var result = await CreateService(dbContext, sender).SendRegistrationConfirmationsAsync(CancellationToken.None);

        Assert.Equal(0, result.Sent);
        Assert.Empty(sender.SentMessages);
    }

    [Fact]
    public async Task RegistrationConfirmation_NoEmailSettingsRow_SkipsGracefully()
    {
        await using var dbContext = CreateContext();
        // Templates exist, but no EmailSettings row seeded.
        dbContext.EmailTemplates.Add(new EmailTemplate { Key = "RegistrationConfirmation", Subject = "s", Body = "b" });
        await dbContext.SaveChangesAsync();
        var session = await SeedSessionAsync(dbContext, Now.AddDays(4));
        dbContext.Candidates.Add(NewCandidate(session));
        await dbContext.SaveChangesAsync();

        var sender = new FakeEmailSender();
        var result = await CreateService(dbContext, sender).SendRegistrationConfirmationsAsync(CancellationToken.None);

        Assert.Equal(0, result.Sent);
        Assert.Empty(sender.SentMessages);
    }

    [Fact]
    public async Task RegistrationConfirmation_OneSendFailing_DoesNotBlockOthers_AndIsRetryable()
    {
        await using var dbContext = CreateContext();
        await SeedEmailSettingsAndTemplatesAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, Now.AddDays(4));
        dbContext.Candidates.Add(NewCandidate(session, "applicant-1", "Roana", "Glory"));
        dbContext.Candidates.Add(NewCandidate(session, "applicant-2", "Tomasina", "Susanna"));
        await dbContext.SaveChangesAsync();

        var sender = new FakeEmailSender { ThrowOnNextSend = new InvalidOperationException("SMTP down") };
        var result = await CreateService(dbContext, sender).SendRegistrationConfirmationsAsync(CancellationToken.None);

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
        await SeedEmailSettingsAndTemplatesAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, Now.Date.AddDays(1).AddHours(17)); // tomorrow, 5pm UTC
        dbContext.Candidates.Add(NewCandidate(session));
        await dbContext.SaveChangesAsync();

        var sender = new FakeEmailSender();
        var result = await CreateService(dbContext, sender).SendDayBeforeRemindersAsync(CancellationToken.None);

        Assert.Equal(1, result.Sent);
        Assert.Equal(Now, dbContext.Candidates.Single().DayBeforeReminderSentUtc);
    }

    [Fact]
    public async Task DayBeforeReminder_SessionToday_IsExcluded()
    {
        await using var dbContext = CreateContext();
        await SeedEmailSettingsAndTemplatesAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, Now.Date.AddHours(17)); // today
        dbContext.Candidates.Add(NewCandidate(session));
        await dbContext.SaveChangesAsync();

        var sender = new FakeEmailSender();
        var result = await CreateService(dbContext, sender).SendDayBeforeRemindersAsync(CancellationToken.None);

        Assert.Equal(0, result.Sent);
        Assert.Empty(sender.SentMessages);
    }

    [Fact]
    public async Task DayBeforeReminder_SessionDayAfterTomorrow_IsExcluded()
    {
        await using var dbContext = CreateContext();
        await SeedEmailSettingsAndTemplatesAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, Now.Date.AddDays(2).AddHours(17));
        dbContext.Candidates.Add(NewCandidate(session));
        await dbContext.SaveChangesAsync();

        var sender = new FakeEmailSender();
        var result = await CreateService(dbContext, sender).SendDayBeforeRemindersAsync(CancellationToken.None);

        Assert.Equal(0, result.Sent);
        Assert.Empty(sender.SentMessages);
    }

    [Fact]
    public async Task DayBeforeReminder_CancelledSession_IsExcludedEvenIfTomorrow()
    {
        await using var dbContext = CreateContext();
        await SeedEmailSettingsAndTemplatesAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, Now.Date.AddDays(1).AddHours(17), status: SessionStatus.Cancelled);
        dbContext.Candidates.Add(NewCandidate(session));
        await dbContext.SaveChangesAsync();

        var sender = new FakeEmailSender();
        var result = await CreateService(dbContext, sender).SendDayBeforeRemindersAsync(CancellationToken.None);

        Assert.Equal(0, result.Sent);
    }

    [Fact]
    public async Task DayBeforeReminder_AlreadySent_IsNotResent()
    {
        await using var dbContext = CreateContext();
        await SeedEmailSettingsAndTemplatesAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, Now.Date.AddDays(1).AddHours(17));
        var candidate = NewCandidate(session);
        candidate.DayBeforeReminderSentUtc = Now.AddHours(-1);
        dbContext.Candidates.Add(candidate);
        await dbContext.SaveChangesAsync();

        var sender = new FakeEmailSender();
        var result = await CreateService(dbContext, sender).SendDayBeforeRemindersAsync(CancellationToken.None);

        Assert.Equal(0, result.Sent);
        Assert.Empty(sender.SentMessages);
    }

    [Fact]
    public async Task DayBeforeReminder_OutstandingUnpaidPayment_IncludesItsLink()
    {
        await using var dbContext = CreateContext();
        await SeedEmailSettingsAndTemplatesAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, Now.Date.AddDays(1).AddHours(17));
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
        await CreateService(dbContext, sender).SendDayBeforeRemindersAsync(CancellationToken.None);

        var message = Assert.Single(sender.SentMessages);
        Assert.Contains("Outstanding: https://square.link/u/xyz", message.HtmlBody);
    }

    [Fact]
    public async Task DayBeforeReminder_NoOutstandingPayment_OutstandingPlaceholderIsBlank()
    {
        await using var dbContext = CreateContext();
        await SeedEmailSettingsAndTemplatesAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, Now.Date.AddDays(1).AddHours(17));
        dbContext.Candidates.Add(NewCandidate(session));
        await dbContext.SaveChangesAsync();

        var sender = new FakeEmailSender();
        await CreateService(dbContext, sender).SendDayBeforeRemindersAsync(CancellationToken.None);

        var message = Assert.Single(sender.SentMessages);
        Assert.EndsWith("Outstanding: ", message.HtmlBody);
    }

    [Fact]
    public async Task MissingTemplate_CountsAsFailed_DoesNotMarkSent_IsRetryable()
    {
        await using var dbContext = CreateContext();
        dbContext.EmailSettings.Add(new EmailSettings
        {
            FromAddress = "noreply@example.org", ReplyToAddress = "reply@example.org", PrivacyPolicyUrl = "https://example.org/privacy", AdminNotificationEmail = "admin@example.org"
        });
        // No RegistrationConfirmation template seeded at all.
        await dbContext.SaveChangesAsync();
        var session = await SeedSessionAsync(dbContext, Now.AddDays(4));
        dbContext.Candidates.Add(NewCandidate(session));
        await dbContext.SaveChangesAsync();

        var sender = new FakeEmailSender();
        var result = await CreateService(dbContext, sender).SendRegistrationConfirmationsAsync(CancellationToken.None);

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
        await SeedEmailSettingsAndTemplatesAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, Now.AddDays(4));
        dbContext.Candidates.Add(NewCandidate(session));
        await dbContext.SaveChangesAsync();

        var sender = new FakeEmailSender { IsConfigured = false };
        var result = await CreateService(dbContext, sender).SendRegistrationConfirmationsAsync(CancellationToken.None);

        Assert.Equal(0, result.Sent);
        Assert.Equal(0, result.Failed); // not attempted, so not a "failure"
        Assert.Empty(sender.SentMessages);
        Assert.Null(dbContext.Candidates.Single().RegistrationConfirmationSentUtc);

        // Once SMTP becomes configured, the very next poll must send the backlog automatically.
        sender.IsConfigured = true;
        var retryResult = await CreateService(dbContext, sender).SendRegistrationConfirmationsAsync(CancellationToken.None);

        Assert.Equal(1, retryResult.Sent);
        Assert.Single(sender.SentMessages);
    }

    [Fact]
    public async Task SmtpNotConfigured_DayBeforeReminder_SkipsQuietly_NoFailureCounted()
    {
        await using var dbContext = CreateContext();
        await SeedEmailSettingsAndTemplatesAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, Now.Date.AddDays(1).AddHours(17));
        dbContext.Candidates.Add(NewCandidate(session));
        await dbContext.SaveChangesAsync();

        var sender = new FakeEmailSender { IsConfigured = false };
        var result = await CreateService(dbContext, sender).SendDayBeforeRemindersAsync(CancellationToken.None);

        Assert.Equal(0, result.Sent);
        Assert.Equal(0, result.Failed);
        Assert.Empty(sender.SentMessages);
        Assert.Null(dbContext.Candidates.Single().DayBeforeReminderSentUtc);
    }
}
