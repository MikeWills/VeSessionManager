using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Email;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Messaging;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// <c>MessageTrigger.PaymentUnpaidBeforeSession</c> (2026-08-25): "the exam fee is still unpaid, and
/// the session starts in N hours."
///
/// <para>Mike: a candidate who has not paid cannot test, and every existing money trigger anchored on
/// something <i>after</i> the session — the FCC application, the FCC fee. This is the trigger that
/// warns while there is still time to do something about it.</para>
/// </summary>
public class PaymentUnpaidBeforeSessionScannerTests
{
    private static readonly DateTime Now = new(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc);

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

    private static async Task<Team> SeedTeamAsync(AppDbContext dbContext)
    {
        var team = new Team { Name = "TESTTEAM", CreatedUtc = Now.AddYears(-1), SmtpHost = "smtp.example.org", SmtpUsername = "smtp-user", SmtpPassword = "smtp-pass" };
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
        await dbContext.SaveChangesAsync();
        return team;
    }

    private static async Task<(Session Session, Candidate Candidate, Payment Payment)> SeedSessionWithPaymentAsync(
        AppDbContext dbContext, Team team, DateTime scheduledStartUtc,
        PaymentStatus paymentStatus = PaymentStatus.Unpaid,
        SessionStatus sessionStatus = SessionStatus.Active,
        PaymentReason reason = PaymentReason.InitialExam,
        CandidateApplicationStatus candidateStatus = CandidateApplicationStatus.Received)
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
            ExamToolsSessionId = $"session-{Guid.NewGuid():N}", Title = "August Session",
            ScheduledStartUtc = scheduledStartUtc, DurationMinutes = 60, Vec = vec, TeamId = team.Id,
            FeeConfiguration = feeConfiguration, Status = sessionStatus
        };
        dbContext.Sessions.Add(session);
        await dbContext.SaveChangesAsync();

        var candidate = new Candidate
        {
            ExamToolsApplicantId = $"applicant-{Guid.NewGuid():N}", SessionId = session.Id, Name = "Roana Glory",
            Email = "roana@example.com", DateRegisteredUtc = Now.AddDays(-3), ApplicationStatus = candidateStatus
        };
        dbContext.Candidates.Add(candidate);
        await dbContext.SaveChangesAsync();

        var payment = new Payment
        {
            CandidateId = candidate.Id, Reason = reason, Amount = 15m, Status = paymentStatus,
            PaymentLinkUrl = "https://square.link/u/abc", CreatedUtc = Now.AddDays(-3)
        };
        dbContext.Payments.Add(payment);
        await dbContext.SaveChangesAsync();

        return (session, candidate, payment);
    }

    private static async Task<MessageRule> SeedRuleAsync(AppDbContext dbContext, Team team, int? parameterHours, DateTime? createdUtc = null)
    {
        var rule = new MessageRule
        {
            TeamId = team.Id,
            Name = "Unpaid exam fee, before the session",
            Trigger = MessageTrigger.PaymentUnpaidBeforeSession,
            ParameterHours = parameterHours,
            Subject = "Your exam fee is still unpaid",
            Body = "Hi {{CandidateName}}, your session on {{SessionDate}} is coming up and {{PaymentAmount}} is still owed. {{PaymentLinkUrl}}",
            Recipient = MessageRecipient.Candidate,
            CreatedUtc = createdUtc ?? Now.AddYears(-1)
        };
        dbContext.MessageRules.Add(rule);
        await dbContext.SaveChangesAsync();
        return rule;
    }

    private static async Task<MessageRuleResult> RunAsync(AppDbContext dbContext, IEmailSender sender, Team team) =>
        await MessageRuleTestHarness.Create(dbContext, sender, new FixedTimeProvider(Now))
            .RunAsync(team, [MessageTrigger.PaymentUnpaidBeforeSession], null, CancellationToken.None);

    [Fact]
    public async Task AnUnpaidFee_WithTheSessionInsideTheWindow_Fires()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedRuleAsync(dbContext, team, parameterHours: 24);
        await SeedSessionWithPaymentAsync(dbContext, team, Now.AddHours(12));
        var sender = new FakeEmailSender();

        var result = await RunAsync(dbContext, sender, team);

        Assert.Equal(1, result.Sent);
        var message = Assert.Single(sender.SentMessages);
        Assert.Equal("roana@example.com", message.ToAddress);
        Assert.Contains("$15.00", message.HtmlBody);
    }

    [Fact]
    public async Task APaidFee_DoesNotFire()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedRuleAsync(dbContext, team, parameterHours: 24);
        await SeedSessionWithPaymentAsync(dbContext, team, Now.AddHours(12), paymentStatus: PaymentStatus.Paid);
        var sender = new FakeEmailSender();

        var result = await RunAsync(dbContext, sender, team);

        Assert.Equal(0, result.Sent);
        Assert.Empty(sender.SentMessages);
    }

    [Fact]
    public async Task ASessionOutsideTheWindow_DoesNotFireYet()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedRuleAsync(dbContext, team, parameterHours: 24);
        await SeedSessionWithPaymentAsync(dbContext, team, Now.AddDays(3));
        var sender = new FakeEmailSender();

        var result = await RunAsync(dbContext, sender, team);

        Assert.Equal(0, result.Sent);
    }

    /// <summary>The upper edge is exclusive of "already started" — a reminder, not a notice about something under way.</summary>
    [Fact]
    public async Task ASessionThatHasAlreadyStarted_DoesNotFire()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedRuleAsync(dbContext, team, parameterHours: 24);
        await SeedSessionWithPaymentAsync(dbContext, team, Now.AddHours(-1));
        var sender = new FakeEmailSender();

        var result = await RunAsync(dbContext, sender, team);

        Assert.Equal(0, result.Sent);
    }

    [Fact]
    public async Task ACancelledSession_DoesNotFire()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedRuleAsync(dbContext, team, parameterHours: 24);
        await SeedSessionWithPaymentAsync(dbContext, team, Now.AddHours(12), sessionStatus: SessionStatus.Cancelled);
        var sender = new FakeEmailSender();

        var result = await RunAsync(dbContext, sender, team);

        Assert.Equal(0, result.Sent);
    }

    /// <summary>Sent once; a second pass over the same subject must not resend.</summary>
    [Fact]
    public async Task AlreadySent_DoesNotResend()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedRuleAsync(dbContext, team, parameterHours: 24);
        await SeedSessionWithPaymentAsync(dbContext, team, Now.AddHours(12));
        var sender = new FakeEmailSender();

        var first = await RunAsync(dbContext, sender, team);
        var second = await RunAsync(dbContext, sender, team);

        Assert.Equal(1, first.Sent);
        Assert.Equal(0, second.Sent);
        Assert.Single(sender.SentMessages);
    }

    /// <summary>
    /// ⚠️ No retest branch, unlike PaymentUnpaidScanner (which needed one to anchor on the FCC
    /// application/result date). This scanner anchors on the session itself, so a retest payment on
    /// an upcoming session fires the same as any other unpaid one — no special-casing required.
    /// </summary>
    [Fact]
    public async Task ARetestFee_FiresTheSameAsAnyOther()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedRuleAsync(dbContext, team, parameterHours: 24);
        await SeedSessionWithPaymentAsync(dbContext, team, Now.AddHours(12),
            reason: PaymentReason.Retest, candidateStatus: CandidateApplicationStatus.Failed);
        var sender = new FakeEmailSender();

        var result = await RunAsync(dbContext, sender, team);

        Assert.Equal(1, result.Sent);
    }

    /// <summary>Same guarantee as BeforeSessionStartScanner: a rule added today never fires for someone already inside its own window when it was created.</summary>
    [Fact]
    public async Task ARuleCreatedInsideItsOwnWindow_DoesNotFireForASessionAlreadyThere()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        // The session is 6 hours out; a 24-hour rule created right now is already "too late" for it.
        await SeedSessionWithPaymentAsync(dbContext, team, Now.AddHours(6));
        await SeedRuleAsync(dbContext, team, parameterHours: 24, createdUtc: Now);
        var sender = new FakeEmailSender();

        var result = await RunAsync(dbContext, sender, team);

        Assert.Equal(0, result.Sent);
    }

    [Fact]
    public async Task NoRule_MeansNothingFires()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedSessionWithPaymentAsync(dbContext, team, Now.AddHours(12));
        var sender = new FakeEmailSender();

        var result = await RunAsync(dbContext, sender, team);

        Assert.Equal(0, result.Sent);
    }
}
