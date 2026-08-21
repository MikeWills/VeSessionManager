using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Email;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Messaging;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Rules that post to Discord instead of emailing (#401 PR4).
///
/// <para>Two properties here matter more than the rest: <b>a channel post carries nothing per-person
/// </b> — no From, no Reply-To, no monitoring Bcc, no unsubscribe — and <b>a digest is one post, not
/// forty</b>. The first is structural (this path builds no <c>EmailMessage</c>) and the second is the
/// entire reason <c>MessageFanOut</c> exists.</para>
/// </summary>
public class DiscordMessageRuleTests
{
    private static readonly DateTime Now = new(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc);
    private const ulong GuildId = 999;
    private const ulong ChannelId = 4242;

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private sealed class ExplodingEmailSender : IEmailSender
    {
        public Task SendAsync(EmailCredentials credentials, EmailMessage message, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A Discord rule must never reach the email sender.");
    }

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<Team> SeedTeamAsync(AppDbContext dbContext, bool discordConfigured = true)
    {
        var team = new Team
        {
            Name = "TESTTEAM",
            DiscordGuildId = discordConfigured ? GuildId : null,
            // Set so that a test proving the email path is untouched cannot pass merely because SMTP
            // was missing.
            SmtpHost = "smtp.example.org",
            SmtpUsername = "smtp-user",
            SmtpPassword = "smtp-pass",
            CreatedUtc = Now
        };
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();

        dbContext.EmailSettings.Add(new EmailSettings
        {
            TeamId = team.Id,
            FromAddress = "noreply@example.org",
            ReplyToAddress = "reply@example.org",
            PrivacyPolicyUrl = "https://example.org/privacy",
            AdminNotificationEmail = "admin@example.org",
            BccAddress = "monitoring@example.org"
        });
        await dbContext.SaveChangesAsync();
        return team;
    }

    private static async Task SeedTemplateAsync(AppDbContext dbContext, Team team, string key, string body) =>
        await AddAsync(dbContext, new EmailTemplate { TeamId = team.Id, Key = key, Subject = "Ignored on Discord", Body = body });

    private static async Task AddAsync<T>(AppDbContext dbContext, T entity) where T : class
    {
        dbContext.Add(entity);
        await dbContext.SaveChangesAsync();
    }

    private static async Task<MessageRule> SeedDiscordRuleAsync(
        AppDbContext dbContext, Team team, MessageFanOut fanOut, string templateKey = "Post", ulong? channelId = ChannelId)
    {
        var rule = MessageRuleTestHarness.NewRule(team, MessageTrigger.CandidateRegistered, templateKey, null, Now.AddYears(-1));
        rule.Channel = MessageChannel.Discord;
        rule.DiscordChannelId = channelId;
        rule.FanOut = fanOut;
        await AddAsync(dbContext, rule);
        return rule;
    }

    private static async Task<Session> SeedSessionAsync(AppDbContext dbContext, Team team)
    {
        var vec = new Vec { Name = "ARRL" };
        var user = new User { Name = "System", Email = $"s-{Guid.NewGuid():N}@localhost", Role = UserRole.SystemAdmin };
        var session = new Session
        {
            ExamToolsSessionId = $"s-{Guid.NewGuid():N}",
            Title = "August",
            ScheduledStartUtc = Now.AddDays(3),
            DurationMinutes = 60,
            Vec = vec,
            TeamId = team.Id,
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
        await AddAsync(dbContext, session);
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
                FirstName = name.Split(' ')[0],
                Email = $"{name.Split(' ')[0].ToLowerInvariant()}@example.com",
                DateRegisteredUtc = Now
            });
        }

        await dbContext.SaveChangesAsync();
    }

    private static Task<MessageRuleResult> RunAsync(
        AppDbContext dbContext, Team team, MessageRuleTestHarness.FakeDiscordChannelClient discord) =>
        MessageRuleTestHarness.Create(dbContext, new ExplodingEmailSender(), new FixedTimeProvider(Now), discord)
            .RunAsync(team, [MessageTrigger.CandidateRegistered], null, CancellationToken.None);

    /// <summary>The forty-posts case, which is what <c>MessageFanOut</c> exists to prevent: three candidates, one post.</summary>
    [Fact]
    public async Task ADigestPostsOnce_NamingEverybody()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedTemplateAsync(dbContext, team, "Post", "<p><strong>{{Count}}</strong> new registrations:</p>{{Subjects}}");
        await SeedDiscordRuleAsync(dbContext, team, MessageFanOut.SingleDigest);
        var session = await SeedSessionAsync(dbContext, team);
        await SeedCandidatesAsync(dbContext, session, "Roana Glory", "Sam Vale", "Tam Okonkwo");

        var discord = new MessageRuleTestHarness.FakeDiscordChannelClient();
        var result = await RunAsync(dbContext, team, discord);

        var post = Assert.Single(discord.Posts);
        Assert.Equal(GuildId, post.GuildId);
        Assert.Equal(ChannelId, post.ChannelId);
        Assert.Contains("**3** new registrations", post.Message);
        Assert.Contains("• Roana Glory", post.Message);
        Assert.Contains("• Tam Okonkwo", post.Message);

        // Every subject is still marked, so the next tick has nothing to say — one marker for the post
        // would leave two of the three looking unsent.
        Assert.Equal(3, result.Sent);
        Assert.Equal(3, dbContext.MessageRuleRuns.Count());
    }

    [Fact]
    public async Task ADigestDoesNotRepeatItselfOnTheNextTick_AndOnlyMentionsNewcomers()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedTemplateAsync(dbContext, team, "Post", "<p>{{Subjects}}</p>");
        await SeedDiscordRuleAsync(dbContext, team, MessageFanOut.SingleDigest);
        var session = await SeedSessionAsync(dbContext, team);
        await SeedCandidatesAsync(dbContext, session, "Roana Glory");

        var discord = new MessageRuleTestHarness.FakeDiscordChannelClient();
        await RunAsync(dbContext, team, discord);
        await SeedCandidatesAsync(dbContext, session, "Sam Vale");
        await RunAsync(dbContext, team, discord);

        Assert.Equal(2, discord.Posts.Count);
        Assert.DoesNotContain("Roana", discord.Posts[1].Message);
        Assert.Contains("Sam Vale", discord.Posts[1].Message);
    }

    [Fact]
    public async Task PerRecipientPostsOncePerSubject()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedTemplateAsync(dbContext, team, "Post", "<p>Welcome {{CandidateFirstName}}!</p>");
        await SeedDiscordRuleAsync(dbContext, team, MessageFanOut.PerRecipient);
        var session = await SeedSessionAsync(dbContext, team);
        await SeedCandidatesAsync(dbContext, session, "Roana Glory", "Sam Vale");

        var discord = new MessageRuleTestHarness.FakeDiscordChannelClient();
        await RunAsync(dbContext, team, discord);

        Assert.Equal(2, discord.Posts.Count);
        Assert.Contains(discord.Posts, p => p.Message.Contains("Welcome Roana!"));
        Assert.Contains(discord.Posts, p => p.Message.Contains("Welcome Sam!"));
    }

    /// <summary>
    /// A failed digest marks nobody. Recording some of them as sent would be recording that a post
    /// said something it never said — and the retry is the whole post, not the remainder.
    /// </summary>
    [Fact]
    public async Task AFailedDigest_MarksNobodySent_AndTheWholePostIsRetried()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedTemplateAsync(dbContext, team, "Post", "<p>{{Subjects}}</p>");
        await SeedDiscordRuleAsync(dbContext, team, MessageFanOut.SingleDigest);
        var session = await SeedSessionAsync(dbContext, team);
        await SeedCandidatesAsync(dbContext, session, "Roana Glory", "Sam Vale");

        var discord = new MessageRuleTestHarness.FakeDiscordChannelClient { ThrowOnNextPost = new InvalidOperationException("Discord said no") };
        var failed = await RunAsync(dbContext, team, discord);

        Assert.Equal(2, failed.Failed);
        Assert.Equal(0, failed.Sent);
        Assert.Empty(discord.Posts);
        Assert.All(dbContext.MessageRuleRuns, r => Assert.Equal(MessageRuleOutcome.Failed, r.Outcome));

        var retried = await RunAsync(dbContext, team, discord);

        Assert.Equal(2, retried.Sent);
        Assert.Contains("Roana Glory", Assert.Single(discord.Posts).Message);
    }

    /// <summary>
    /// Unconfigured Discord leaves no marker, exactly as unconfigured SMTP does — everything waiting
    /// posts on the first tick after the guild and channel are set.
    /// </summary>
    [Fact]
    public async Task WithNoGuildOrChannel_NothingIsMarked_SoItPostsOnceConfigured()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext, discordConfigured: false);
        await SeedTemplateAsync(dbContext, team, "Post", "<p>Hi</p>");
        var rule = await SeedDiscordRuleAsync(dbContext, team, MessageFanOut.PerRecipient);
        var session = await SeedSessionAsync(dbContext, team);
        await SeedCandidatesAsync(dbContext, session, "Roana Glory");

        var discord = new MessageRuleTestHarness.FakeDiscordChannelClient();
        Assert.Equal(1, (await RunAsync(dbContext, team, discord)).Waiting);
        Assert.Empty(dbContext.MessageRuleRuns);

        team.DiscordGuildId = GuildId;
        rule.DiscordChannelId = ChannelId;
        await dbContext.SaveChangesAsync();

        Assert.Equal(1, (await RunAsync(dbContext, team, discord)).Sent);
    }

    /// <summary>Discord switched off settles, like every other muted send — nothing queues while it is off.</summary>
    [Fact]
    public async Task DiscordSwitchedOff_RecordsSuppressed()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        team.IntegrationOverridesEnabled = true;
        team.DiscordEnabled = false;
        await SeedTemplateAsync(dbContext, team, "Post", "<p>Hi</p>");
        await SeedDiscordRuleAsync(dbContext, team, MessageFanOut.PerRecipient);
        var session = await SeedSessionAsync(dbContext, team);
        await SeedCandidatesAsync(dbContext, session, "Roana Glory");

        var discord = new MessageRuleTestHarness.FakeDiscordChannelClient();
        Assert.Equal(1, (await RunAsync(dbContext, team, discord)).Suppressed);
        Assert.Empty(discord.Posts);
        Assert.Equal(MessageRuleOutcome.Suppressed, dbContext.MessageRuleRuns.Single().Outcome);
    }

    /// <summary>
    /// The guarantee the PR4 plan called out: unsubscribe and the CAN-SPAM footer are per-person
    /// concepts and must not reach a channel. Asserted by the email sender throwing if it is ever
    /// touched — the Discord path builds no message that could carry them.
    /// </summary>
    [Fact]
    public async Task ADiscordRuleNeverTouchesTheEmailPath()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedTemplateAsync(dbContext, team, "Post", "<p>Hi</p>");
        await SeedDiscordRuleAsync(dbContext, team, MessageFanOut.PerRecipient);
        var session = await SeedSessionAsync(dbContext, team);
        await SeedCandidatesAsync(dbContext, session, "Roana Glory");

        // ExplodingEmailSender throws on any use; reaching the end without it is the assertion.
        Assert.Equal(1, (await RunAsync(dbContext, team, new MessageRuleTestHarness.FakeDiscordChannelClient())).Sent);
    }

    /// <summary>
    /// And it leaves the candidate's email history alone. Those <c>…SentUtc</c> columns mean "this
    /// candidate was emailed", which a post in a chat room is not.
    /// </summary>
    [Fact]
    public async Task ADiscordRuleDoesNotStampTheEmailHistoryColumns()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedTemplateAsync(dbContext, team, "Post", "<p>Hi</p>");
        await SeedDiscordRuleAsync(dbContext, team, MessageFanOut.PerRecipient);
        var session = await SeedSessionAsync(dbContext, team);
        await SeedCandidatesAsync(dbContext, session, "Roana Glory");

        await RunAsync(dbContext, team, new MessageRuleTestHarness.FakeDiscordChannelClient());

        Assert.Null(dbContext.Candidates.Single().RegistrationConfirmationSentUtc);
    }

    // ---- Mentionable roles, per team (#116) ---------------------------------------------------

    /// <summary>The default every existing team has: a post resolves no mentions at all.</summary>
    [Fact]
    public async Task ByDefault_APostMayPingNobody()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await SeedTemplateAsync(dbContext, team, "Post", "hello");
        await SeedDiscordRuleAsync(dbContext, team, MessageFanOut.SingleDigest);
        await SeedCandidatesAsync(dbContext, await SeedSessionAsync(dbContext, team), "Roana Glory");

        var discord = new MessageRuleTestHarness.FakeDiscordChannelClient();
        await RunAsync(dbContext, team, discord);

        Assert.Empty(Assert.Single(discord.AllowedRoleIds));
    }

    /// <summary>A team that named a role gets that role, and only that role, offered to Discord.</summary>
    [Fact]
    public async Task AConfiguredRole_ReachesTheClient()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        team.DiscordMentionableRoleIds = "555, 666";
        await dbContext.SaveChangesAsync();
        await SeedTemplateAsync(dbContext, team, "Post", "<@&555> heads up");
        await SeedDiscordRuleAsync(dbContext, team, MessageFanOut.SingleDigest);
        await SeedCandidatesAsync(dbContext, await SeedSessionAsync(dbContext, team), "Roana Glory");

        var discord = new MessageRuleTestHarness.FakeDiscordChannelClient();
        await RunAsync(dbContext, team, discord);

        Assert.Equal([555UL, 666UL], Assert.Single(discord.AllowedRoleIds));
    }

    /// <summary>
    /// ⚠️ The property the allow-list exists to preserve. A candidate named <c>@everyone</c> reaches a
    /// channel post through <c>{{Subjects}}</c> unescaped — and still cannot ping the server, because
    /// the ids offered to Discord are the team's roles and <c>@everyone</c> is not among them.
    /// </summary>
    [Fact]
    public async Task ACandidateNamedEveryone_StillCannotPingTheServer()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        team.DiscordMentionableRoleIds = "555";
        await dbContext.SaveChangesAsync();
        await SeedTemplateAsync(dbContext, team, "Post", "{{Subjects}}");
        await SeedDiscordRuleAsync(dbContext, team, MessageFanOut.SingleDigest);
        await SeedCandidatesAsync(dbContext, await SeedSessionAsync(dbContext, team), "@everyone");

        var discord = new MessageRuleTestHarness.FakeDiscordChannelClient();
        await RunAsync(dbContext, team, discord);

        var post = Assert.Single(discord.Posts);
        Assert.Contains("@everyone", post.Message);                       // the text is not mangled...
        Assert.Equal([555UL], Assert.Single(discord.AllowedRoleIds));     // ...and it resolves to nothing
    }

}
