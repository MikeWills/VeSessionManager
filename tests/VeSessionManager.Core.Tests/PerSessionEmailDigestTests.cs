using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Email;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Messaging;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// <see cref="MessageFanOut.PerSession"/> extended to email (#491), so a VE-facing rule can say "there
/// is a session on this date, N candidates registered" as one message rather than one email per
/// registration. Was Discord-only; the recipient concept email needs and Discord doesn't (who does a
/// batched message go To?) is the whole reason this is new work rather than "already there," per Mike
/// asking whether the app already covered it.
/// </summary>
public class PerSessionEmailDigestTests
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

    private static async Task<int> SeedUserAsync(AppDbContext dbContext)
    {
        var user = new User { Name = "Admin", Email = $"a-{Guid.NewGuid():N}@example.org", Role = UserRole.TeamAdmin };
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
        return user.Id;
    }

    private static async Task<MessageRule> SeedRuleAsync(
        AppDbContext dbContext, Team team, MessageTrigger trigger, MessageRecipient recipient, bool includeCalendarInvite = false, int? parameterHours = null)
    {
        var rule = MessageRuleTestHarness.NewRule(team, trigger, "<p>{{SessionTitle}} on {{SessionDate}} — {{RegisteredCount}} registered</p>", parameterHours, Now.AddYears(-1));
        rule.Subject = "Session summary";
        rule.Recipient = recipient;
        rule.FanOut = MessageFanOut.PerSession;
        rule.IncludeCalendarInvite = includeCalendarInvite;
        dbContext.MessageRules.Add(rule);
        await dbContext.SaveChangesAsync();
        return rule;
    }

    private static async Task<Session> SeedSessionAsync(AppDbContext dbContext, Team team, string leadCallSign, DateTime scheduledStartUtc, string title = "August VE Session")
    {
        var vec = new Vec { Name = "ARRL" };
        var user = new User { Name = "Sys", Email = $"s-{Guid.NewGuid():N}@localhost", Role = UserRole.SystemAdmin };
        var session = new Session
        {
            ExamToolsSessionId = $"s-{Guid.NewGuid():N}",
            Title = title,
            ScheduledStartUtc = scheduledStartUtc,
            DurationMinutes = 90,
            ZoomJoinUrl = "https://zoom.example/join/123",
            Vec = vec,
            TeamId = team.Id,
            TeamLeadCallSign = leadCallSign,
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
        return session;
    }

    private static async Task SeedCandidatesAsync(AppDbContext dbContext, Session session, params string[] names)
    {
        foreach (var name in names)
        {
            dbContext.Candidates.Add(new Candidate
            {
                ExamToolsApplicantId = $"a-{Guid.NewGuid():N}", SessionId = session.Id, Name = name, FirstName = name,
                Email = $"{name.ToLowerInvariant()}@example.com", DateRegisteredUtc = Now.AddDays(-1)
            });
        }
        await dbContext.SaveChangesAsync();
    }

    private static Task<MessageRuleResult> RunAsync(AppDbContext dbContext, Team team, FakeEmailSender sender, MessageTrigger trigger) =>
        MessageRuleTestHarness.Create(dbContext, sender, new FixedTimeProvider(Now))
            .RunAsync(team, [trigger], null, CancellationToken.None);

    // ---- Validation ----

    [Fact]
    public async Task PerSession_AddressedToTheSessionLead_IsAllowedOnEmail()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var userId = await SeedUserAsync(dbContext);

        var result = await new MessageRuleAdminService(dbContext, new FixedTimeProvider(Now)).CreateAsync(
            team.Id, MessageTrigger.CandidateRegistered, "VE summary", "Subject", "Body", null,
            MessageRecipient.SessionLead, userId, CancellationToken.None,
            MessageChannel.Email, null, MessageFanOut.PerSession);

        Assert.Equal(MessageRuleActionResult.Success, result);
    }

    /// <summary>A per-session summary is about several candidates at once — there is no single candidate address it could go to, the same "one message listing everybody else" problem SingleDigest already refuses on email.</summary>
    [Fact]
    public async Task PerSession_AddressedToCandidate_IsRefused()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var userId = await SeedUserAsync(dbContext);

        var result = await new MessageRuleAdminService(dbContext, new FixedTimeProvider(Now)).CreateAsync(
            team.Id, MessageTrigger.CandidateRegistered, "VE summary", "Subject", "Body", null,
            MessageRecipient.Candidate, userId, CancellationToken.None,
            MessageChannel.Email, null, MessageFanOut.PerSession);

        Assert.Equal(MessageRuleActionResult.PerSessionDigestCannotAddressCandidate, result);
    }

    /// <summary>Regression: SingleDigest — a batch spanning every session, not just one — stays Discord-only.</summary>
    [Fact]
    public async Task SingleDigest_OnEmail_IsStillRefused()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var userId = await SeedUserAsync(dbContext);

        var result = await new MessageRuleAdminService(dbContext, new FixedTimeProvider(Now)).CreateAsync(
            team.Id, MessageTrigger.CandidateRegistered, "Digest", "Subject", "Body", null,
            MessageRecipient.SessionLead, userId, CancellationToken.None,
            MessageChannel.Email, null, MessageFanOut.SingleDigest);

        Assert.Equal(MessageRuleActionResult.DigestNeedsAChannel, result);
    }

    // ---- Dispatch ----

    [Fact]
    public async Task TwoCandidatesOnOneSession_ProduceExactlyOneEmail_ToTheSessionLead()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        dbContext.VolunteerExaminers.Add(new VolunteerExaminer { CallSign = "N0LEAD", Name = "Lead VE", Email = "lead@example.org" });
        await dbContext.SaveChangesAsync();
        await SeedRuleAsync(dbContext, team, MessageTrigger.CandidateRegistered, MessageRecipient.SessionLead, includeCalendarInvite: true);
        var session = await SeedSessionAsync(dbContext, team, "N0LEAD", Now.AddDays(3));
        await SeedCandidatesAsync(dbContext, session, "Roana", "Sam");

        var sender = new FakeEmailSender();
        await RunAsync(dbContext, team, sender, MessageTrigger.CandidateRegistered);

        var message = Assert.Single(sender.SentMessages);
        Assert.Equal("lead@example.org", message.ToAddress);
        Assert.Contains("August VE Session on", message.HtmlBody);
        Assert.Contains("2 registered", message.HtmlBody);
        Assert.NotNull(message.IcsAttachment);
    }

    [Fact]
    public async Task CandidatesOnTwoDifferentSessions_ProduceTwoSeparateEmails()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        dbContext.VolunteerExaminers.Add(new VolunteerExaminer { CallSign = "N0LEAD", Name = "Lead VE", Email = "lead@example.org" });
        await dbContext.SaveChangesAsync();
        await SeedRuleAsync(dbContext, team, MessageTrigger.CandidateRegistered, MessageRecipient.SessionLead);
        var sessionA = await SeedSessionAsync(dbContext, team, "N0LEAD", Now.AddDays(3), "Session A");
        var sessionB = await SeedSessionAsync(dbContext, team, "N0LEAD", Now.AddDays(5), "Session B");
        await SeedCandidatesAsync(dbContext, sessionA, "Roana");
        await SeedCandidatesAsync(dbContext, sessionB, "Sam");

        var sender = new FakeEmailSender();
        await RunAsync(dbContext, team, sender, MessageTrigger.CandidateRegistered);

        Assert.Equal(2, sender.SentMessages.Count);
        Assert.Contains(sender.SentMessages, m => m.HtmlBody.Contains("Session A"));
        Assert.Contains(sender.SentMessages, m => m.HtmlBody.Contains("Session B"));
    }

    /// <summary>Idempotency markers still land per candidate, not per email — otherwise the next tick would resend to a VE who already got the summary.</summary>
    [Fact]
    public async Task BothCandidatesGetAMarker_EvenThoughOnlyOneEmailWasSent()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        dbContext.VolunteerExaminers.Add(new VolunteerExaminer { CallSign = "N0LEAD", Name = "Lead VE", Email = "lead@example.org" });
        await dbContext.SaveChangesAsync();
        var rule = await SeedRuleAsync(dbContext, team, MessageTrigger.CandidateRegistered, MessageRecipient.SessionLead);
        var session = await SeedSessionAsync(dbContext, team, "N0LEAD", Now.AddDays(3));
        await SeedCandidatesAsync(dbContext, session, "Roana", "Sam");

        var sender = new FakeEmailSender();
        var result = await RunAsync(dbContext, team, sender, MessageTrigger.CandidateRegistered);

        Assert.Equal(2, result.Sent);
        Assert.Equal(2, await dbContext.MessageRuleRuns.CountAsync(r => r.MessageRuleId == rule.Id));

        // And a second tick sends nothing further — both candidates already have terminal runs.
        var secondSender = new FakeEmailSender();
        await RunAsync(dbContext, team, secondSender, MessageTrigger.CandidateRegistered);
        Assert.Empty(secondSender.SentMessages);
    }

    /// <summary>No VE on file for the session's lead — nobody to send the summary to, and nothing throws.</summary>
    [Fact]
    public async Task NoResolvableSessionLead_RecordsNoRecipient_SendsNothing()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedRuleAsync(dbContext, team, MessageTrigger.CandidateRegistered, MessageRecipient.SessionLead);
        var session = await SeedSessionAsync(dbContext, team, "N0NOBODY", Now.AddDays(3));
        await SeedCandidatesAsync(dbContext, session, "Roana");

        var sender = new FakeEmailSender();
        var result = await RunAsync(dbContext, team, sender, MessageTrigger.CandidateRegistered);

        Assert.Empty(sender.SentMessages);
        Assert.Equal(1, result.NoRecipient);
    }
}
