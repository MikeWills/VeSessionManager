using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Email;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Payments;
using Xunit;

namespace VeSessionManager.Core.Tests;

public class PaymentReminderServiceTests
{
    private static readonly DateTime Now = new(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);

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

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static PaymentReminderService CreateService(AppDbContext dbContext, IEmailSender emailSender, int unmatchedReviewWindowDays = 5) => new(
        dbContext,
        new EmailTemplateRenderer(dbContext, NullLogger<EmailTemplateRenderer>.Instance),
        emailSender,
        new FixedTimeProvider(Now),
        Options.Create(new PaymentReminderOptions { UnmatchedReviewWindowDays = unmatchedReviewWindowDays }),
        NullLogger<PaymentReminderService>.Instance);

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
            ReplyToAddress = "reply@example.org",
            PrivacyPolicyUrl = "https://example.org/privacy",
            AdminNotificationEmail = "admin@example.org"
        });
        dbContext.EmailTemplates.Add(new EmailTemplate
        {
            TeamId = team.Id,
            Key = "PaymentReminder5Day",
            Subject = "Reminder",
            Body = "Hi {{CandidateName}}, Zoom: {{ZoomJoinUrl}}, Pay: {{PaymentLinkUrl}}"
        });
        dbContext.EmailTemplates.Add(new EmailTemplate
        {
            TeamId = team.Id,
            Key = "PaymentExpirationNotice",
            Subject = "Expired",
            Body = "{{CandidateName}} owes {{PaymentAmount}} from {{SessionDate}}"
        });
        await dbContext.SaveChangesAsync();
    }

    /// <summary>Seeds Vec/User/FeeConfiguration/Session/Candidate/Payment(Unpaid, InitialExam, $15) with the given status/date combination.</summary>
    private static async Task<(Candidate Candidate, Payment Payment)> SeedCandidateWithPaymentAsync(
        AppDbContext dbContext,
        Team team,
        CandidateApplicationStatus status = CandidateApplicationStatus.Received,
        DateTime? applicationDateEnteredUtc = null,
        DateTime? dateRegisteredUtc = null,
        SessionStatus sessionStatus = SessionStatus.Active,
        PaymentStatus paymentStatus = PaymentStatus.Unpaid,
        bool expiredUnpaid = false,
        DateTime? paymentReminderSentUtc = null)
    {
        var vec = new Vec { Name = "ARRL" };
        var user = new User { Name = "System", Email = "system@localhost", Role = UserRole.SystemAdmin };
        var feeConfiguration = new FeeConfiguration
        {
            Vec = vec, EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            FeeCollectionEnabled = true, ExamFeeAmount = 15m, CreatedByUser = user, CreatedUtc = Now
        };
        var session = new Session
        {
            ExamToolsSessionId = "session-1", Title = "July Session", ScheduledStartUtc = Now.AddDays(-3),
            DurationMinutes = 60, Vec = vec, TeamId = team.Id, FeeConfiguration = feeConfiguration, Status = sessionStatus,
            ZoomJoinUrl = "https://zoom.us/j/123", CreatedUtc = Now
        };
        dbContext.Sessions.Add(session);
        await dbContext.SaveChangesAsync();

        var candidate = new Candidate
        {
            ExamToolsApplicantId = "applicant-1", SessionId = session.Id, Name = "Roana Glory",
            Email = "roana@example.com", DateRegisteredUtc = dateRegisteredUtc ?? Now,
            ApplicationStatus = status, ApplicationDateEnteredUtc = applicationDateEnteredUtc
        };
        dbContext.Candidates.Add(candidate);
        await dbContext.SaveChangesAsync();

        var payment = new Payment
        {
            CandidateId = candidate.Id, Reason = PaymentReason.InitialExam, Amount = 15m,
            Status = paymentStatus, PaymentLinkUrl = "https://square.link/u/abc", CreatedUtc = Now,
            ExpiredUnpaid = expiredUnpaid, PaymentReminderSentUtc = paymentReminderSentUtc
        };
        dbContext.Payments.Add(payment);
        await dbContext.SaveChangesAsync();

        return (candidate, payment);
    }

    // ---- 5-day reminder ----

    [Fact]
    public async Task Reminder_ExactlyFiveDaysSinceApplicationDateEntered_Fires()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        await SeedCandidateWithPaymentAsync(dbContext, team, applicationDateEnteredUtc: Now.AddDays(-5));
        var sender = new FakeEmailSender();

        var result = await CreateService(dbContext, sender).RunAsync(team, CancellationToken.None);

        Assert.Equal(1, result.RemindersSent);
        var message = Assert.Single(sender.SentMessages);
        Assert.Equal("roana@example.com", message.ToAddress);
        Assert.NotNull((await dbContext.Payments.SingleAsync()).PaymentReminderSentUtc);
    }

    [Fact]
    public async Task Reminder_BeforeFiveDays_DoesNotFire()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        await SeedCandidateWithPaymentAsync(dbContext, team, applicationDateEnteredUtc: Now.AddDays(-4));
        var sender = new FakeEmailSender();

        var result = await CreateService(dbContext, sender).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.RemindersSent);
        Assert.Empty(sender.SentMessages);
    }

    [Fact]
    public async Task Reminder_AfterFiveDays_Fires()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        await SeedCandidateWithPaymentAsync(dbContext, team, applicationDateEnteredUtc: Now.AddDays(-6));
        var sender = new FakeEmailSender();

        var result = await CreateService(dbContext, sender).RunAsync(team, CancellationToken.None);

        Assert.Equal(1, result.RemindersSent);
    }

    [Fact]
    public async Task Reminder_AlreadySent_DoesNotResend()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        await SeedCandidateWithPaymentAsync(dbContext, team, applicationDateEnteredUtc: Now.AddDays(-8), paymentReminderSentUtc: Now.AddDays(-3));
        var sender = new FakeEmailSender();

        var result = await CreateService(dbContext, sender).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.RemindersSent);
        Assert.Empty(sender.SentMessages);
    }

    [Fact]
    public async Task Reminder_UnmatchedCandidate_NeverFires_NoApplicationDate()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        await SeedCandidateWithPaymentAsync(dbContext, team, status: CandidateApplicationStatus.Unmatched, applicationDateEnteredUtc: null);
        var sender = new FakeEmailSender();

        var result = await CreateService(dbContext, sender).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.RemindersSent);
    }

    [Fact]
    public async Task Reminder_GrantedCandidate_TerminalStatusSkipsIt()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        await SeedCandidateWithPaymentAsync(dbContext, team, status: CandidateApplicationStatus.Granted, applicationDateEnteredUtc: Now.AddDays(-8));
        var sender = new FakeEmailSender();

        var result = await CreateService(dbContext, sender).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.RemindersSent);
    }

    [Fact]
    public async Task Reminder_NotApplicablePayment_Skipped()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        await SeedCandidateWithPaymentAsync(dbContext, team, applicationDateEnteredUtc: Now.AddDays(-8), paymentStatus: PaymentStatus.NotApplicable);
        var sender = new FakeEmailSender();

        var result = await CreateService(dbContext, sender).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.RemindersSent);
    }

    [Fact]
    public async Task Reminder_CancelledSession_Skipped()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        await SeedCandidateWithPaymentAsync(dbContext, team, applicationDateEnteredUtc: Now.AddDays(-8), sessionStatus: SessionStatus.Cancelled);
        var sender = new FakeEmailSender();

        var result = await CreateService(dbContext, sender).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.RemindersSent);
    }

    [Fact]
    public async Task Reminder_MissingTemplate_CountsAsFailed_IsRetryable()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        dbContext.EmailSettings.Add(new EmailSettings
        {
            TeamId = team.Id,
            FromAddress = "noreply@example.org", ReplyToAddress = "reply@example.org",
            PrivacyPolicyUrl = "https://example.org/privacy", AdminNotificationEmail = "admin@example.org"
        });
        await dbContext.SaveChangesAsync();
        await SeedCandidateWithPaymentAsync(dbContext, team, applicationDateEnteredUtc: Now.AddDays(-5));
        var sender = new FakeEmailSender();

        var result = await CreateService(dbContext, sender).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.RemindersSent);
        Assert.Equal(1, result.Failed);
        Assert.Null((await dbContext.Payments.SingleAsync()).PaymentReminderSentUtc);
    }

    // ---- 10-day expiration ----

    [Fact]
    public async Task Expiration_ExactlyTenDays_Fires_SetsExpiredUnpaid_SendsToAdmin()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        await SeedCandidateWithPaymentAsync(dbContext, team, applicationDateEnteredUtc: Now.AddDays(-10));
        var sender = new FakeEmailSender();

        var result = await CreateService(dbContext, sender).RunAsync(team, CancellationToken.None);

        Assert.Equal(1, result.ExpirationsProcessed);
        var message = Assert.Single(sender.SentMessages, m => m.ToAddress == "admin@example.org");
        Assert.Contains("$15.00", message.HtmlBody);
        Assert.True((await dbContext.Payments.SingleAsync()).ExpiredUnpaid);
    }

    [Fact]
    public async Task Expiration_BeforeTenDays_DoesNotFire()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        await SeedCandidateWithPaymentAsync(dbContext, team, applicationDateEnteredUtc: Now.AddDays(-9));
        var sender = new FakeEmailSender();

        var result = await CreateService(dbContext, sender).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.ExpirationsProcessed);
        Assert.False((await dbContext.Payments.SingleAsync()).ExpiredUnpaid);
    }

    [Fact]
    public async Task Expiration_AfterTenDays_Fires()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        await SeedCandidateWithPaymentAsync(dbContext, team, applicationDateEnteredUtc: Now.AddDays(-11));
        var sender = new FakeEmailSender();

        var result = await CreateService(dbContext, sender).RunAsync(team, CancellationToken.None);

        Assert.Equal(1, result.ExpirationsProcessed);
    }

    [Fact]
    public async Task Expiration_AlreadyExpired_DoesNotResend()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        await SeedCandidateWithPaymentAsync(dbContext, team, applicationDateEnteredUtc: Now.AddDays(-20), expiredUnpaid: true);
        var sender = new FakeEmailSender();

        var result = await CreateService(dbContext, sender).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.ExpirationsProcessed);
        Assert.DoesNotContain(sender.SentMessages, m => m.ToAddress == "admin@example.org");
    }

    [Fact]
    public async Task Expiration_GrantedCandidate_TerminalStatusSkipsIt()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        await SeedCandidateWithPaymentAsync(dbContext, team, status: CandidateApplicationStatus.Granted, applicationDateEnteredUtc: Now.AddDays(-20));
        var sender = new FakeEmailSender();

        var result = await CreateService(dbContext, sender).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.ExpirationsProcessed);
    }

    [Fact]
    public async Task Expiration_NotApplicablePayment_Skipped()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        await SeedCandidateWithPaymentAsync(dbContext, team, applicationDateEnteredUtc: Now.AddDays(-20), paymentStatus: PaymentStatus.NotApplicable);
        var sender = new FakeEmailSender();

        var result = await CreateService(dbContext, sender).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.ExpirationsProcessed);
    }

    [Fact]
    public async Task Expiration_CancelledSession_Skipped()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        await SeedCandidateWithPaymentAsync(dbContext, team, applicationDateEnteredUtc: Now.AddDays(-20), sessionStatus: SessionStatus.Cancelled);
        var sender = new FakeEmailSender();

        var result = await CreateService(dbContext, sender).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.ExpirationsProcessed);
    }

    [Fact]
    public async Task ReminderAndExpiration_BothOverdueInOneRun_BothFire()
    {
        // Simulates catching up after downtime: a payment discovered for the first time at 12
        // days old is eligible for both triggers simultaneously — each is independently idempotent
        // via its own tracking field, so both firing in the same run is expected, not a bug.
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        await SeedCandidateWithPaymentAsync(dbContext, team, applicationDateEnteredUtc: Now.AddDays(-12));
        var sender = new FakeEmailSender();

        var result = await CreateService(dbContext, sender).RunAsync(team, CancellationToken.None);

        Assert.Equal(1, result.RemindersSent);
        Assert.Equal(1, result.ExpirationsProcessed);
        Assert.Equal(2, sender.SentMessages.Count);
    }

    // ---- Unmatched review flag ----

    [Fact]
    public async Task UnmatchedFlag_ExactlyWindowDays_Flags()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        var (candidate, _) = await SeedCandidateWithPaymentAsync(dbContext, team, status: CandidateApplicationStatus.Unmatched, dateRegisteredUtc: Now.AddDays(-5));
        var sender = new FakeEmailSender();

        var result = await CreateService(dbContext, sender).RunAsync(team, CancellationToken.None);

        Assert.Equal(1, result.CandidatesFlaggedForReview);
        Assert.NotNull((await dbContext.Candidates.SingleAsync(c => c.Id == candidate.Id)).UnmatchedReviewFlaggedUtc);
    }

    [Fact]
    public async Task UnmatchedFlag_BeforeWindow_DoesNotFlag()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        await SeedCandidateWithPaymentAsync(dbContext, team, status: CandidateApplicationStatus.Unmatched, dateRegisteredUtc: Now.AddDays(-4));
        var sender = new FakeEmailSender();

        var result = await CreateService(dbContext, sender).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.CandidatesFlaggedForReview);
    }

    [Fact]
    public async Task UnmatchedFlag_AlreadyFlagged_DoesNotReflag()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        var (candidate, _) = await SeedCandidateWithPaymentAsync(dbContext, team, status: CandidateApplicationStatus.Unmatched, dateRegisteredUtc: Now.AddDays(-10));
        candidate.UnmatchedReviewFlaggedUtc = Now.AddDays(-2);
        await dbContext.SaveChangesAsync();
        var sender = new FakeEmailSender();

        var result = await CreateService(dbContext, sender).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.CandidatesFlaggedForReview);
    }

    [Fact]
    public async Task UnmatchedFlag_ReceivedCandidate_NotFlagged()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        await SeedCandidateWithPaymentAsync(dbContext, team, status: CandidateApplicationStatus.Received, dateRegisteredUtc: Now.AddDays(-10), applicationDateEnteredUtc: Now.AddDays(-10));
        var sender = new FakeEmailSender();

        var result = await CreateService(dbContext, sender).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.CandidatesFlaggedForReview);
    }

    [Fact]
    public async Task UnmatchedFlag_CustomWindow_RespectsConfiguredValue()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        await SeedCandidateWithPaymentAsync(dbContext, team, status: CandidateApplicationStatus.Unmatched, dateRegisteredUtc: Now.AddDays(-3));
        var sender = new FakeEmailSender();

        var result = await CreateService(dbContext, sender, unmatchedReviewWindowDays: 3).RunAsync(team, CancellationToken.None);

        Assert.Equal(1, result.CandidatesFlaggedForReview);
    }

    [Fact]
    public async Task UnmatchedFlag_RunsEvenWhenNoEmailSettingsRowExists()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        // Deliberately no EmailSettings row seeded — reminders/expirations get skipped, but the
        // flag pass doesn't send email at all, so it should still run.
        var (candidate, _) = await SeedCandidateWithPaymentAsync(dbContext, team, status: CandidateApplicationStatus.Unmatched, dateRegisteredUtc: Now.AddDays(-5));
        var sender = new FakeEmailSender();

        var result = await CreateService(dbContext, sender).RunAsync(team, CancellationToken.None);

        Assert.Equal(1, result.CandidatesFlaggedForReview);
        Assert.NotNull((await dbContext.Candidates.SingleAsync(c => c.Id == candidate.Id)).UnmatchedReviewFlaggedUtc);
        Assert.Empty(sender.SentMessages);
    }

    // ---- SMTP not configured ----

    [Fact]
    public async Task SmtpNotConfigured_SkipsRemindersAndExpirations_ButStillFlagsUnmatched()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, emailConfigured: false);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        await SeedCandidateWithPaymentAsync(dbContext, team, applicationDateEnteredUtc: Now.AddDays(-20)); // due for both
        var (unmatchedCandidate, _) = await SeedCandidateWithPaymentAsync(dbContext, team, status: CandidateApplicationStatus.Unmatched, dateRegisteredUtc: Now.AddDays(-10));
        var sender = new FakeEmailSender();

        var result = await CreateService(dbContext, sender).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.RemindersSent);
        Assert.Equal(0, result.ExpirationsProcessed);
        Assert.Empty(sender.SentMessages);
        Assert.Equal(1, result.CandidatesFlaggedForReview);
        Assert.NotNull((await dbContext.Candidates.SingleAsync(c => c.Id == unmatchedCandidate.Id)).UnmatchedReviewFlaggedUtc);
    }

    // ---- PII purge ----

    [Fact]
    public async Task PurgedCandidate_ExcludedFromReminderAndFlagPasses()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        var (candidate, _) = await SeedCandidateWithPaymentAsync(dbContext, team, applicationDateEnteredUtc: Now.AddDays(-8));
        candidate.PiiPurgedUtc = Now;
        candidate.Email = null;
        await dbContext.SaveChangesAsync();
        var sender = new FakeEmailSender();

        var result = await CreateService(dbContext, sender).RunAsync(team, CancellationToken.None);

        Assert.Equal(0, result.RemindersSent);
    }
}
