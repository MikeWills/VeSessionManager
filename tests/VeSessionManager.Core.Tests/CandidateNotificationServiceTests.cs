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
    public async Task RegistrationConfirmation_SessionAlreadyEnded_IsSkipped()
    {
        // Issue #22: a candidate on a session ingested via the completed-session backfill window
        // already had their session happen — the automatic scan must not send a "you're
        // registered!" email for something already over.
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        var session = await SeedSessionAsync(dbContext, team, Now.AddDays(-15));
        dbContext.Candidates.Add(NewCandidate(session));
        await dbContext.SaveChangesAsync();

        var sender = new FakeEmailSender();
        var result = await CreateService(dbContext, sender).SendRegistrationConfirmationsAsync(team, CancellationToken.None);

        Assert.Equal(0, result.Sent);
        Assert.Empty(sender.SentMessages);
        Assert.Null(dbContext.Candidates.Single().RegistrationConfirmationSentUtc);
    }

    [Fact]
    public async Task ResendRegistrationConfirmationAsync_SessionAlreadyEnded_StillSends()
    {
        // The manual, admin-triggered "resend" action is unaffected by the past-session guard —
        // a human explicitly clicking resend means it regardless of the session's date.
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        var session = await SeedSessionAsync(dbContext, team, Now.AddDays(-15));
        var candidate = NewCandidate(session);
        dbContext.Candidates.Add(candidate);
        await dbContext.SaveChangesAsync();

        var sender = new FakeEmailSender();
        var result = await CreateService(dbContext, sender).ResendRegistrationConfirmationAsync(candidate.Id, CancellationToken.None);

        Assert.Equal(CandidateEmailSendResult.Sent, result);
        Assert.Single(sender.SentMessages);
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

    /// <summary>
    /// The rule is a rolling 24-hour window ending at the session start, not a calendar date (#220).
    /// Now is 12:00 UTC, so a session at 08:00 tomorrow is 20 hours out and inside it.
    /// </summary>
    [Fact]
    public async Task PreSessionReminder_SessionWithinTheNext24Hours_IsIncluded()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        var session = await SeedSessionAsync(dbContext, team, Now.AddHours(20));
        dbContext.Candidates.Add(NewCandidate(session));
        await dbContext.SaveChangesAsync();

        var sender = new FakeEmailSender();
        var result = await CreateService(dbContext, sender).SendDayBeforeRemindersAsync(team, CancellationToken.None);

        Assert.Equal(1, result.Sent);
        Assert.Equal(Now, dbContext.Candidates.Single().DayBeforeReminderSentUtc);
    }

    [Fact]
    public async Task PreSessionReminder_SessionMoreThan24HoursAway_IsExcluded()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        var session = await SeedSessionAsync(dbContext, team, Now.AddHours(25));
        dbContext.Candidates.Add(NewCandidate(session));
        await dbContext.SaveChangesAsync();

        var sender = new FakeEmailSender();
        var result = await CreateService(dbContext, sender).SendDayBeforeRemindersAsync(team, CancellationToken.None);

        Assert.Equal(0, result.Sent);
        Assert.Empty(sender.SentMessages);
    }

    /// <summary>A reminder about something already under way is not a reminder.</summary>
    [Fact]
    public async Task PreSessionReminder_SessionAlreadyStarted_IsExcluded()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        var session = await SeedSessionAsync(dbContext, team, Now.AddHours(-1));
        dbContext.Candidates.Add(NewCandidate(session));
        await dbContext.SaveChangesAsync();

        var sender = new FakeEmailSender();
        var result = await CreateService(dbContext, sender).SendDayBeforeRemindersAsync(team, CancellationToken.None);

        Assert.Equal(0, result.Sent);
        Assert.Empty(sender.SentMessages);
    }

    /// <summary>
    /// ⚠️ The regression this issue was about. A session 22 hours away, on the SAME UTC calendar
    /// date as now, got no reminder under the old rule: "tomorrow" was a calendar date, and this
    /// session is today. It would then be reminded never — the window had already passed it by.
    ///
    /// <para>That is not a corner case. Sessions run in the evening Eastern, so a session late on
    /// the current UTC date is the normal shape whenever the job ticks in the early hours UTC — and
    /// which side of UTC midnight the job ticked on depended on when the Worker was last
    /// deployed.</para>
    /// </summary>
    [Fact]
    public async Task PreSessionReminder_SessionLaterTodayButNearlyADayAway_IsIncluded()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);

        // 12:00 UTC now; a session at 10:00 UTC tomorrow is 22 hours out. Under the calendar rule
        // this fell in "tomorrow" — but shift the clock a few hours either way and the identical
        // session lands on today's date and is silently skipped. The instant comparison does not
        // care which date it is.
        var session = await SeedSessionAsync(dbContext, team, Now.AddHours(22));
        dbContext.Candidates.Add(NewCandidate(session));
        await dbContext.SaveChangesAsync();

        var sender = new FakeEmailSender();
        var result = await CreateService(dbContext, sender).SendDayBeforeRemindersAsync(team, CancellationToken.None);

        Assert.Equal(1, result.Sent);
    }

    /// <summary>
    /// A real-world shape: an 8pm Eastern session is stored as the NEXT day in UTC (EDT is UTC-4).
    /// The reminder must be driven by how far away it is, not by whose calendar date it falls on.
    /// </summary>
    [Fact]
    public async Task PreSessionReminder_EveningEasternSessionStoredOnTheFollowingUtcDate_IsHandledByDistanceNotDate()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);

        // 8pm EDT on the 20th == 00:00 UTC on the 21st: a different UTC date from "now", but only
        // 12 hours away.
        var session = await SeedSessionAsync(dbContext, team, new DateTime(2026, 7, 21, 0, 0, 0, DateTimeKind.Utc));
        dbContext.Candidates.Add(NewCandidate(session));
        await dbContext.SaveChangesAsync();

        var sender = new FakeEmailSender();
        var result = await CreateService(dbContext, sender).SendDayBeforeRemindersAsync(team, CancellationToken.None);

        Assert.Equal(1, result.Sent);
    }

    /// <summary>
    /// Once only, however often the job ticks inside the window — the guard field, not the window,
    /// is what prevents a second send. Worth pinning now the window is 24 hours wide rather than a
    /// single calendar day: a job on a short interval sees the same candidate many times.
    /// </summary>
    [Fact]
    public async Task PreSessionReminder_RunTwiceInsideTheWindow_SendsOnce()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        var session = await SeedSessionAsync(dbContext, team, Now.AddHours(20));
        dbContext.Candidates.Add(NewCandidate(session));
        await dbContext.SaveChangesAsync();

        var sender = new FakeEmailSender();
        var service = CreateService(dbContext, sender);
        await service.SendDayBeforeRemindersAsync(team, CancellationToken.None);
        var second = await service.SendDayBeforeRemindersAsync(team, CancellationToken.None);

        Assert.Equal(0, second.Sent);
        Assert.Single(sender.SentMessages);
    }

    [Fact]
    public async Task PreSessionReminder_SessionDayAfterTomorrow_IsExcluded()
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
        var session = await SeedSessionAsync(dbContext, team, Now.AddHours(20), status: SessionStatus.Cancelled);
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
        var session = await SeedSessionAsync(dbContext, team, Now.AddHours(20));
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
        var session = await SeedSessionAsync(dbContext, team, Now.AddHours(20));
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
        var session = await SeedSessionAsync(dbContext, team, Now.AddHours(20));
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
        var session = await SeedSessionAsync(dbContext, team, Now.AddHours(20));
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

    // ---- onlySessionId filter (session-scoped Detail-page refresh, 2026-08-03) ----

    [Fact]
    public async Task RegistrationConfirmation_WithOnlySessionId_SendsOnlyForThatSession()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        var sessionA = await SeedSessionAsync(dbContext, team, Now.AddDays(4));
        var sessionB = await SeedSessionAsync(dbContext, team, Now.AddDays(5));
        dbContext.Candidates.Add(NewCandidate(sessionA, "applicant-a", "Roana"));
        dbContext.Candidates.Add(NewCandidate(sessionB, "applicant-b", "Tomasina"));
        await dbContext.SaveChangesAsync();

        var sender = new FakeEmailSender();
        var result = await CreateService(dbContext, sender).SendRegistrationConfirmationsAsync(team, CancellationToken.None, sessionA.Id);

        Assert.Equal(1, result.Sent);
        var message = Assert.Single(sender.SentMessages);
        Assert.Equal("roana@example.com", message.ToAddress);
        // The other session's candidate is neither emailed nor marked — it waits for the next
        // team-wide tick, whose null onlySessionId still scans everything (covered by the
        // existing tests above).
        Assert.Null(dbContext.Candidates.Single(c => c.ExamToolsApplicantId == "applicant-b").RegistrationConfirmationSentUtc);
    }
}
