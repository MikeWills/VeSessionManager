using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Messaging;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// A manual message must be invisible to the scan.
///
/// <para><b>Found running the Worker, not by a test</b> (2026-08-21). Manual triggers have no scanner
/// by design — nothing scans a button press — but the scan loaded every <i>enabled</i> rule and looked
/// one up per trigger, so the three hand-sent messages seeded switched on produced
/// <c>No scanner is registered…</c> at ERROR, for every team, on every tick. Nine per pass on a
/// three-team deployment.</para>
///
/// <para>The error itself is right and worth keeping: a rule somebody created and can see enabled on
/// screen doing nothing at all is indistinguishable from working. What was wrong is asking the
/// question of a message whose whole point is that a person sends it.</para>
///
/// <para>⚠️ This is the failure mode CLAUDE.md's optional-integration rule names directly — a
/// repeating ERROR for an ordinary state teaches people to ignore the log, and the next real error
/// goes with it.</para>
/// </summary>
public class ManualMessageScanTests
{
    private static readonly DateTime Now = new(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    /// <summary>Keeps every entry so a test can assert on what was <i>not</i> logged, which is the point here.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class FakeEmailSender : VeSessionManager.Core.Email.IEmailSender
    {
        public List<VeSessionManager.Core.Email.EmailMessage> SentMessages { get; } = [];

        public Task SendAsync(
            VeSessionManager.Core.Email.EmailCredentials credentials,
            VeSessionManager.Core.Email.EmailMessage message,
            CancellationToken cancellationToken)
        {
            SentMessages.Add(message);
            return Task.CompletedTask;
        }
    }

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<Team> SeedTeamAsync(AppDbContext dbContext)
    {
        var team = new Team { Name = "TESTTEAM", CreatedUtc = Now };
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();

        dbContext.EmailSettings.Add(new EmailSettings
        {
            TeamId = team.Id,
            FromAddress = "noreply@example.org",
            ReplyToAddress = "reply@example.org",
            PrivacyPolicyUrl = "https://example.org/privacy",
            AdminNotificationEmail = "admin@example.org",
            UpdatedUtc = Now
        });
        await dbContext.SaveChangesAsync();
        return team;
    }

    private static async Task AddRuleAsync(AppDbContext dbContext, Team team, MessageTrigger trigger, bool enabled = true)
    {
        dbContext.MessageRules.Add(new MessageRule
        {
            TeamId = team.Id,
            Name = $"{trigger} message",
            Trigger = trigger,
            Subject = "Subject",
            Body = "<p>Body</p>",
            IsEnabled = enabled,
            CreatedUtc = Now.AddYears(-1)
        });
        await dbContext.SaveChangesAsync();
    }

    private static async Task<(MessageRuleResult Result, CapturingLogger<MessageRuleService> Logger)> RunAsync(
        AppDbContext dbContext, Team team)
    {
        var logger = new CapturingLogger<MessageRuleService>();
        var sender = new FakeEmailSender();
        var service = MessageRuleTestHarness.Create(dbContext, sender, new FixedTimeProvider(Now), logger: logger);
        var result = await service.RunAsync(team, null, null, CancellationToken.None);
        return (result, logger);
    }

    [Theory]
    [InlineData(MessageTrigger.ManualToCandidate)]
    [InlineData(MessageTrigger.ManualToVe)]
    [InlineData(MessageTrigger.ManualFelonyDisclosureInstructions)]
    [InlineData(MessageTrigger.ManualYouthProgramInstructions)]
    public async Task AnEnabledManualMessage_IsSkippedSilently(MessageTrigger trigger)
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await AddRuleAsync(dbContext, team, trigger);

        var (result, logger) = await RunAsync(dbContext, team);

        Assert.Equal(0, result.Sent);
        Assert.Equal(0, result.Failed);
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Error);
    }

    /// <summary>
    /// The seeded set exactly as a team receives it, because the bug only appeared with all three
    /// hand-sent messages on at once and a per-trigger test would have missed how loud it was.
    /// </summary>
    [Fact]
    public async Task TheSeededHandSentMessages_ProduceNoErrorsAtAll()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await AddRuleAsync(dbContext, team, MessageTrigger.ManualFelonyDisclosureInstructions);
        await AddRuleAsync(dbContext, team, MessageTrigger.ManualToCandidate);
        await AddRuleAsync(dbContext, team, MessageTrigger.ManualYouthProgramInstructions);

        var (_, logger) = await RunAsync(dbContext, team);

        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Error);
    }

    /// <summary>
    /// ⚠️ The other half, and the reason this is a filter on the mechanism rather than on "has a
    /// scanner". A scheduled trigger whose scanner is genuinely missing — a new trigger someone added
    /// to the registry and forgot to register a scanner for — must still shout, because that rule
    /// looks enabled on screen and does nothing.
    /// </summary>
    [Fact]
    public async Task AScheduledTriggerWithNoScanner_StillLogsAnError()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);

        // SentByHand is in the enum but deliberately absent from MessageTriggerDefinitions.All and
        // has no scanner — the closest thing to "somebody added a trigger and forgot the scanner".
        //
        // ⚠️ It is also why the fix excludes triggers whose definition says Manual, rather than
        // calling MessageTriggerDefinitions.For on every rule: For THROWS for anything outside All,
        // so asking would turn this quiet, correct error into a crashed tick.
        await AddRuleAsync(dbContext, team, MessageTrigger.SentByHand);

        var (_, logger) = await RunAsync(dbContext, team);

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("No scanner is registered"));
    }

    /// <summary>A disabled manual message was never a problem, but it is worth pinning that the fix did not simply hide everything.</summary>
    [Fact]
    public async Task ADisabledManualMessage_IsAlsoSilent()
    {
        await using var dbContext = CreateContext();
        var team = await SeedTeamAsync(dbContext);
        await AddRuleAsync(dbContext, team, MessageTrigger.ManualToCandidate, enabled: false);

        var (_, logger) = await RunAsync(dbContext, team);

        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Error);
    }
}
