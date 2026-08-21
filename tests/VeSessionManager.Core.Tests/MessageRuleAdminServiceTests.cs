using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Messaging;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// The write side of the Message Rules screen (#401, PR2).
///
/// <para>Most of these are validation, and each one describes a rule that would look configured and
/// do nothing — which is the failure mode worth refusing, because a rule that silently never fires is
/// indistinguishable from a quiet week.</para>
/// </summary>
public class MessageRuleAdminServiceTests
{
    private static readonly DateTime Now = new(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static MessageRuleAdminService CreateService(AppDbContext dbContext) =>
        new(dbContext, new FixedTimeProvider(Now));

    private static async Task<(Team Team, int UserId)> SeedAsync(AppDbContext dbContext)
    {
        var team = new Team { Name = "TESTTEAM", CreatedUtc = Now };
        var user = new User { Name = "Admin", Email = "admin@example.org", Role = UserRole.TeamAdmin };
        dbContext.Teams.Add(team);
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
        return (team, user.Id);
    }

    private static Task<MessageRuleActionResult> CreateReminderAsync(
        AppDbContext dbContext, Team team, int userId, int? hours = 24,
        MessageRecipient recipient = MessageRecipient.Candidate, string name = "Day before",
        string subject = "Your session is tomorrow", string body = "<p>See you then.</p>") =>
        CreateService(dbContext).CreateAsync(
            team.Id, MessageTrigger.BeforeSessionStart, name, subject, body, hours, recipient, userId, CancellationToken.None);

    [Fact]
    public async Task Create_StoresTheRule_AndAuditsIt()
    {
        await using var dbContext = CreateContext();
        var (team, userId) = await SeedAsync(dbContext);

        var result = await CreateReminderAsync(dbContext, team, userId, hours: 48);

        Assert.Equal(MessageRuleActionResult.Success, result);
        var rule = await dbContext.MessageRules.SingleAsync();
        Assert.Equal(48, rule.ParameterHours);
        Assert.True(rule.IsEnabled);

        // The audit row must point at the real rule, not at the 0 an unsaved entity has — an entry
        // nobody can trace back is not an audit trail.
        var audit = await dbContext.AuditLogs.SingleAsync();
        Assert.Equal("MessageRuleCreated", audit.Action);
        Assert.Equal(rule.Id, audit.EntityId);
    }

    /// <summary>
    /// The safety property, asserted where it is set rather than only where it is read: a rule created
    /// now cannot reach anybody whose moment passed before now. Seeding this in the past — which is
    /// the natural thing to do if you think of it as a timestamp — is what "3000 emails because you
    /// added a rule" looks like.
    /// </summary>
    [Fact]
    public async Task Create_StampsCreatedUtcAtCreation_WhichIsWhatBoundsEveryScan()
    {
        await using var dbContext = CreateContext();
        var (team, userId) = await SeedAsync(dbContext);

        await CreateReminderAsync(dbContext, team, userId);

        Assert.Equal(Now, (await dbContext.MessageRules.SingleAsync()).CreatedUtc);
    }

    [Fact]
    public async Task Create_WithNoName_IsRefused()
    {
        await using var dbContext = CreateContext();
        var (team, userId) = await SeedAsync(dbContext);

        Assert.Equal(MessageRuleActionResult.NameRequired, await CreateReminderAsync(dbContext, team, userId, name: "  "));
        Assert.Empty(dbContext.MessageRules);
    }

    /// <summary>A time-relative trigger with no hours has not been answered — the parameter is the question that trigger asks.</summary>
    [Fact]
    public async Task Create_TimeRelativeTriggerWithNoHours_IsRefused()
    {
        await using var dbContext = CreateContext();
        var (team, userId) = await SeedAsync(dbContext);

        Assert.Equal(MessageRuleActionResult.ParameterRequired, await CreateReminderAsync(dbContext, team, userId, hours: null));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-24)]
    [InlineData(MessageRuleAdminService.MaxParameterHours + 1)]
    public async Task Create_WithHoursOutOfRange_IsRefused(int hours)
    {
        await using var dbContext = CreateContext();
        var (team, userId) = await SeedAsync(dbContext);

        Assert.Equal(MessageRuleActionResult.ParameterOutOfRange, await CreateReminderAsync(dbContext, team, userId, hours));
    }

    /// <summary>
    /// A state trigger has no parameter, and passing none must be fine — otherwise the registration
    /// confirmation could not be created at all.
    /// </summary>
    [Fact]
    public async Task Create_StateTriggerWithNoHours_IsAccepted()
    {
        await using var dbContext = CreateContext();
        var (team, userId) = await SeedAsync(dbContext);

        var result = await CreateService(dbContext).CreateAsync(
            team.Id, MessageTrigger.CandidateRegistered, "Confirmation", "Subject", "DayBeforeReminder", null,
            MessageRecipient.Candidate, userId, CancellationToken.None);

        Assert.Equal(MessageRuleActionResult.Success, result);
        Assert.Null((await dbContext.MessageRules.SingleAsync()).ParameterHours);
    }

    /// <summary>
    /// The service refuses a recipient the trigger cannot address — not just left off the form, since
    /// the form is a default and the value arrives in a POST.
    ///
    /// <para>⚠️ <b>Repointed</b> when the trigger × recipient matrix landed: this used to use
    /// <c>CandidateRegistered</c> + <c>TeamAdminAddress</c>, which is now legal by decision. The
    /// mechanism being tested is unchanged; the example had to move to a pair that is still illegal.
    /// A registration confirmation posted into a Discord channel is that pair — the matrix marks the
    /// channel column N for every trigger but the session reminder.</para>
    /// </summary>
    [Fact]
    public async Task Create_WithARecipientTheTriggerCannotAddress_IsRefused()
    {
        await using var dbContext = CreateContext();
        var (team, userId) = await SeedAsync(dbContext);

        var result = await CreateService(dbContext).CreateAsync(
            team.Id, MessageTrigger.CandidateRegistered, "Confirmation", "Subject", "DayBeforeReminder", null,
            MessageRecipient.DiscordChannel, userId, CancellationToken.None);

        Assert.Equal(MessageRuleActionResult.RecipientNotLegal, result);
    }

    // ⚠️ Two more template-era refusals were deleted here on 2026-08-21, for the same reason as the
    // one above: an edit could not swap in a VE-audience template, and neither create nor edit could
    // point at another team's template. Both described ways of pointing at the wrong stored template.
    // A message carries its own words now, so there is nothing to point at and nothing to get wrong —
    // the failures are unreachable rather than caught, which is why no replacement test stands here.

    /// <summary>
    /// An edit leaves <c>CreatedUtc</c> alone. Refreshing it would mean a typo corrected an hour later
    /// silently skips everybody whose moment fell in between — the bound exists to stop retroactive
    /// sends, not to be reset by ordinary maintenance.
    /// </summary>
    [Fact]
    public async Task Update_DoesNotMoveCreatedUtc()
    {
        await using var dbContext = CreateContext();
        var (team, userId) = await SeedAsync(dbContext);
        await CreateReminderAsync(dbContext, team, userId);
        var rule = await dbContext.MessageRules.SingleAsync();
        var createdUtc = rule.CreatedUtc;

        var result = await CreateService(dbContext).UpdateAsync(
            rule.Id, "Two days before", "Subject", "DayBeforeReminder", 48, MessageRecipient.Candidate, userId, CancellationToken.None);

        Assert.Equal(MessageRuleActionResult.Success, result);
        var updated = await dbContext.MessageRules.SingleAsync();
        Assert.Equal(48, updated.ParameterHours);
        Assert.Equal("Two days before", updated.Name);
        Assert.Equal(createdUtc, updated.CreatedUtc);
    }

    /// <summary>An edit is validated exactly as a create is — the same posted values arrive by a different route.</summary>
    [Fact]
    public async Task Update_WithHoursOutOfRange_IsRefused_AndChangesNothing()
    {
        await using var dbContext = CreateContext();
        var (team, userId) = await SeedAsync(dbContext);
        await CreateReminderAsync(dbContext, team, userId);
        var rule = await dbContext.MessageRules.SingleAsync();

        var result = await CreateService(dbContext).UpdateAsync(
            rule.Id, "Silly", "Subject", "DayBeforeReminder", 0, MessageRecipient.Candidate, userId, CancellationToken.None);

        Assert.Equal(MessageRuleActionResult.ParameterOutOfRange, result);
        Assert.Equal(24, (await dbContext.MessageRules.SingleAsync()).ParameterHours);
    }

    /// <summary>
    /// Switching off is the only way to stop a rule, and it leaves no marker — so switching it back on
    /// picks up whoever is eligible at that moment rather than chasing everybody missed in between.
    /// </summary>
    [Fact]
    public async Task SetEnabled_TogglesAndAudits_AndWritesNoMarkers()
    {
        await using var dbContext = CreateContext();
        var (team, userId) = await SeedAsync(dbContext);
        await CreateReminderAsync(dbContext, team, userId);
        var rule = await dbContext.MessageRules.SingleAsync();

        Assert.Equal(MessageRuleActionResult.Success,
            await CreateService(dbContext).SetEnabledAsync(rule.Id, false, userId, CancellationToken.None));
        Assert.False((await dbContext.MessageRules.SingleAsync()).IsEnabled);
        Assert.Empty(dbContext.MessageRuleRuns);
        Assert.Contains(dbContext.AuditLogs, a => a.Action == "MessageRuleDisabled");

        Assert.Equal(MessageRuleActionResult.Success,
            await CreateService(dbContext).SetEnabledAsync(rule.Id, true, userId, CancellationToken.None));
        Assert.True((await dbContext.MessageRules.SingleAsync()).IsEnabled);
        Assert.Contains(dbContext.AuditLogs, a => a.Action == "MessageRuleEnabled");
    }

    /// <summary>
    /// A real delete: the row goes. Switching off is the answer to "not right now", and this is the
    /// answer to "we do not do this" — Mike asked for both, and conflating them would have made one of
    /// the two questions unanswerable.
    /// </summary>
    [Fact]
    public async Task Delete_RemovesTheRule_AndAuditsIt()
    {
        await using var dbContext = CreateContext();
        var (team, userId) = await SeedAsync(dbContext);
        await CreateReminderAsync(dbContext, team, userId);
        var rule = await dbContext.MessageRules.SingleAsync();

        var result = await CreateService(dbContext).DeleteAsync(rule.Id, userId, CancellationToken.None);

        Assert.Equal(MessageRuleActionResult.Success, result);
        Assert.Empty(dbContext.MessageRules);
        Assert.Contains(dbContext.AuditLogs, a => a.Action == "MessageRuleDeleted");
    }

    /// <summary>
    /// <b>The record of what it sent to real people survives it.</b> That is what <c>RuleName</c> and
    /// <c>Trigger</c> are snapshots for, and why the FK is SetNull rather than Cascade — deleting a
    /// rule is tidying up a configuration, not editing history.
    /// </summary>
    [Fact]
    public async Task Delete_KeepsTheRunsItAlreadyProduced_WithTheirRuleNameIntact()
    {
        await using var dbContext = CreateContext();
        var (team, userId) = await SeedAsync(dbContext);
        await CreateReminderAsync(dbContext, team, userId);
        var rule = await dbContext.MessageRules.SingleAsync();

        dbContext.MessageRuleRuns.Add(new MessageRuleRun
        {
            TeamId = team.Id,
            MessageRuleId = rule.Id,
            RuleName = rule.Name,
            Trigger = rule.Trigger,
            SubjectType = MessageSubjectType.Candidate,
            SubjectId = 7,
            FiredUtc = Now,
            Outcome = MessageRuleOutcome.Sent
        });
        await dbContext.SaveChangesAsync();

        await CreateService(dbContext).DeleteAsync(rule.Id, userId, CancellationToken.None);

        var run = await dbContext.MessageRuleRuns.SingleAsync();
        Assert.Null(run.MessageRuleId);
        Assert.Equal("Day before", run.RuleName);
        Assert.Equal(MessageTrigger.BeforeSessionStart, run.Trigger);
        Assert.Equal(MessageRuleOutcome.Sent, run.Outcome);
    }

    /// <summary>
    /// And an orphaned run guards nothing, which is the right answer rather than a loose end: it
    /// belongs to a rule that no longer exists. A rule re-created afterwards starts clean, bounded by
    /// its own <c>CreatedUtc</c> — so "delete and re-add" cannot become a way to re-email everybody.
    /// </summary>
    [Fact]
    public async Task ARuleRecreatedAfterADelete_IsANewRule_WithItsOwnCreatedUtc()
    {
        await using var dbContext = CreateContext();
        var (team, userId) = await SeedAsync(dbContext);
        await CreateReminderAsync(dbContext, team, userId);
        var original = await dbContext.MessageRules.SingleAsync();
        await CreateService(dbContext).DeleteAsync(original.Id, userId, CancellationToken.None);

        await CreateReminderAsync(dbContext, team, userId);

        var replacement = await dbContext.MessageRules.SingleAsync();
        Assert.NotEqual(original.Id, replacement.Id);
        Assert.Equal(Now, replacement.CreatedUtc);
    }

    [Fact]
    public async Task Update_OrSetEnabled_OnARuleThatIsGone_IsNotFound()
    {
        await using var dbContext = CreateContext();
        var (_, userId) = await SeedAsync(dbContext);

        Assert.Equal(MessageRuleActionResult.NotFound, await CreateService(dbContext)
            .UpdateAsync(999, "x", "Subject", "DayBeforeReminder", 24, MessageRecipient.Candidate, userId, CancellationToken.None));
        Assert.Equal(MessageRuleActionResult.NotFound, await CreateService(dbContext)
            .SetEnabledAsync(999, false, userId, CancellationToken.None));
        Assert.Equal(MessageRuleActionResult.NotFound, await CreateService(dbContext)
            .DeleteAsync(999, userId, CancellationToken.None));
    }
}
