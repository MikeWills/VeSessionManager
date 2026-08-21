using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VeSessionManager.Core;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Email;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Integrations;
using VeSessionManager.Core.Messaging;
using VeSessionManager.Core.Messaging.Scanners;
using VeSessionManager.Core.Payments;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// The trigger-point engine (#401), driven end to end: scanner predicate, dispatch, marker.
///
/// <para><b>Most of this file was CandidateNotificationServiceTests.</b> PR1 moved four hardcoded
/// sends onto rules and promised behaviour would not change, so the tests that pinned that behaviour
/// moved with them rather than being rewritten — a fresh set of tests written against the new code
/// would have proved only that the new code does what it does.</para>
/// </summary>
public class MessageRuleEngineTests
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

    /// <summary>One rule pass over one trigger, which is what every test below is doing.</summary>
    private static Task<MessageRuleResult> RunRulesAsync(
        AppDbContext dbContext, IEmailSender sender, Team team, MessageTrigger trigger, int? onlySessionId = null) =>
        MessageRuleTestHarness.Create(dbContext, sender, new FixedTimeProvider(Now))
            .RunAsync(team, [trigger], onlySessionId, CancellationToken.None);

    /// <summary>
    /// A rule created well in the past, so <c>MessageRule.CreatedUtc</c> bounds nothing and each test
    /// is about the predicate it was written for. The tests that <i>are</i> about that bound pass
    /// their own <paramref name="createdUtc"/>.
    /// </summary>
    private static async Task<MessageRule> SeedRuleAsync(
        AppDbContext dbContext, Team team, MessageTrigger trigger, string templateKey, int? parameterHours,
        MessageRecipient recipient = MessageRecipient.Candidate, DateTime? createdUtc = null,
        string? subject = null, string? body = null)
    {
        // A message owns its words, so the rule carries them. The key is now just a name for a
        // standard pair of subject/body kept in this file — the tests that assert on rendered output
        // still get the text they were written against, and the ones with their own wording pass it.
        var (standardSubject, standardBody) = StandardText.TryGetValue(templateKey, out var text)
            ? text
            : (templateKey, templateKey);

        var rule = MessageRuleTestHarness.NewRule(team, trigger, body ?? standardBody, parameterHours, createdUtc ?? Now.AddYears(-1), recipient);
        rule.Subject = subject ?? standardSubject;
        dbContext.MessageRules.Add(rule);
        await dbContext.SaveChangesAsync();
        return rule;
    }

    /// <summary>
    /// Records that this team's rule for <paramref name="trigger"/> already fired for a subject —
    /// which is what stops a resend now, in place of the <c>…SentUtc</c> column that used to.
    /// </summary>
    private static async Task MarkAlreadyFiredAsync(
        AppDbContext dbContext, Team team, MessageTrigger trigger, int subjectId, MessageSubjectType subjectType)
    {
        var rule = await dbContext.MessageRules.FirstAsync(r => r.TeamId == team.Id && r.Trigger == trigger);
        dbContext.MessageRuleRuns.Add(new MessageRuleRun
        {
            TeamId = team.Id,
            MessageRuleId = rule.Id,
            RuleName = rule.Name,
            Trigger = trigger,
            SubjectType = subjectType,
            SubjectId = subjectId,
            FiredUtc = Now.AddDays(-1),
            Outcome = MessageRuleOutcome.Sent
        });
        await dbContext.SaveChangesAsync();
    }

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

    /// <summary>
    /// The wording the rendering assertions in this file were written against. It lived in seeded
    /// EmailTemplate rows until 2026-08-21; a message owns its words now, and there is no table left
    /// to seed it into, so it lives here — which is nearer the assertions that depend on it anyway.
    /// </summary>
    private static readonly Dictionary<string, (string Subject, string Body)> StandardText = new()
    {
        ["RegistrationConfirmation"] = (
            "Registered for {{SessionDate}}",
            "Hi {{CandidateFirstName}} ({{CandidateName}}), Zoom: {{ZoomJoinUrl}}, Pay: {{PaymentLinkUrl}}, Privacy: {{PrivacyPolicyUrl}}"),
        ["DayBeforeReminder"] = (
            "Reminder for {{SessionDate}}",
            "Hi {{CandidateFirstName}}, Zoom: {{ZoomJoinUrl}}, Outstanding: {{OutstandingPaymentLinkUrl}}")
    };

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
        await dbContext.SaveChangesAsync();

        await SeedRuleAsync(dbContext, team, MessageTrigger.CandidateRegistered, "RegistrationConfirmation", null);
        await SeedRuleAsync(dbContext, team, MessageTrigger.BeforeSessionStart, "DayBeforeReminder", 24);
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
        var result = await RunRulesAsync(dbContext, sender, team, MessageTrigger.CandidateRegistered);

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
        await RunRulesAsync(dbContext, sender, team, MessageTrigger.CandidateRegistered);

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
        await dbContext.SaveChangesAsync();
        await SeedRuleAsync(dbContext, team, MessageTrigger.CandidateRegistered, "RegistrationConfirmation", null,
            body: "Pay: {{PaymentLinkUrl}}, Youth: {{YouthPaymentLinkUrl}}");
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
        await RunRulesAsync(dbContext, sender, team, MessageTrigger.CandidateRegistered);

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
        await dbContext.SaveChangesAsync();
        await SeedRuleAsync(dbContext, team, MessageTrigger.CandidateRegistered, "RegistrationConfirmation", null,
            body: "Youth: {{YouthPaymentLinkUrl}}.");
        var session = await SeedSessionAsync(dbContext, team, Now.AddDays(4), supportsYouthProgram: false);
        dbContext.Candidates.Add(NewCandidate(session));
        await dbContext.SaveChangesAsync();

        var sender = new FakeEmailSender();
        await RunRulesAsync(dbContext, sender, team, MessageTrigger.CandidateRegistered);

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
        var result = await RunRulesAsync(dbContext, sender, team, MessageTrigger.CandidateRegistered);

        Assert.Equal(0, result.Sent);
        Assert.Empty(sender.SentMessages);
        Assert.Null(dbContext.Candidates.Single().RegistrationConfirmationSentUtc);
    }

    [Fact]
    public async Task RegistrationConfirmation_AlreadySent_IsNotResent()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        var session = await SeedSessionAsync(dbContext, team, Now.AddDays(4));
        var candidate = NewCandidate(session);
        dbContext.Candidates.Add(candidate);
        await dbContext.SaveChangesAsync();
        await MarkAlreadyFiredAsync(dbContext, team, MessageTrigger.CandidateRegistered, candidate.Id, MessageSubjectType.Candidate);

        var sender = new FakeEmailSender();
        var result = await RunRulesAsync(dbContext, sender, team, MessageTrigger.CandidateRegistered);

        Assert.Equal(0, result.Sent);
        Assert.Empty(sender.SentMessages);
    }

    /// <summary>
    /// <c>Candidate.RegistrationConfirmationSentUtc</c> is <b>not</b> what suppresses a resend any
    /// more — the run marker is (#401). Worth its own test because the column is still written and
    /// still rendered on the Email history screen, so it looks exactly as authoritative as it used to.
    ///
    /// <para>The rows this leaves exposed — already emailed before trigger points existed, so carrying
    /// a timestamp and no marker — are precisely what the MessageRules migration's backfill is for,
    /// and what the seeded rule's CreatedUtc bound catches if the backfill ever misses one.</para>
    /// </summary>
    [Fact]
    public async Task RegistrationConfirmation_LegacySentUtcWithNoRunMarker_IsNotWhatStopsAResend()
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
        var result = await RunRulesAsync(dbContext, sender, team, MessageTrigger.CandidateRegistered);

        Assert.Equal(1, result.Sent);
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
        var result = await RunRulesAsync(dbContext, sender, team, MessageTrigger.CandidateRegistered);

        Assert.Equal(0, result.Sent);
        Assert.Empty(sender.SentMessages);
    }

    [Fact]
    public async Task RegistrationConfirmation_NoEmailSettingsRow_SkipsGracefully()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        // A message exists and would send, but no EmailSettings row is seeded — which is the thing
        // under test. Until 2026-08-21 this seeded a TEMPLATE and no rule, so the engine had nothing
        // to send either way and the assertion held whether or not the settings row was there.
        await dbContext.SaveChangesAsync();
        await SeedRuleAsync(dbContext, team, MessageTrigger.CandidateRegistered, "RegistrationConfirmation", null,
            subject: "s", body: "b");
        var session = await SeedSessionAsync(dbContext, team, Now.AddDays(4));
        dbContext.Candidates.Add(NewCandidate(session));
        await dbContext.SaveChangesAsync();

        var sender = new FakeEmailSender();
        var result = await RunRulesAsync(dbContext, sender, team, MessageTrigger.CandidateRegistered);

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
        var result = await RunRulesAsync(dbContext, sender, team, MessageTrigger.CandidateRegistered);

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
        var result = await RunRulesAsync(dbContext, sender, team, MessageTrigger.BeforeSessionStart);

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
        var result = await RunRulesAsync(dbContext, sender, team, MessageTrigger.BeforeSessionStart);

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
        var result = await RunRulesAsync(dbContext, sender, team, MessageTrigger.BeforeSessionStart);

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
        var result = await RunRulesAsync(dbContext, sender, team, MessageTrigger.BeforeSessionStart);

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
        var result = await RunRulesAsync(dbContext, sender, team, MessageTrigger.BeforeSessionStart);

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
        await RunRulesAsync(dbContext, sender, team, MessageTrigger.BeforeSessionStart);
        var second = await RunRulesAsync(dbContext, sender, team, MessageTrigger.BeforeSessionStart);

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
        var result = await RunRulesAsync(dbContext, sender, team, MessageTrigger.BeforeSessionStart);

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
        var result = await RunRulesAsync(dbContext, sender, team, MessageTrigger.BeforeSessionStart);

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
        dbContext.Candidates.Add(candidate);
        await dbContext.SaveChangesAsync();
        await MarkAlreadyFiredAsync(dbContext, team, MessageTrigger.BeforeSessionStart, candidate.Id, MessageSubjectType.Candidate);

        var sender = new FakeEmailSender();
        var result = await RunRulesAsync(dbContext, sender, team, MessageTrigger.BeforeSessionStart);

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
        await RunRulesAsync(dbContext, sender, team, MessageTrigger.BeforeSessionStart);

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
        await RunRulesAsync(dbContext, sender, team, MessageTrigger.BeforeSessionStart);

        var message = Assert.Single(sender.SentMessages);
        Assert.EndsWith("Outstanding: ", message.HtmlBody);
    }

    // ⚠️ MissingTemplate_CountsAsFailed_DoesNotMarkSent_IsRetryable was deleted here on 2026-08-21.
    //
    // It seeded a rule pointing at a template that did not exist and asserted the send recorded
    // Failed, left the candidate unmarked, and stayed retryable. A message carries its own words
    // now, so a rule cannot point at anything and there is nothing to be missing — the failure is
    // unreachable rather than handled, which is why no replacement stands here.
    //
    // What it was really protecting — that a failed send is not marked sent and comes back next
    // tick — is still covered by the SMTP-not-configured and send-throws tests below.

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
        var result = await RunRulesAsync(dbContext, sender, team, MessageTrigger.CandidateRegistered);

        Assert.Equal(0, result.Sent);
        Assert.Equal(0, result.Failed); // not attempted, so not a "failure"
        Assert.Empty(sender.SentMessages);
        Assert.Null(dbContext.Candidates.Single().RegistrationConfirmationSentUtc);

        // Once SMTP becomes configured, the very next poll must send the backlog automatically.
        team.SmtpHost = "smtp.example.org";
        team.SmtpUsername = "smtp-user";
        team.SmtpPassword = "smtp-pass";
        await dbContext.SaveChangesAsync();
        var retryResult = await RunRulesAsync(dbContext, sender, team, MessageTrigger.CandidateRegistered);

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
        var result = await RunRulesAsync(dbContext, sender, team, MessageTrigger.BeforeSessionStart);

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
        await dbContext.SaveChangesAsync();
        await SeedRuleAsync(dbContext, teamA, MessageTrigger.CandidateRegistered, "RegistrationConfirmation", null,
            subject: "A subject", body: "Team A body");
        await SeedRuleAsync(dbContext, teamB, MessageTrigger.CandidateRegistered, "RegistrationConfirmation", null,
            subject: "B subject", body: "Team B body");
        var sessionA = await SeedSessionAsync(dbContext, teamA, Now.AddDays(4));
        var sessionB = await SeedSessionAsync(dbContext, teamB, Now.AddDays(4));
        dbContext.Candidates.Add(NewCandidate(sessionA, "applicant-a"));
        dbContext.Candidates.Add(NewCandidate(sessionB, "applicant-b"));
        await dbContext.SaveChangesAsync();

        var sender = new FakeEmailSender();
        await RunRulesAsync(dbContext, sender, teamA, MessageTrigger.CandidateRegistered);
        await RunRulesAsync(dbContext, sender, teamB, MessageTrigger.CandidateRegistered);

        Assert.Equal(2, sender.SentMessages.Count);
        Assert.Contains(sender.SentMessages, m => m.FromAddress == "a@example.org" && m.HtmlBody == "Team A body");
        Assert.Contains(sender.SentMessages, m => m.FromAddress == "b@example.org" && m.HtmlBody == "Team B body");
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
        var result = await RunRulesAsync(dbContext, sender, team, MessageTrigger.CandidateRegistered, sessionA.Id);

        Assert.Equal(1, result.Sent);
        var message = Assert.Single(sender.SentMessages);
        Assert.Equal("roana@example.com", message.ToAddress);
        // The other session's candidate is neither emailed nor marked — it waits for the next
        // team-wide tick, whose null onlySessionId still scans everything (covered by the
        // existing tests above).
        Assert.Null(dbContext.Candidates.Single(c => c.ExamToolsApplicantId == "applicant-b").RegistrationConfirmationSentUtc);
    }

    // ---- CreatedUtc: a new rule never fires for anyone already past the moment ----

    /// <summary>
    /// Mike's requirement on the issue, in one test: "nothing worse than sending out 3000 emails
    /// because you added a new rule". A rule added now must not reach somebody who registered
    /// yesterday.
    ///
    /// <para>Mutation-checked by hand before it was kept: deleting
    /// <c>c.DateRegisteredUtc &gt;= rule.CreatedUtc</c> from <c>CandidateRegisteredScanner</c> makes
    /// this fail and leaves every other test in this file green — which is the whole reason it is
    /// here, because nothing else notices.</para>
    /// </summary>
    [Fact]
    public async Task ANewRule_DoesNotFireForACandidateWhoRegisteredBeforeItExisted()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        var session = await SeedSessionAsync(dbContext, team, Now.AddDays(4));
        var candidate = NewCandidate(session);
        candidate.DateRegisteredUtc = Now.AddDays(-2);
        dbContext.Candidates.Add(candidate);
        await dbContext.SaveChangesAsync();

        // Replaces the far-past rule the helper seeded with one created an hour ago.
        dbContext.MessageRules.RemoveRange(dbContext.MessageRules.Where(r => r.Trigger == MessageTrigger.CandidateRegistered));
        await dbContext.SaveChangesAsync();
        await SeedRuleAsync(dbContext, team, MessageTrigger.CandidateRegistered, "RegistrationConfirmation", null, createdUtc: Now.AddHours(-1));

        var sender = new FakeEmailSender();
        var result = await RunRulesAsync(dbContext, sender, team, MessageTrigger.CandidateRegistered);

        Assert.Equal(0, result.Sent);
        Assert.Empty(sender.SentMessages);
    }

    /// <summary>
    /// The same guarantee for a time-relative trigger, where the moment is computed rather than
    /// stored — <c>start - ParameterHours</c>, which has to fall at or after the rule's creation.
    /// This is the scenario from the issue: add a 7-day reminder today, and the candidate whose
    /// session is in three days hears nothing, because their seven-day mark is behind them.
    /// </summary>
    [Fact]
    public async Task ANewTimeRelativeRule_DoesNotFireForSomeoneAlreadyInsideItsWindow()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        // Three days out, and the new rule fires a week ahead — so its moment passed four days ago.
        var session = await SeedSessionAsync(dbContext, team, Now.AddDays(3));
        dbContext.Candidates.Add(NewCandidate(session));
        await dbContext.SaveChangesAsync();

        dbContext.MessageRules.RemoveRange(dbContext.MessageRules.Where(r => r.Trigger == MessageTrigger.BeforeSessionStart));
        await dbContext.SaveChangesAsync();
        await SeedRuleAsync(dbContext, team, MessageTrigger.BeforeSessionStart, "DayBeforeReminder", 168, createdUtc: Now);

        var sender = new FakeEmailSender();
        var result = await RunRulesAsync(dbContext, sender, team, MessageTrigger.BeforeSessionStart);

        Assert.Equal(0, result.Sent);

        // The same rule, the same candidate, the same window — created a month ago instead of now,
        // and it fires. CreatedUtc is the only difference, which is what stops this passing against a
        // rule that never fires at all.
        dbContext.MessageRules.RemoveRange(dbContext.MessageRules.Where(r => r.Trigger == MessageTrigger.BeforeSessionStart));
        await dbContext.SaveChangesAsync();
        await SeedRuleAsync(dbContext, team, MessageTrigger.BeforeSessionStart, "DayBeforeReminder", 168, createdUtc: Now.AddDays(-30));

        Assert.Equal(1, (await RunRulesAsync(dbContext, sender, team, MessageTrigger.BeforeSessionStart)).Sent);
    }

    // ---- Outcomes: what the marker records, and which ones settle ----

    [Fact]
    public async Task EmailSwitchedOffForTheTeam_RecordsSuppressed_AndNeverSends()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        // Both, because the per-integration switches only apply while the master override is on.
        team.IntegrationOverridesEnabled = true;
        team.EmailEnabled = false;
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        var session = await SeedSessionAsync(dbContext, team, Now.AddDays(4));
        dbContext.Candidates.Add(NewCandidate(session));
        await dbContext.SaveChangesAsync();

        var sender = new FakeEmailSender();
        var result = await RunRulesAsync(dbContext, sender, team, MessageTrigger.CandidateRegistered);

        Assert.Equal(0, result.Sent);
        Assert.Equal(1, result.Suppressed);
        Assert.Empty(sender.SentMessages);
        Assert.Equal(MessageRuleOutcome.Suppressed, dbContext.MessageRuleRuns.Single().Outcome);

        // Suppressed settles. Nothing is queued while the switch is off, so turning it back on starts
        // fresh from that moment rather than flushing a backlog — and the candidate's history is not
        // told an email went out.
        Assert.Null(dbContext.Candidates.Single().RegistrationConfirmationSentUtc);

        team.EmailEnabled = true;
        await dbContext.SaveChangesAsync();
        Assert.Equal(0, (await RunRulesAsync(dbContext, sender, team, MessageTrigger.CandidateRegistered)).Sent);
    }

    /// <summary>
    /// Unconfigured SMTP is the opposite case and must stay that way: no marker at all, so everything
    /// waiting goes out on the first tick after credentials are entered. This is the optional-integration
    /// pattern, and writing a marker here would turn "setup unfinished" into "permanently skipped".
    /// </summary>
    [Fact]
    public async Task SmtpNotConfigured_WritesNoMarker_SoItSendsOnceConfigured()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, emailConfigured: false);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        var session = await SeedSessionAsync(dbContext, team, Now.AddDays(4));
        dbContext.Candidates.Add(NewCandidate(session));
        await dbContext.SaveChangesAsync();

        var sender = new FakeEmailSender();
        var result = await RunRulesAsync(dbContext, sender, team, MessageTrigger.CandidateRegistered);

        Assert.Equal(1, result.Waiting);
        Assert.Empty(dbContext.MessageRuleRuns);

        team.SmtpHost = "smtp.example.org";
        team.SmtpUsername = "smtp-user";
        team.SmtpPassword = "smtp-pass";
        await dbContext.SaveChangesAsync();

        Assert.Equal(1, (await RunRulesAsync(dbContext, sender, team, MessageTrigger.CandidateRegistered)).Sent);
    }

    /// <summary>
    /// A failed send has always retried on the next tick, and that survives the move onto rules. The
    /// marker is written — it is the log, and a failure nobody can see is what this table exists to
    /// end — but <see cref="MessageRuleOutcome.Failed"/> is not terminal, so the subject comes back,
    /// and the second attempt <b>updates that row</b> rather than adding one. Without the update the
    /// unique index would throw on every retry.
    /// </summary>
    [Fact]
    public async Task AFailedSend_IsLoggedAndRetried_AndTheRetryUpdatesTheSameRow()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        var session = await SeedSessionAsync(dbContext, team, Now.AddDays(4));
        dbContext.Candidates.Add(NewCandidate(session));
        await dbContext.SaveChangesAsync();

        var sender = new FakeEmailSender { ThrowOnNextSend = new InvalidOperationException("SMTP said no") };
        var first = await RunRulesAsync(dbContext, sender, team, MessageTrigger.CandidateRegistered);

        Assert.Equal(1, first.Failed);
        var failedRun = dbContext.MessageRuleRuns.Single();
        Assert.Equal(MessageRuleOutcome.Failed, failedRun.Outcome);
        Assert.Contains("SMTP said no", failedRun.Detail);
        Assert.Null(dbContext.Candidates.Single().RegistrationConfirmationSentUtc);

        var second = await RunRulesAsync(dbContext, sender, team, MessageTrigger.CandidateRegistered);

        Assert.Equal(1, second.Sent);
        var run = Assert.Single(dbContext.MessageRuleRuns);
        Assert.Equal(MessageRuleOutcome.Sent, run.Outcome);
        Assert.Null(run.Detail);
    }

    /// <summary>
    /// A candidate with no address is not settled either — an address filled in later should still get
    /// the message. The scanners already require <c>Email != null</c>, so this exercises the
    /// dispatcher's own answer through the one recipient that can go missing independently: a team
    /// whose admin notification address is blank.
    /// </summary>
    [Fact]
    public async Task NoAddressForTheRecipient_RecordsNoRecipient_AndIsNotSettled()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        var settings = dbContext.EmailSettings.Single();
        settings.AdminNotificationEmail = "";
        // A rule that reuses the registration template but points it at the team's own inbox.
        await SeedRuleAsync(dbContext, team, MessageTrigger.BeforeSessionStart, "DayBeforeReminder", 24, MessageRecipient.TeamAdminAddress);
        dbContext.MessageRules.RemoveRange(dbContext.MessageRules.Where(r => r.Trigger == MessageTrigger.BeforeSessionStart && r.Recipient == MessageRecipient.Candidate));
        var session = await SeedSessionAsync(dbContext, team, Now.AddHours(20));
        dbContext.Candidates.Add(NewCandidate(session));
        await dbContext.SaveChangesAsync();

        var sender = new FakeEmailSender();
        var result = await RunRulesAsync(dbContext, sender, team, MessageTrigger.BeforeSessionStart);

        Assert.Equal(1, result.NoRecipient);
        Assert.Equal(MessageRuleOutcome.NoRecipient, dbContext.MessageRuleRuns.Single().Outcome);

        settings.AdminNotificationEmail = "admin@example.org";
        await dbContext.SaveChangesAsync();

        Assert.Equal(1, (await RunRulesAsync(dbContext, sender, team, MessageTrigger.BeforeSessionStart)).Sent);
    }

    /// <summary>
    /// #205's lesson, applied to the engine: assert what a placeholder <i>renders to</i>, not that it
    /// was passed. The notification tests used {{SessionDate}} in subjects and never asserted its
    /// output, so a green suite coexisted with every candidate email rendering UTC for months.
    /// </summary>
    [Fact]
    public async Task SessionDate_RendersAsEasternAndPacific_NotUtc()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        // 14:00 UTC is 10:00 ET / 7:00 PT — three time zones apart in one string, and none of them UTC.
        var session = await SeedSessionAsync(dbContext, team, new DateTime(2026, 7, 24, 14, 0, 0, DateTimeKind.Utc));
        dbContext.Candidates.Add(NewCandidate(session));
        await dbContext.SaveChangesAsync();

        var sender = new FakeEmailSender();
        await RunRulesAsync(dbContext, sender, team, MessageTrigger.CandidateRegistered);

        var message = Assert.Single(sender.SentMessages);
        Assert.Contains("10:00 AM ET", message.Subject);
        Assert.Contains("7:00 AM PT", message.Subject);
    }

    /// <summary>
    /// A Discord rule on a team with no guild waits rather than failing, and leaves no marker — the
    /// optional-integration pattern, same as unconfigured SMTP.
    ///
    /// <para>This test asserted the opposite until PR4. PR1 declared the Discord channel in the model
    /// and refused it at dispatch, so "not implemented" was a state a rule could be in; now that it is
    /// implemented, what remains is "not configured yet", which must backfill rather than settle.
    /// <c>DiscordMessageRuleTests</c> covers the working path.</para>
    /// </summary>
    [Fact]
    public async Task ADiscordRuleOnATeamWithNoGuild_Waits_AndLeavesNoMarker()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        dbContext.MessageRules.Single(r => r.Trigger == MessageTrigger.CandidateRegistered).Channel = MessageChannel.Discord;
        var session = await SeedSessionAsync(dbContext, team, Now.AddDays(4));
        dbContext.Candidates.Add(NewCandidate(session));
        await dbContext.SaveChangesAsync();

        var sender = new FakeEmailSender();
        var result = await RunRulesAsync(dbContext, sender, team, MessageTrigger.CandidateRegistered);

        Assert.Equal(1, result.Waiting);
        Assert.Equal(0, result.Sent);
        Assert.Empty(sender.SentMessages);
        Assert.Empty(dbContext.MessageRuleRuns);
    }

    /// <summary>A disabled rule does nothing at all — and leaves no marker, so enabling it later still works.</summary>
    [Fact]
    public async Task ADisabledRule_DoesNothing_AndLeavesNoMarker()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        var rule = dbContext.MessageRules.Single(r => r.Trigger == MessageTrigger.CandidateRegistered);
        rule.IsEnabled = false;
        var session = await SeedSessionAsync(dbContext, team, Now.AddDays(4));
        dbContext.Candidates.Add(NewCandidate(session));
        await dbContext.SaveChangesAsync();

        var sender = new FakeEmailSender();
        Assert.Equal(0, (await RunRulesAsync(dbContext, sender, team, MessageTrigger.CandidateRegistered)).Sent);
        Assert.Empty(dbContext.MessageRuleRuns);

        rule.IsEnabled = true;
        await dbContext.SaveChangesAsync();
        Assert.Equal(1, (await RunRulesAsync(dbContext, sender, team, MessageTrigger.CandidateRegistered)).Sent);
    }

    /// <summary>
    /// Two rules on one trigger, which is the case a per-trigger marker would break: sending the first
    /// must not mark the second done. This is why <c>MessageRuleRun</c> is keyed by rule.
    /// </summary>
    [Fact]
    public async Task TwoRulesOnOneTrigger_BothFire_AndNeitherMarksTheOtherDone()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedEmailSettingsAndTemplatesAsync(dbContext, team);
        // A second reminder at 48 hours, alongside the seeded 24-hour one.
        await SeedRuleAsync(dbContext, team, MessageTrigger.BeforeSessionStart, "DayBeforeReminder", 48);
        var session = await SeedSessionAsync(dbContext, team, Now.AddHours(20));
        dbContext.Candidates.Add(NewCandidate(session));
        await dbContext.SaveChangesAsync();

        var sender = new FakeEmailSender();
        var result = await RunRulesAsync(dbContext, sender, team, MessageTrigger.BeforeSessionStart);

        Assert.Equal(2, result.Sent);
        Assert.Equal(2, dbContext.MessageRuleRuns.Count());
    }
}
