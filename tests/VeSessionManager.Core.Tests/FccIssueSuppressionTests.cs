using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Email;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Messaging;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// The FCC-wide-issue suppression gate (2026-08-26) — a manual escape hatch for a real FCC/VEC
/// processing stall, where <c>FccFeeOutstandingScanner</c> otherwise has no way to tell "the FCC is
/// backlogged" from "this one candidate hasn't paid". See <c>SystemSettings</c>'s own remarks and
/// <c>MessageDispatchService.SuppressByFccIssueAsync</c>.
///
/// <para><b>The one property every test here is really pinning:</b> suppression is a terminal
/// <see cref="MessageRuleOutcome.Suppressed"/> marker, not a silent skip — so flipping the master
/// switch back off never sends a backlog. <see cref="TurningTheSwitchBackOff_NeverSendsWhatWasSuppressed"/>
/// is the test that would fail if that regressed back to a silent exclude.</para>
/// </summary>
public class FccIssueSuppressionTests
{
    private static readonly DateTime Now = new(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);

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

    private static AppDbContext CreateContext() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<Team> SeedTeamAsync(AppDbContext dbContext)
    {
        var team = new Team
        {
            Name = "TESTTEAM", SmtpHost = "smtp.example.org", SmtpUsername = "smtp-user",
            SmtpPassword = "smtp-pass", CreatedUtc = Now
        };
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();

        dbContext.EmailSettings.Add(new EmailSettings
        {
            TeamId = team.Id, FromAddress = "noreply@example.org", ReplyToAddress = "reply@example.org",
            PrivacyPolicyUrl = "https://example.org/privacy", AdminNotificationEmail = "admin@example.org"
        });

        var rule = MessageRuleTestHarness.NewRule(
            team, MessageTrigger.FccFeeOutstanding,
            "Hi {{CandidateName}}, session {{SessionDate}}, FRN {{Frn}}", 120, Now.AddYears(-1));
        rule.Subject = "The FCC is waiting for its fee";
        dbContext.MessageRules.Add(rule);

        await dbContext.SaveChangesAsync();
        return team;
    }

    /// <summary>InitialLicenseClass null (the default) is a first-time applicant; a non-null/non-None value is an upgrade.</summary>
    private static async Task<Candidate> SeedPendingCandidateAsync(AppDbContext dbContext, Team team, LicenseClass? initialLicenseClass)
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
            ExamToolsSessionId = "session-1", Title = "August Session", ScheduledStartUtc = Now.AddDays(-3),
            DurationMinutes = 60, Vec = vec, TeamId = team.Id, FeeConfiguration = feeConfiguration,
            Status = SessionStatus.Active, ZoomJoinUrl = "https://zoom.us/j/123", CreatedUtc = Now
        };
        dbContext.Sessions.Add(session);
        await dbContext.SaveChangesAsync();

        var candidate = new Candidate
        {
            ExamToolsApplicantId = "applicant-1", SessionId = session.Id, Name = "Roana Glory",
            Email = "roana@example.com", DateRegisteredUtc = Now,
            ApplicationStatus = CandidateApplicationStatus.Received,
            ApplicationDateEnteredUtc = Now.AddHours(-200),
            Frn = "0012345678", FccPaymentStatus = FccApplicationPaymentStatus.PendingVerification,
            InitialLicenseClass = initialLicenseClass
        };
        dbContext.Candidates.Add(candidate);
        await dbContext.SaveChangesAsync();
        return candidate;
    }

    private static async Task SetFccIssueAsync(
        AppDbContext dbContext, bool active, bool suppressNewLicense = false, bool suppressUpgrade = false)
    {
        var settings = new SystemSettingsService(dbContext, new FixedTimeProvider(Now));
        await settings.UpdateFccIssueAsync(active, suppressNewLicense, suppressUpgrade, false, userId: 1, CancellationToken.None);
    }

    [Fact]
    public async Task MasterSwitchOff_SendsNormallyRegardlessOfSubSwitches()
    {
        using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedPendingCandidateAsync(dbContext, team, initialLicenseClass: null);
        // Sub-switches on, but the master is off — should change nothing.
        await SetFccIssueAsync(dbContext, active: false, suppressNewLicense: true, suppressUpgrade: true);

        var emailSender = new FakeEmailSender();
        var rules = MessageRuleTestHarness.Create(dbContext, emailSender, new FixedTimeProvider(Now));
        var result = await rules.RunAsync(team, [MessageTrigger.FccFeeOutstanding], null, CancellationToken.None);

        Assert.Equal(1, result.Sent);
        Assert.Equal(0, result.Suppressed);
    }

    [Fact]
    public async Task NewLicenseCandidate_SuppressedWhenThatPopulationIsSwitchedOff()
    {
        using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedPendingCandidateAsync(dbContext, team, initialLicenseClass: null);
        await SetFccIssueAsync(dbContext, active: true, suppressNewLicense: true);

        var emailSender = new FakeEmailSender();
        var rules = MessageRuleTestHarness.Create(dbContext, emailSender, new FixedTimeProvider(Now));
        var result = await rules.RunAsync(team, [MessageTrigger.FccFeeOutstanding], null, CancellationToken.None);

        Assert.Equal(0, result.Sent);
        Assert.Equal(1, result.Suppressed);
        Assert.Empty(emailSender.SentMessages);
    }

    [Fact]
    public async Task UpgradeCandidate_NotAffectedByTheNewLicenseSwitchAlone()
    {
        using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedPendingCandidateAsync(dbContext, team, initialLicenseClass: LicenseClass.General);
        // Only the new-license population is suppressed — this candidate is an upgrade.
        await SetFccIssueAsync(dbContext, active: true, suppressNewLicense: true, suppressUpgrade: false);

        var emailSender = new FakeEmailSender();
        var rules = MessageRuleTestHarness.Create(dbContext, emailSender, new FixedTimeProvider(Now));
        var result = await rules.RunAsync(team, [MessageTrigger.FccFeeOutstanding], null, CancellationToken.None);

        Assert.Equal(1, result.Sent);
        Assert.Equal(0, result.Suppressed);
    }

    [Fact]
    public async Task UpgradeCandidate_SuppressedWhenTheUpgradeSwitchIsOn()
    {
        using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedPendingCandidateAsync(dbContext, team, initialLicenseClass: LicenseClass.General);
        await SetFccIssueAsync(dbContext, active: true, suppressNewLicense: false, suppressUpgrade: true);

        var emailSender = new FakeEmailSender();
        var rules = MessageRuleTestHarness.Create(dbContext, emailSender, new FixedTimeProvider(Now));
        var result = await rules.RunAsync(team, [MessageTrigger.FccFeeOutstanding], null, CancellationToken.None);

        Assert.Equal(0, result.Sent);
        Assert.Equal(1, result.Suppressed);
    }

    /// <summary>
    /// The whole point of marking a suppressed subject terminal rather than silently excluding it:
    /// once the master switch goes back off (the FCC issue is resolved), a candidate suppressed
    /// during the outage must not suddenly send — that would be exactly the backlog-on-re-enable
    /// failure MessageRuleEligibility.FloorUtc already exists to prevent for a different kind of "off".
    /// </summary>
    [Fact]
    public async Task TurningTheSwitchBackOff_NeverSendsWhatWasSuppressed()
    {
        using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedPendingCandidateAsync(dbContext, team, initialLicenseClass: null);
        await SetFccIssueAsync(dbContext, active: true, suppressNewLicense: true);

        var emailSender = new FakeEmailSender();
        var rules = MessageRuleTestHarness.Create(dbContext, emailSender, new FixedTimeProvider(Now));
        var first = await rules.RunAsync(team, [MessageTrigger.FccFeeOutstanding], null, CancellationToken.None);
        Assert.Equal(1, first.Suppressed);

        // The issue is resolved: switch the master back off.
        await SetFccIssueAsync(dbContext, active: false);

        var second = await rules.RunAsync(team, [MessageTrigger.FccFeeOutstanding], null, CancellationToken.None);

        Assert.Equal(0, second.Sent);
        Assert.Equal(0, second.Suppressed);
        Assert.Empty(emailSender.SentMessages);
    }
}
