using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Discord;
using VeSessionManager.Core.Email;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Integrations;
using VeSessionManager.Core.Messaging;
using VeSessionManager.Core.Messaging.Scanners;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Builds a real <see cref="MessageRuleService"/> over a test <see cref="AppDbContext"/> — every
/// scanner registered, the real dispatcher, the real renderer (#401).
///
/// <para>Shared rather than copied into each test file because the <b>set of scanners</b> is the
/// thing worth building once: a scanner added later and wired into DI but not into a test harness
/// would leave its trigger silently untested everywhere.</para>
/// </summary>
public static class MessageRuleTestHarness
{
    public const string PublicBaseUrl = "https://test.example";

    /// <summary>
    /// Records every channel post instead of making one, so a test can assert on what would have gone
    /// to Discord (#401 PR4). <c>IsConfigured</c> is true because the interesting cases are about what
    /// the dispatcher does with a working client; the unconfigured path has its own test.
    /// </summary>
    public sealed class FakeDiscordChannelClient : IDiscordChannelMessageClient
    {
        public List<(ulong GuildId, ulong ChannelId, string Message)> Posts { get; } = [];
        public Exception? ThrowOnNextPost { get; set; }
        public bool IsConfigured { get; set; } = true;

        /// <summary>What each post was allowed to ping (#116) — empty on every existing test, which is the point.</summary>
        public List<IReadOnlyList<ulong>> AllowedRoleIds { get; } = [];

        public Task PostMessageAsync(ulong guildId, ulong channelId, string message, IReadOnlyList<ulong> mentionableRoleIds, CancellationToken cancellationToken)
        {
            if (ThrowOnNextPost is not null)
            {
                var ex = ThrowOnNextPost;
                ThrowOnNextPost = null;
                throw ex;
            }

            Posts.Add((guildId, channelId, message));
            AllowedRoleIds.Add(mentionableRoleIds);
            return Task.CompletedTask;
        }
    }

    public static MessageRuleService Create(
        AppDbContext dbContext, IEmailSender emailSender, TimeProvider timeProvider, IDiscordChannelMessageClient? discordClient = null)
    {
        var appOptions = Options.Create(new AppOptions { PublicBaseUrl = PublicBaseUrl });
        IMessageTriggerScanner[] scanners =
        [
            new CandidateRegisteredScanner(dbContext, appOptions, NullLogger<CandidateRegisteredScanner>.Instance),
            new BeforeSessionStartScanner(dbContext),
            new FccFeeOutstandingScanner(dbContext),
            new PaymentUnpaidScanner(dbContext),
            new CandidateTestedScanner(dbContext),
            new LicenseGrantedScanner(dbContext, NullLogger<LicenseGrantedScanner>.Instance),
            new FelonyDisclosureDeclaredScanner(dbContext, NullLogger<FelonyDisclosureDeclaredScanner>.Instance)
        ];

        var dispatch = new MessageDispatchService(
            dbContext,
            new EmailTemplateRenderer(dbContext, NullLogger<EmailTemplateRenderer>.Instance),
            emailSender,
            discordClient ?? new FakeDiscordChannelClient(),
            new TeamIntegrationState(NullLogger<TeamIntegrationState>.Instance),
            timeProvider,
            NullLogger<MessageDispatchService>.Instance);

        return new MessageRuleService(dbContext, scanners, dispatch, timeProvider, NullLogger<MessageRuleService>.Instance);
    }

    /// <summary>
    /// A rule with <c>CreatedUtc</c> far enough in the past to bound nothing, which is what a test
    /// about a scanner's predicate wants. Tests about the bound itself pass their own.
    /// </summary>
    public static MessageRule NewRule(
        Team team, MessageTrigger trigger, string templateKey, int? parameterHours, DateTime createdUtc,
        MessageRecipient recipient = MessageRecipient.Candidate) => new()
        {
            TeamId = team.Id,
            Name = $"{trigger} rule",
            Trigger = trigger,
            ParameterHours = parameterHours,
            TemplateKey = templateKey,
            Recipient = recipient,
            CreatedUtc = createdUtc
        };
}
