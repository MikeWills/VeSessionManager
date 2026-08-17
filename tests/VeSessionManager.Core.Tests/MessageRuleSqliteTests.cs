using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// The two halves of #401 that EF InMemory cannot observe: the <c>MessageRules</c> migration's
/// rule seeding and marker backfill (raw SQL, which InMemory cannot execute at all), and the unique
/// index that makes a retry an update rather than a duplicate.
///
/// <para><b>Why the backfill needs a test rather than a read-through.</b> If those statements resolve
/// nothing the migration still succeeds — no rules, no markers, and the first tick after deploy
/// emails everybody who is currently mid-cycle. Silent, and indistinguishable from "there was nothing
/// to backfill". Same shape as <see cref="AuditLogTeamBackfillSqliteTests"/>: migrate to the revision
/// before, seed what production actually holds, then apply the rest.</para>
/// </summary>
public class MessageRuleSqliteTests
{
    private static readonly DateTime Now = new(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>The migration immediately before MessageRules — the state a deployment is in when the backfill runs.</summary>
    private const string MigrationBeforeTheBackfill = "20260816190833_VeEmailPreferences";

    private static async Task<AppDbContext> OpenAsync(SqliteConnection connection)
    {
        await connection.OpenAsync();
        return new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
    }

    /// <summary>
    /// Raw SQL rather than EF, because seeding through a DbSet writes the columns the <i>current</i>
    /// model has and those do not all exist at the migration this pins to — the trap already recorded
    /// in <see cref="PaymentUniqueIndexSqliteTests"/>.
    /// </summary>
    private sealed record Seed(int TeamId, int SessionId, int CandidateId, int PaymentId);

    private static async Task<Seed> SeedAsync(AppDbContext dbContext)
    {
        await dbContext.Database.ExecuteSqlRawAsync(
            "INSERT INTO Teams (Name, CreatedUtc, PurgeUnpaidLinkDays, ZoomBreakoutRoomCount) VALUES ('TEAMA', {0}, 30, 0)", Now);
        var teamId = await dbContext.Teams.Select(t => t.Id).OrderByDescending(id => id).FirstAsync();

        var userId = await PaymentUniqueIndexSqliteTests.SeedUserViaSqlAsync(dbContext, $"system-{Guid.NewGuid():N}@localhost");
        var sessionId = await PaymentUniqueIndexSqliteTests.SeedSessionViaSqlAsync(dbContext, $"s-{Guid.NewGuid():N}", teamId, userId);
        var candidateId = await PaymentUniqueIndexSqliteTests.SeedCandidateViaSqlAsync(dbContext, $"a-{Guid.NewGuid():N}", sessionId);

        await dbContext.Database.ExecuteSqlRawAsync(
            "INSERT INTO Payments (CandidateId, Reason, Amount, Status, CreatedUtc, ExpiredUnpaid, RefundRequested) VALUES ({0}, 0, 15, 1, {1}, 0, 0)",
            candidateId, Now);
        var paymentId = await dbContext.Payments.Select(p => p.Id).OrderByDescending(id => id).FirstAsync();

        return new Seed(teamId, sessionId, candidateId, paymentId);
    }

    [Fact]
    public async Task Migration_GivesEveryExistingTeamTheFourRulesThatReproduceTodaysBehaviour()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await using var dbContext = await OpenAsync(connection);

        await dbContext.GetService<IMigrator>().MigrateAsync(MigrationBeforeTheBackfill);
        var seed = await SeedAsync(dbContext);

        await dbContext.Database.MigrateAsync();

        var rules = await dbContext.MessageRules.AsNoTracking().Where(r => r.TeamId == seed.TeamId).ToListAsync();
        Assert.Equal(4, rules.Count);

        // The parameters are asserted because they are the behaviour: 24 hours, 5 days and 10 days,
        // in hours, matching the constants they replaced.
        Assert.Equal(24, rules.Single(r => r.Trigger == MessageTrigger.BeforeSessionStart).ParameterHours);
        Assert.Equal(120, rules.Single(r => r.Trigger == MessageTrigger.FccFeeOutstanding).ParameterHours);
        Assert.Equal(240, rules.Single(r => r.Trigger == MessageTrigger.PaymentUnpaid).ParameterHours);
        Assert.Null(rules.Single(r => r.Trigger == MessageTrigger.CandidateRegistered).ParameterHours);

        // The one message that was never candidate-facing.
        Assert.Equal(MessageRecipient.TeamAdminAddress, rules.Single(r => r.Trigger == MessageTrigger.PaymentUnpaid).Recipient);
        Assert.All(rules, r => Assert.True(r.IsEnabled));
        Assert.All(rules, r => Assert.Equal(MessageChannel.Email, r.Channel));
    }

    /// <summary>
    /// The guarantee the whole deploy rests on: a candidate already emailed gets a marker, so the
    /// first tick finds nothing to do. Asserted <i>through the scanner's own exclusion</i> would be
    /// indirect — this asserts the rows, because a missing row is the failure.
    /// </summary>
    [Fact]
    public async Task Migration_BackfillsAMarkerForEveryMessageAlreadySent()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await using var dbContext = await OpenAsync(connection);

        await dbContext.GetService<IMigrator>().MigrateAsync(MigrationBeforeTheBackfill);
        var seed = await SeedAsync(dbContext);

        await dbContext.Database.ExecuteSqlRawAsync(
            "UPDATE Candidates SET RegistrationConfirmationSentUtc = {0}, DayBeforeReminderSentUtc = {0}, FccFeeReminderSentUtc = {0} WHERE Id = {1}",
            Now.AddDays(-3), seed.CandidateId);
        await dbContext.Database.ExecuteSqlRawAsync("UPDATE Payments SET ExpiredUnpaid = 1 WHERE Id = {0}", seed.PaymentId);

        await dbContext.Database.MigrateAsync();

        var runs = await dbContext.MessageRuleRuns.AsNoTracking().Where(r => r.TeamId == seed.TeamId).ToListAsync();
        Assert.Equal(4, runs.Count);
        Assert.All(runs, r => Assert.Equal(MessageRuleOutcome.Sent, r.Outcome));

        foreach (var trigger in new[] { MessageTrigger.CandidateRegistered, MessageTrigger.BeforeSessionStart, MessageTrigger.FccFeeOutstanding })
        {
            var run = runs.Single(r => r.Trigger == trigger);
            Assert.Equal(MessageSubjectType.Candidate, run.SubjectType);
            Assert.Equal(seed.CandidateId, run.SubjectId);
            Assert.Equal(Now.AddDays(-3), run.FiredUtc);
        }

        // The expiration notice never had a timestamp column — ExpiredUnpaid *was* its idempotency
        // guard — so its marker points at the Payment and says where its time came from.
        var expiry = runs.Single(r => r.Trigger == MessageTrigger.PaymentUnpaid);
        Assert.Equal(MessageSubjectType.Payment, expiry.SubjectType);
        Assert.Equal(seed.PaymentId, expiry.SubjectId);
        Assert.Contains("ExpiredUnpaid", expiry.Detail);
    }

    /// <summary>Nothing sent yet means nothing to mark — and no marker standing in the way of the first real send.</summary>
    [Fact]
    public async Task Migration_LeavesACandidateWhoHasHadNothingUnmarked()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await using var dbContext = await OpenAsync(connection);

        await dbContext.GetService<IMigrator>().MigrateAsync(MigrationBeforeTheBackfill);
        var seed = await SeedAsync(dbContext);

        await dbContext.Database.MigrateAsync();

        Assert.Empty(await dbContext.MessageRuleRuns.AsNoTracking().Where(r => r.TeamId == seed.TeamId).ToListAsync());
    }

    /// <summary>
    /// The database, not the dispatcher, is what guarantees one marker per (rule, subject). Two
    /// overlapping Worker ticks are not hypothetical, and the thing being prevented is a duplicate
    /// email — so this is asserted against a real unique index rather than against the upsert that
    /// normally keeps it satisfied. EF InMemory enforces no index at all and would pass either way.
    /// </summary>
    [Fact]
    public async Task ASecondMarkerForTheSameRuleAndSubject_IsRejected()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await using var dbContext = await OpenAsync(connection);

        await dbContext.Database.MigrateAsync();
        var seed = await SeedAsync(dbContext);

        var rule = new MessageRule
        {
            TeamId = seed.TeamId, Name = "Registration confirmation", Trigger = MessageTrigger.CandidateRegistered,
            TemplateKey = "RegistrationConfirmation", CreatedUtc = Now
        };
        dbContext.MessageRules.Add(rule);
        await dbContext.SaveChangesAsync();

        dbContext.MessageRuleRuns.Add(NewRun(seed.TeamId, rule, seed.CandidateId));
        await dbContext.SaveChangesAsync();

        dbContext.MessageRuleRuns.Add(NewRun(seed.TeamId, rule, seed.CandidateId));
        await Assert.ThrowsAsync<DbUpdateException>(() => dbContext.SaveChangesAsync());
    }

    private static MessageRuleRun NewRun(int teamId, MessageRule rule, int subjectId) => new()
    {
        TeamId = teamId,
        MessageRuleId = rule.Id,
        RuleName = rule.Name,
        Trigger = rule.Trigger,
        SubjectType = MessageSubjectType.Candidate,
        SubjectId = subjectId,
        FiredUtc = Now,
        Outcome = MessageRuleOutcome.Sent
    };
}
