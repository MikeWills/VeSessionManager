using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Email;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Messaging;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// The <c>{{PaymentStatus}}</c> placeholder on <c>BeforeSessionStartScanner</c> (#490) — lets a
/// day-before (or any time-relative) reminder tell a VE/session lead whether the candidate has paid,
/// without them having to cross-reference the roster separately. Reuses the exact "most recent
/// Unpaid, else most recent overall" rule the session roster's own Payment chip uses
/// (Detail.cshtml.cs), via the shared VeSessionManager.Core.Payments.PaymentStatusText.
/// </summary>
public class BeforeSessionStartScannerTests
{
    private static readonly DateTime Now = new(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc);

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

    private static async Task<Candidate> SeedCandidateAsync(
        AppDbContext dbContext, Team team, DateTime scheduledStartUtc, IReadOnlyList<PaymentStatus>? payments = null)
    {
        var vec = new Vec { Name = "ARRL" };
        var user = new User { Name = "System", Email = "system@localhost", Role = UserRole.SystemAdmin };
        var session = new Session
        {
            ExamToolsSessionId = $"session-{Guid.NewGuid():N}", Title = "August Session",
            ScheduledStartUtc = scheduledStartUtc, DurationMinutes = 60, Vec = vec, TeamId = team.Id,
            FeeConfiguration = new FeeConfiguration
            {
                Vec = vec, EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                FeeCollectionEnabled = true, ExamFeeAmount = 15m, CreatedByUser = user, CreatedUtc = Now
            },
            Status = SessionStatus.Active
        };
        dbContext.Sessions.Add(session);
        await dbContext.SaveChangesAsync();

        var candidate = new Candidate
        {
            ExamToolsApplicantId = $"applicant-{Guid.NewGuid():N}", SessionId = session.Id, Name = "Roana Glory",
            Email = "roana@example.com", DateRegisteredUtc = Now.AddDays(-3)
        };
        dbContext.Candidates.Add(candidate);
        await dbContext.SaveChangesAsync();

        var createdUtc = Now.AddDays(-3);
        foreach (var status in payments ?? [])
        {
            dbContext.Payments.Add(new Payment
            {
                CandidateId = candidate.Id, Reason = PaymentReason.InitialExam, Amount = 15m,
                Status = status, CreatedUtc = createdUtc
            });
            // Each subsequent payment is "more recent" than the last, so ordering is unambiguous.
            createdUtc = createdUtc.AddMinutes(1);
        }
        await dbContext.SaveChangesAsync();

        return candidate;
    }

    private static async Task<MessageRule> SeedRuleAsync(AppDbContext dbContext, Team team, int parameterHours = 24)
    {
        var rule = MessageRuleTestHarness.NewRule(
            team, MessageTrigger.BeforeSessionStart,
            "Hi {{CandidateName}}, payment status: {{PaymentStatus}}",
            parameterHours, Now.AddYears(-1));
        dbContext.MessageRules.Add(rule);
        await dbContext.SaveChangesAsync();
        return rule;
    }

    private static async Task<MessageRuleResult> RunAsync(AppDbContext dbContext, IEmailSender sender, Team team) =>
        await MessageRuleTestHarness.Create(dbContext, sender, new FixedTimeProvider(Now))
            .RunAsync(team, [MessageTrigger.BeforeSessionStart], null, CancellationToken.None);

    [Fact]
    public async Task UnpaidCandidate_RendersUnpaid()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedRuleAsync(dbContext, team);
        await SeedCandidateAsync(dbContext, team, Now.AddHours(12), [PaymentStatus.Unpaid]);
        var sender = new FakeEmailSender();

        await RunAsync(dbContext, sender, team);

        Assert.Contains("payment status: Unpaid", Assert.Single(sender.SentMessages).HtmlBody);
    }

    [Fact]
    public async Task PaidCandidate_RendersPaid()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedRuleAsync(dbContext, team);
        await SeedCandidateAsync(dbContext, team, Now.AddHours(12), [PaymentStatus.Paid]);
        var sender = new FakeEmailSender();

        await RunAsync(dbContext, sender, team);

        Assert.Contains("payment status: Paid", Assert.Single(sender.SentMessages).HtmlBody);
    }

    /// <summary>An outstanding fee takes priority over an older paid row — same rule the roster's chip uses.</summary>
    [Fact]
    public async Task UnpaidRetestAfterAnEarlierPaidFee_StillRendersUnpaid()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedRuleAsync(dbContext, team);
        await SeedCandidateAsync(dbContext, team, Now.AddHours(12), [PaymentStatus.Paid, PaymentStatus.Unpaid]);
        var sender = new FakeEmailSender();

        await RunAsync(dbContext, sender, team);

        Assert.Contains("payment status: Unpaid", Assert.Single(sender.SentMessages).HtmlBody);
    }

    [Fact]
    public async Task NoFeeCollected_RendersNotApplicable()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedRuleAsync(dbContext, team);
        await SeedCandidateAsync(dbContext, team, Now.AddHours(12), [PaymentStatus.NotApplicable]);
        var sender = new FakeEmailSender();

        await RunAsync(dbContext, sender, team);

        Assert.Contains("payment status: Not applicable", Assert.Single(sender.SentMessages).HtmlBody);
    }

    [Fact]
    public async Task NoPaymentRowAtAll_RendersNoPayment()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedRuleAsync(dbContext, team);
        await SeedCandidateAsync(dbContext, team, Now.AddHours(12));
        var sender = new FakeEmailSender();

        await RunAsync(dbContext, sender, team);

        Assert.Contains("payment status: No payment", Assert.Single(sender.SentMessages).HtmlBody);
    }
}
