using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Email;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Messaging;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// <c>FccFeeOutstandingScanner</c>'s historical-import exclusion (#88) — moved from a 30-day
/// <c>PaymentEligibilityWindow</c> date guess to <c>Session.ImportedHistoricallyUtc</c>. A candidate
/// on an imported session is realistically already terminal (<c>MarkHistoricalCandidatesGranted</c>
/// auto-grants them), so this is defense in depth rather than the load-bearing guard
/// <see cref="PaymentGenerationServiceTests"/>'s equivalent tests are — but the point of #88 is the
/// same either way: a real session that is simply old must not be excluded just for being old.
/// </summary>
public class FccFeeOutstandingHistoricalImportTests
{
    private static readonly DateTime Now = new(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc);

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
        var team = new Team { Name = "TESTTEAM", CreatedUtc = Now, SmtpHost = "smtp.example.org", SmtpUsername = "u", SmtpPassword = "p" };
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();

        dbContext.EmailSettings.Add(new EmailSettings
        {
            TeamId = team.Id, FromAddress = "noreply@example.org", ReplyToAddress = "reply@example.org",
            PrivacyPolicyUrl = "https://example.org/privacy", AdminNotificationEmail = "admin@example.org"
        });

        var rule = MessageRuleTestHarness.NewRule(
            team, MessageTrigger.FccFeeOutstanding, "Hi {{CandidateName}}, FRN {{Frn}}", 120, Now.AddYears(-1));
        rule.Subject = "The FCC is waiting for its fee";
        dbContext.MessageRules.Add(rule);
        await dbContext.SaveChangesAsync();
        return team;
    }

    private static async Task<Candidate> SeedPendingCandidateAsync(AppDbContext dbContext, Team team, DateTime? importedHistoricallyUtc)
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
            ExamToolsSessionId = "session-1", Title = "August Session", ScheduledStartUtc = Now.AddDays(-200),
            DurationMinutes = 60, Vec = vec, TeamId = team.Id, FeeConfiguration = feeConfiguration,
            Status = SessionStatus.Active, ImportedHistoricallyUtc = importedHistoricallyUtc, CreatedUtc = Now
        };
        dbContext.Sessions.Add(session);
        await dbContext.SaveChangesAsync();

        // ApplicationDateEnteredUtc 200 hours ago clears the rule's 120-hour threshold either way —
        // the thing under test is the session-age exclusion, not the trigger's own timing.
        var candidate = new Candidate
        {
            ExamToolsApplicantId = "applicant-1", SessionId = session.Id, Name = "Roana Glory",
            Email = "roana@example.com", DateRegisteredUtc = Now,
            ApplicationStatus = CandidateApplicationStatus.Received,
            ApplicationDateEnteredUtc = Now.AddHours(-200),
            Frn = "0012345678", FccPaymentStatus = FccApplicationPaymentStatus.PendingVerification
        };
        dbContext.Candidates.Add(candidate);
        await dbContext.SaveChangesAsync();
        return candidate;
    }

    private static Task<MessageRuleResult> RunAsync(AppDbContext dbContext, Team team, FakeEmailSender sender) =>
        MessageRuleTestHarness.Create(dbContext, sender, new FixedTimeProvider(Now))
            .RunAsync(team, [MessageTrigger.FccFeeOutstanding], null, CancellationToken.None);

    [Fact]
    public async Task CandidateOnAnImportedSession_GetsNoReminder()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedPendingCandidateAsync(dbContext, team, importedHistoricallyUtc: Now.AddDays(-1));
        var sender = new FakeEmailSender();

        await RunAsync(dbContext, team, sender);

        Assert.Empty(sender.SentMessages);
    }

    /// <summary>The correction #88 makes: age alone must not exclude a real, non-imported session.</summary>
    [Fact]
    public async Task ARealSessionThatIsSimplyOld_StillGetsAReminder()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedPendingCandidateAsync(dbContext, team, importedHistoricallyUtc: null);
        var sender = new FakeEmailSender();

        await RunAsync(dbContext, team, sender);

        Assert.Single(sender.SentMessages);
    }
}
