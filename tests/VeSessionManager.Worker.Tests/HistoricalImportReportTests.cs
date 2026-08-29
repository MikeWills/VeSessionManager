using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Ingestion;
using Xunit;

namespace VeSessionManager.Worker.Tests;

/// <summary>
/// The Worker's <c>--report-historical-imports</c> switch (#88) — a read-only report identifying
/// which existing <c>Session</c> rows were likely created by historical import before
/// <see cref="Session.ImportedHistoricallyUtc"/> existed, so an operator can review the list before
/// any backfill actually writes the flag. Mike's own call, asked directly: dry-run report first,
/// never write blind against production HRCC/MARC data.
///
/// <para><b>Two combinable, imperfect signals</b> — neither alone reliably identifies every
/// historically-imported session (see <c>docs/session-lifecycle-gate.md</c> for the full reasoning):
/// the audit trail's exact but incomplete <c>VecSubmissionMarked</c> entries, and a
/// <c>CreatedUtc</c>-vs-<c>ScheduledStartUtc</c> gap heuristic bounded by
/// <see cref="SessionIngestionService.CompletedSessionBackfillWindow"/>. This command reports the
/// union and says which signal(s) matched each row, never writing anything itself.</para>
///
/// <para><b>Real SQLite, not EF InMemory</b> — same reasoning as every other Worker test: this
/// command is a maintenance tool that will run against a real production database, and InMemory
/// cannot represent the FK/index behavior a real SQLite file has.</para>
/// </summary>
public class HistoricalImportReportTests
{
    private static readonly DateTime Now = new(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private static async Task<(SqliteConnection Connection, AppDbContext Context)> CreateAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await context.Database.EnsureCreatedAsync();
        return (connection, context);
    }

    private static async Task<Team> SeedTeamAsync(AppDbContext dbContext, string name = "TESTTEAM")
    {
        var team = new Team { Name = name, CreatedUtc = Now };
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        return team;
    }

    private static async Task<Session> SeedSessionAsync(
        AppDbContext dbContext, Team team, DateTime scheduledStartUtc, DateTime createdUtc, string examToolsSessionId)
    {
        var vec = new Vec { Name = "ARRL" };
        var user = new User { Name = "Sys", Email = $"s-{Guid.NewGuid():N}@localhost", Role = UserRole.SystemAdmin };
        var feeConfiguration = new FeeConfiguration
        {
            Vec = vec, EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            FeeCollectionEnabled = false, CreatedByUser = user, CreatedUtc = Now
        };
        dbContext.FeeConfigurations.Add(feeConfiguration);
        await dbContext.SaveChangesAsync();

        var session = new Session
        {
            ExamToolsSessionId = examToolsSessionId, Title = "Session", ScheduledStartUtc = scheduledStartUtc,
            DurationMinutes = 60, VecId = vec.Id, TeamId = team.Id, FeeConfigurationId = feeConfiguration.Id,
            Status = SessionStatus.Active,
            CreatedUtc = createdUtc
        };
        dbContext.Sessions.Add(session);
        await dbContext.SaveChangesAsync();
        return session;
    }

    [Fact]
    public async Task SessionWithAVecSubmissionMarkedAuditEntry_IsFlagged()
    {
        var (connection, dbContext) = await CreateAsync();
        await using var _ = connection;
        await using var __ = dbContext;
        var team = await SeedTeamAsync(dbContext);
        // Created same day as scheduled — the gap heuristic alone would miss this one; the audit
        // trail is what catches it.
        var session = await SeedSessionAsync(dbContext, team, scheduledStartUtc: Now.AddDays(-30), createdUtc: Now.AddDays(-30), examToolsSessionId: "s-audit");
        dbContext.AuditLogs.Add(new AuditLog
        {
            UserId = 1, Action = "VecSubmissionMarked", EntityType = nameof(Session), EntityId = session.Id,
            Details = "auto-marked", TimestampUtc = Now.AddDays(-30)
        });
        await dbContext.SaveChangesAsync();

        var output = new StringWriter();
        var exitCode = await HistoricalImportReport.RunAsync(dbContext, NullLogger.Instance, output, new FixedTimeProvider(Now));

        Assert.Equal(0, exitCode);
        Assert.Contains(session.ExamToolsSessionId, output.ToString());
        Assert.Contains("audit", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SessionCreatedLongAfterItsOwnScheduledDate_IsFlaggedByTheGapHeuristic()
    {
        var (connection, dbContext) = await CreateAsync();
        await using var _ = connection;
        await using var __ = dbContext;
        var team = await SeedTeamAsync(dbContext);
        // No audit entry at all (simulates a session that was already Submitted before the import
        // ran, so MarkVecSubmitted's early return wrote nothing) — only the creation gap catches it.
        var session = await SeedSessionAsync(
            dbContext, team, scheduledStartUtc: Now.AddDays(-200), createdUtc: Now.AddDays(-1), examToolsSessionId: "s-gap");

        var output = new StringWriter();
        await HistoricalImportReport.RunAsync(dbContext, NullLogger.Instance, output, new FixedTimeProvider(Now));

        Assert.Contains(session.ExamToolsSessionId, output.ToString());
        Assert.Contains("gap", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ARealSessionCreatedPromptly_IsNotFlagged()
    {
        var (connection, dbContext) = await CreateAsync();
        await using var _ = connection;
        await using var __ = dbContext;
        var team = await SeedTeamAsync(dbContext);
        // Created the same day it was scheduled, no audit trail — an ordinary session ingested the
        // normal way.
        await SeedSessionAsync(dbContext, team, scheduledStartUtc: Now.AddDays(-2), createdUtc: Now.AddDays(-6), examToolsSessionId: "s-real");

        var output = new StringWriter();
        await HistoricalImportReport.RunAsync(dbContext, NullLogger.Instance, output, new FixedTimeProvider(Now));

        Assert.DoesNotContain("s-real", output.ToString());
    }

    /// <summary>A session already stamped by #88's real field needs no report line — it's already handled, not a backfill candidate.</summary>
    [Fact]
    public async Task ASessionAlreadyStampedImportedHistoricallyUtc_IsNotFlaggedAgain()
    {
        var (connection, dbContext) = await CreateAsync();
        await using var _ = connection;
        await using var __ = dbContext;
        var team = await SeedTeamAsync(dbContext);
        var session = await SeedSessionAsync(dbContext, team, scheduledStartUtc: Now.AddDays(-200), createdUtc: Now.AddDays(-1), examToolsSessionId: "s-already-flagged");
        session.ImportedHistoricallyUtc = Now.AddDays(-1);
        await dbContext.SaveChangesAsync();

        var output = new StringWriter();
        await HistoricalImportReport.RunAsync(dbContext, NullLogger.Instance, output, new FixedTimeProvider(Now));

        Assert.DoesNotContain("s-already-flagged", output.ToString());
    }

    [Fact]
    public async Task NeverWritesAnything()
    {
        var (connection, dbContext) = await CreateAsync();
        await using var _ = connection;
        await using var __ = dbContext;
        var team = await SeedTeamAsync(dbContext);
        await SeedSessionAsync(dbContext, team, scheduledStartUtc: Now.AddDays(-200), createdUtc: Now.AddDays(-1), examToolsSessionId: "s-gap");

        await HistoricalImportReport.RunAsync(dbContext, NullLogger.Instance, new StringWriter(), new FixedTimeProvider(Now));

        Assert.Null((await dbContext.Sessions.SingleAsync()).ImportedHistoricallyUtc);
    }
}
