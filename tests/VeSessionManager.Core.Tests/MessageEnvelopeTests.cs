using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Email;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Messaging;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Per-rule Reply-To, Cc and Bcc (#401 PR4).
///
/// <para>Three constraints are designed in rather than left to whoever fills the form: <b>there is no
/// From</b> (SPF/DKIM/DMARC on a domain this app does not control, and getting it wrong sends mail to
/// spam silently), <b>a Cc cannot go on candidate mail</b> (the person copied cannot unsubscribe, and
/// every candidate sees the address), and <b>copies go once per run</b> rather than once per
/// recipient.</para>
/// </summary>
public class MessageEnvelopeTests
{
    private static readonly DateTime Now = new(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc);

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
        var team = new Team
        {
            Name = "TESTTEAM",
            SmtpHost = "smtp.example.org",
            SmtpUsername = "u",
            SmtpPassword = "p",
            CreatedUtc = Now
        };
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();

        dbContext.EmailSettings.Add(new EmailSettings
        {
            TeamId = team.Id,
            FromAddress = "noreply@example.org",
            ReplyToAddress = "team@example.org",
            PrivacyPolicyUrl = "https://example.org/privacy",
            AdminNotificationEmail = "admin@example.org"
        });
        dbContext.EmailTemplates.Add(new EmailTemplate
        {
            TeamId = team.Id, Key = "Confirm", Subject = "Hi {{CandidateName}}", Body = "<p>Hello</p>"
        });
        await dbContext.SaveChangesAsync();
        return team;
    }

    private static async Task<MessageRule> SeedRuleAsync(
        AppDbContext dbContext, Team team, MessageReplyToSource replyToSource = MessageReplyToSource.EmailSettings,
        string? replyToOverride = null, string? bcc = null, bool oncePerRun = true)
    {
        var rule = MessageRuleTestHarness.NewRule(team, MessageTrigger.CandidateRegistered, "Confirm", null, Now.AddYears(-1));
        rule.ReplyToSource = replyToSource;
        rule.ReplyToOverride = replyToOverride;
        rule.BccAddress = bcc;
        rule.MonitoringCopyOncePerRun = oncePerRun;
        dbContext.MessageRules.Add(rule);
        await dbContext.SaveChangesAsync();
        return rule;
    }

    private static async Task<Session> SeedSessionAsync(AppDbContext dbContext, Team team, string? leadCallSign)
    {
        var vec = new Vec { Name = "ARRL" };
        var user = new User { Name = "Sys", Email = $"s-{Guid.NewGuid():N}@localhost", Role = UserRole.SystemAdmin };
        var session = new Session
        {
            ExamToolsSessionId = $"s-{Guid.NewGuid():N}",
            Title = "August",
            ScheduledStartUtc = Now.AddDays(3),
            DurationMinutes = 60,
            Vec = vec,
            TeamId = team.Id,
            TeamLeadCallSign = leadCallSign,
            FeeConfiguration = new FeeConfiguration
            {
                Vec = vec,
                EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                FeeCollectionEnabled = false,
                CreatedByUser = user,
                CreatedUtc = Now
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
                ExamToolsApplicantId = $"a-{name}",
                SessionId = session.Id,
                Name = name,
                FirstName = name,
                Email = $"{name.ToLowerInvariant()}@example.com",
                DateRegisteredUtc = Now
            });
        }

        await dbContext.SaveChangesAsync();
    }

    private static Task<MessageRuleResult> RunAsync(AppDbContext dbContext, Team team, FakeEmailSender sender) =>
        MessageRuleTestHarness.Create(dbContext, sender, new FixedTimeProvider(Now))
            .RunAsync(team, [MessageTrigger.CandidateRegistered], null, CancellationToken.None);

    // ---- Reply-To ----

    [Fact]
    public async Task ByDefault_RepliesGoToTheTeam()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedRuleAsync(dbContext, team);
        await SeedCandidatesAsync(dbContext, await SeedSessionAsync(dbContext, team, "N0LEAD"), "Roana");

        var sender = new FakeEmailSender();
        await RunAsync(dbContext, team, sender);

        Assert.Equal("team@example.org", Assert.Single(sender.SentMessages).ReplyToAddress);
    }

    [Fact]
    public async Task SessionLead_RepliesGoToThatVe()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        dbContext.VolunteerExaminers.Add(new VolunteerExaminer { CallSign = "N0LEAD", Name = "Lead VE", Email = "lead@example.org" });
        await dbContext.SaveChangesAsync();
        await SeedRuleAsync(dbContext, team, MessageReplyToSource.SessionLead);
        await SeedCandidatesAsync(dbContext, await SeedSessionAsync(dbContext, team, "n0lead"), "Roana");

        var sender = new FakeEmailSender();
        await RunAsync(dbContext, team, sender);

        // Matched through CallSign.Normalize, so the session's lower-case value still finds the VE.
        Assert.Equal("lead@example.org", Assert.Single(sender.SentMessages).ReplyToAddress);
    }

    /// <summary>
    /// The fallbacks, as one theory because they are one rule: a reply reaching the team is worse than
    /// one reaching the lead, and a reply reaching nobody is worse than both. <c>&lt;UNKNOWN&gt;</c> is
    /// ExamTools' own placeholder and once fused two people into a single VE record — it must not be
    /// looked up.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<UNKNOWN>")]
    [InlineData("N0NOBODY")]
    public async Task SessionLead_ThatCannotBeResolved_FallsBackToTheTeam(string? leadCallSign)
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedRuleAsync(dbContext, team, MessageReplyToSource.SessionLead);
        await SeedCandidatesAsync(dbContext, await SeedSessionAsync(dbContext, team, leadCallSign), "Roana");

        var sender = new FakeEmailSender();
        await RunAsync(dbContext, team, sender);

        Assert.Equal("team@example.org", Assert.Single(sender.SentMessages).ReplyToAddress);
    }

    /// <summary>A VE record with no email is the same non-answer as no VE record at all.</summary>
    [Fact]
    public async Task SessionLead_WithNoEmailOnFile_FallsBackToTheTeam()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        dbContext.VolunteerExaminers.Add(new VolunteerExaminer { CallSign = "N0LEAD", Name = "Lead VE", Email = null });
        await dbContext.SaveChangesAsync();
        await SeedRuleAsync(dbContext, team, MessageReplyToSource.SessionLead);
        await SeedCandidatesAsync(dbContext, await SeedSessionAsync(dbContext, team, "N0LEAD"), "Roana");

        var sender = new FakeEmailSender();
        await RunAsync(dbContext, team, sender);

        Assert.Equal("team@example.org", Assert.Single(sender.SentMessages).ReplyToAddress);
    }

    [Fact]
    public async Task ACustomReplyToIsUsedAsGiven()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedRuleAsync(dbContext, team, MessageReplyToSource.Custom, replyToOverride: "vec@example.org");
        await SeedCandidatesAsync(dbContext, await SeedSessionAsync(dbContext, team, null), "Roana");

        var sender = new FakeEmailSender();
        await RunAsync(dbContext, team, sender);

        Assert.Equal("vec@example.org", Assert.Single(sender.SentMessages).ReplyToAddress);
    }

    /// <summary>
    /// <b>From is never a rule's to change.</b> Asserted rather than assumed, because the field's
    /// absence is the design: SPF/DKIM/DMARC live on a domain this app does not control, and a
    /// mis-set From sends the mail to spam without any error to notice.
    /// </summary>
    [Fact]
    public async Task TheFromAddressIsAlwaysTheTeams()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedRuleAsync(dbContext, team, MessageReplyToSource.Custom, replyToOverride: "elsewhere@example.net");
        await SeedCandidatesAsync(dbContext, await SeedSessionAsync(dbContext, team, null), "Roana");

        var sender = new FakeEmailSender();
        await RunAsync(dbContext, team, sender);

        Assert.Equal("noreply@example.org", Assert.Single(sender.SentMessages).FromAddress);
    }

    // ---- Copies ----

    /// <summary>The multiplication problem, which is the whole reason the flag exists: three candidates, one copy.</summary>
    [Fact]
    public async Task ARulesOwnBcc_GoesOnceByDefault_NotOncePerRecipient()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedRuleAsync(dbContext, team, bcc: "watch@example.org");
        await SeedCandidatesAsync(dbContext, await SeedSessionAsync(dbContext, team, null), "Roana", "Sam", "Tam");

        var sender = new FakeEmailSender();
        await RunAsync(dbContext, team, sender);

        Assert.Equal(3, sender.SentMessages.Count);
        Assert.Single(sender.SentMessages, m => m.BccAddress == "watch@example.org");
    }

    [Fact]
    public async Task WithOncePerRunOff_EveryMessageCarriesTheCopy()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedRuleAsync(dbContext, team, bcc: "watch@example.org", oncePerRun: false);
        await SeedCandidatesAsync(dbContext, await SeedSessionAsync(dbContext, team, null), "Roana", "Sam", "Tam");

        var sender = new FakeEmailSender();
        await RunAsync(dbContext, team, sender);

        Assert.Equal(3, sender.SentMessages.Count(m => m.BccAddress == "watch@example.org"));
    }

    // ---- What the admin service refuses ----

    private static MessageRuleAdminService CreateAdminService(AppDbContext dbContext) => new(dbContext, new FixedTimeProvider(Now));

    private static async Task<int> SeedUserAsync(AppDbContext dbContext)
    {
        var user = new User { Name = "Admin", Email = $"a-{Guid.NewGuid():N}@example.org", Role = UserRole.TeamAdmin };
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
        return user.Id;
    }

    private static Task<MessageRuleActionResult> CreateWithEnvelopeAsync(
        AppDbContext dbContext, Team team, int userId, MessageEnvelope envelope,
        MessageRecipient recipient = MessageRecipient.Candidate, MessageChannel channel = MessageChannel.Email) =>
        CreateAdminService(dbContext).CreateAsync(
            team.Id, MessageTrigger.CandidateRegistered, "A rule", "Confirm", null, recipient, userId, CancellationToken.None,
            channel, channel == MessageChannel.Discord ? 42UL : null, MessageFanOut.PerRecipient, envelope);

    /// <summary>A Cc'd person cannot unsubscribe, and every candidate sees the address.</summary>
    [Fact]
    public async Task ACcOnCandidateMail_IsRefused()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var userId = await SeedUserAsync(dbContext);

        var result = await CreateWithEnvelopeAsync(dbContext, team, userId,
            MessageEnvelope.Default with { CcAddress = "vec@example.org" });

        Assert.Equal(MessageRuleActionResult.CcNotAllowedOnCandidateMail, result);
    }

    /// <summary>But it is fine on a rule that writes to the team's own inbox — nobody is being disclosed to a candidate there.</summary>
    [Fact]
    public async Task ACcOnAnInternalNotice_IsAllowed()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var userId = await SeedUserAsync(dbContext);

        var result = await CreateAdminService(dbContext).CreateAsync(
            team.Id, MessageTrigger.PaymentUnpaid, "Unpaid notice", "Confirm", 240, MessageRecipient.TeamAdminAddress,
            userId, CancellationToken.None, envelope: MessageEnvelope.Default with { CcAddress = "treasurer@example.org" });

        Assert.Equal(MessageRuleActionResult.Success, result);
    }

    /// <summary>Nobody is addressed on a channel post, so an envelope there would be settings that look like they do something.</summary>
    [Fact]
    public async Task AnEnvelopeOnADiscordRule_IsRefused()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var userId = await SeedUserAsync(dbContext);

        var result = await CreateWithEnvelopeAsync(dbContext, team, userId,
            MessageEnvelope.Default with { BccAddress = "watch@example.org" }, channel: MessageChannel.Discord);

        Assert.Equal(MessageRuleActionResult.EnvelopeNeedsEmail, result);
    }

    [Fact]
    public async Task ACustomReplyToWithNoAddress_IsRefused()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        var userId = await SeedUserAsync(dbContext);

        var result = await CreateWithEnvelopeAsync(dbContext, team, userId,
            MessageEnvelope.Default with { ReplyToSource = MessageReplyToSource.Custom });

        Assert.Equal(MessageRuleActionResult.ReplyToRequired, result);
    }
}
