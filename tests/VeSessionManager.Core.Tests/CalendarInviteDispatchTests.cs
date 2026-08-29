using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Email;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Messaging;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// The send-path half of #491's calendar invite: a rule with <c>IncludeCalendarInvite</c> attaches an
/// <see cref="IcsInviteBuilder"/>-built .ics for the subject's session, built here in
/// <see cref="MessageDispatchService"/> rather than in a scanner — the scanner's job is deciding who's
/// due, not building an attachment every candidate on the run would need the same one of.
/// </summary>
public class CalendarInviteDispatchTests
{
    private static readonly DateTime Now = new(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc);

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
            TeamId = team.Id, FromAddress = "noreply@example.org", ReplyToAddress = "team@example.org",
            PrivacyPolicyUrl = "https://example.org/privacy", AdminNotificationEmail = "admin@example.org"
        });
        await dbContext.SaveChangesAsync();
        return team;
    }

    private static async Task<MessageRule> SeedRuleAsync(AppDbContext dbContext, Team team, MessageTrigger trigger, bool includeCalendarInvite, int? parameterHours = null)
    {
        var rule = MessageRuleTestHarness.NewRule(team, trigger, "<p>Hi {{CandidateName}}</p>", parameterHours, Now.AddYears(-1));
        rule.Subject = "Session reminder";
        rule.IncludeCalendarInvite = includeCalendarInvite;
        dbContext.MessageRules.Add(rule);
        await dbContext.SaveChangesAsync();
        return rule;
    }

    private static async Task<(Session Session, Candidate Candidate)> SeedSessionAndCandidateAsync(
        AppDbContext dbContext, Team team, DateTime scheduledStartUtc, string? zoomJoinUrl = "https://zoom.example/join/123")
    {
        var vec = new Vec { Name = "ARRL" };
        var user = new User { Name = "Sys", Email = $"s-{Guid.NewGuid():N}@localhost", Role = UserRole.SystemAdmin };
        var session = new Session
        {
            ExamToolsSessionId = $"s-{Guid.NewGuid():N}",
            Title = "August VE Session",
            ScheduledStartUtc = scheduledStartUtc,
            DurationMinutes = 90,
            ZoomJoinUrl = zoomJoinUrl,
            Vec = vec,
            TeamId = team.Id,
            FeeConfiguration = new FeeConfiguration
            {
                Vec = vec, EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                FeeCollectionEnabled = false, CreatedByUser = user, CreatedUtc = Now
            },
            Status = SessionStatus.Active,
            CreatedUtc = Now
        };
        dbContext.Sessions.Add(session);
        await dbContext.SaveChangesAsync();

        var candidate = new Candidate
        {
            ExamToolsApplicantId = $"a-{Guid.NewGuid():N}", SessionId = session.Id, Name = "Roana Glory",
            FirstName = "Roana", Email = "roana@example.com", DateRegisteredUtc = Now.AddDays(-3)
        };
        dbContext.Candidates.Add(candidate);
        await dbContext.SaveChangesAsync();
        return (session, candidate);
    }

    private static Task<MessageRuleResult> RunAsync(AppDbContext dbContext, Team team, FakeEmailSender sender, MessageTrigger trigger) =>
        MessageRuleTestHarness.Create(dbContext, sender, new FixedTimeProvider(Now))
            .RunAsync(team, [trigger], null, CancellationToken.None);

    [Fact]
    public async Task RegistrationConfirmation_WithCalendarInviteOn_AttachesAnIcsForTheSession()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedRuleAsync(dbContext, team, MessageTrigger.CandidateRegistered, includeCalendarInvite: true);
        var (session, _) = await SeedSessionAndCandidateAsync(dbContext, team, Now.AddDays(2));

        var sender = new FakeEmailSender();
        await RunAsync(dbContext, team, sender, MessageTrigger.CandidateRegistered);

        var ics = Assert.Single(sender.SentMessages).IcsAttachment;
        Assert.NotNull(ics);
        Assert.Equal("invite.ics", ics!.FileName);
        Assert.StartsWith("text/calendar", ics.ContentType);
        var text = System.Text.Encoding.UTF8.GetString(ics.Content);
        Assert.Contains("BEGIN:VEVENT", text);
        Assert.Contains("SUMMARY:August VE Session", text);
        Assert.Contains("LOCATION:https://zoom.example/join/123", text);
        Assert.Contains($"UID:session-{session.Id}@ve-ops", text);
    }

    [Fact]
    public async Task RegistrationConfirmation_WithCalendarInviteOff_AttachesNothing()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedRuleAsync(dbContext, team, MessageTrigger.CandidateRegistered, includeCalendarInvite: false);
        await SeedSessionAndCandidateAsync(dbContext, team, Now.AddDays(2));

        var sender = new FakeEmailSender();
        await RunAsync(dbContext, team, sender, MessageTrigger.CandidateRegistered);

        Assert.Null(Assert.Single(sender.SentMessages).IcsAttachment);
    }

    [Fact]
    public async Task DayBeforeReminder_WithCalendarInviteOn_AttachesAnIcsToo()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedRuleAsync(dbContext, team, MessageTrigger.BeforeSessionStart, includeCalendarInvite: true, parameterHours: 24);
        await SeedSessionAndCandidateAsync(dbContext, team, Now.AddHours(12));

        var sender = new FakeEmailSender();
        await RunAsync(dbContext, team, sender, MessageTrigger.BeforeSessionStart);

        Assert.NotNull(Assert.Single(sender.SentMessages).IcsAttachment);
    }

    /// <summary>Same session id sent through two different triggers (registration, then the reminder) — the calendar client should treat this as one event, not two.</summary>
    [Fact]
    public async Task TheSameSession_AlwaysProducesTheSameUid_AcrossDifferentTriggers()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var (session, _) = await SeedSessionAndCandidateAsync(dbContext, team, Now.AddHours(12));
        await SeedRuleAsync(dbContext, team, MessageTrigger.CandidateRegistered, includeCalendarInvite: true);
        await SeedRuleAsync(dbContext, team, MessageTrigger.BeforeSessionStart, includeCalendarInvite: true, parameterHours: 24);

        var registrationSender = new FakeEmailSender();
        await RunAsync(dbContext, team, registrationSender, MessageTrigger.CandidateRegistered);
        var reminderSender = new FakeEmailSender();
        await RunAsync(dbContext, team, reminderSender, MessageTrigger.BeforeSessionStart);

        var registrationText = System.Text.Encoding.UTF8.GetString(Assert.Single(registrationSender.SentMessages).IcsAttachment!.Content);
        var reminderText = System.Text.Encoding.UTF8.GetString(Assert.Single(reminderSender.SentMessages).IcsAttachment!.Content);
        var expectedUid = $"UID:session-{session.Id}@ve-ops";
        Assert.Contains(expectedUid, registrationText);
        Assert.Contains(expectedUid, reminderText);
    }

    /// <summary>No Zoom link yet — the .ics still builds, just with no LOCATION line, rather than failing the whole send.</summary>
    [Fact]
    public async Task NoZoomLinkYet_StillAttachesAnIcs_WithNoLocationLine()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedRuleAsync(dbContext, team, MessageTrigger.CandidateRegistered, includeCalendarInvite: true);
        await SeedSessionAndCandidateAsync(dbContext, team, Now.AddDays(2), zoomJoinUrl: null);

        var sender = new FakeEmailSender();
        await RunAsync(dbContext, team, sender, MessageTrigger.CandidateRegistered);

        var text = System.Text.Encoding.UTF8.GetString(Assert.Single(sender.SentMessages).IcsAttachment!.Content);
        Assert.Contains("BEGIN:VEVENT", text);
        Assert.DoesNotContain("LOCATION:", text);
    }
}
