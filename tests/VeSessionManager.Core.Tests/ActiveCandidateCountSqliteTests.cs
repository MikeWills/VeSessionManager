using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// The Sessions list counts candidates as
/// <c>s.Candidates.Count(c =&gt; c.ApplicationStatus != NotTested)</c> so a session that has lost
/// people doesn't read as fuller than it is.
///
/// <para><b>Pinned against real SQLite, not InMemory.</b> That list pages server-side, so the
/// expression has to translate to SQL — and InMemory evaluates predicates as plain LINQ, so it would
/// happily pass a query that throws in production. Same reasoning as
/// <see cref="VecExamToolsCodeSqliteTests"/>.</para>
/// </summary>
public class ActiveCandidateCountSqliteTests
{
    private static readonly DateTime Now = new(2026, 8, 6, 12, 0, 0, DateTimeKind.Utc);

    private static async Task<(SqliteConnection Connection, AppDbContext Context)> CreateSqliteContextAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(); // an in-memory DB lives only as long as its connection
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var context = new AppDbContext(options);
        await context.Database.EnsureCreatedAsync();
        return (connection, context);
    }

    private static async Task<Session> SeedSessionAsync(AppDbContext dbContext, params CandidateApplicationStatus[] statuses)
    {
        var vec = new Vec { Name = "VEC-" + Guid.NewGuid().ToString("N")[..8] };
        var team = new Team { Name = "TEAM-" + Guid.NewGuid().ToString("N")[..8], CreatedUtc = Now };
        // Real SQLite enforces the FKs InMemory ignores: a Session needs a FeeConfiguration, which
        // needs a Vec and a creating User.
        var user = new User { Name = "System", Email = $"sys-{Guid.NewGuid():N}@localhost", Role = UserRole.SystemAdmin };
        var feeConfiguration = new FeeConfiguration
        {
            Vec = vec,
            EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            FeeCollectionEnabled = true,
            ExamFeeAmount = 15m,
            CreatedByUser = user,
            CreatedUtc = Now
        };
        var session = new Session
        {
            ExamToolsSessionId = "s-" + Guid.NewGuid().ToString("N")[..8],
            Title = "Session",
            ScheduledStartUtc = Now.AddDays(7),
            DurationMinutes = 120,
            Vec = vec,
            Team = team,
            FeeConfiguration = feeConfiguration,
            Status = SessionStatus.Active,
            CreatedUtc = Now
        };

        var i = 0;
        foreach (var status in statuses)
        {
            session.Candidates.Add(new Candidate
            {
                ExamToolsApplicantId = $"a{i++}",
                Name = "Someone",
                DateRegisteredUtc = Now,
                ApplicationStatus = status
            });
        }

        dbContext.Sessions.Add(session);
        await dbContext.SaveChangesAsync();
        return session;
    }

    [Fact]
    public async Task ActiveCandidateCount_TranslatesToSql_AndExcludesWithdrawn()
    {
        var (connection, dbContext) = await CreateSqliteContextAsync();
        await using var _ = connection;
        await using var __ = dbContext;

        await SeedSessionAsync(
            dbContext,
            CandidateApplicationStatus.Unmatched,
            CandidateApplicationStatus.Granted,
            CandidateApplicationStatus.NotTested,   // withdrawn / moved away
            CandidateApplicationStatus.NotTested);

        // Projected, not materialised first: this is the shape the paged Sessions query uses, so if
        // it can't translate, this is where it throws.
        var counts = await dbContext.Sessions
            .Select(s => s.Candidates.Count(c => c.ApplicationStatus != CandidateApplicationStatus.NotTested))
            .ToListAsync();

        Assert.Equal(2, Assert.Single(counts));
    }

    /// <summary>The same expression as an ORDER BY key — the Sessions list sorts on it too.</summary>
    [Fact]
    public async Task OrderingByActiveCandidateCount_TranslatesToSql()
    {
        var (connection, dbContext) = await CreateSqliteContextAsync();
        await using var _ = connection;
        await using var __ = dbContext;

        await SeedSessionAsync(dbContext, CandidateApplicationStatus.NotTested);                          // 0 active
        await SeedSessionAsync(dbContext, CandidateApplicationStatus.Unmatched, CandidateApplicationStatus.Received); // 2 active
        await SeedSessionAsync(dbContext, CandidateApplicationStatus.Granted);                            // 1 active

        var ordered = await dbContext.Sessions
            .OrderByDescending(s => s.Candidates.Count(c => c.ApplicationStatus != CandidateApplicationStatus.NotTested))
            .Select(s => s.Candidates.Count(c => c.ApplicationStatus != CandidateApplicationStatus.NotTested))
            .ToListAsync();

        Assert.Equal([2, 1, 0], ordered);
    }

    /// <summary>A session whose every candidate has left reads as empty, not as its historical headcount.</summary>
    [Fact]
    public async Task SessionWhereEveryoneWithdrew_CountsZero()
    {
        var (connection, dbContext) = await CreateSqliteContextAsync();
        await using var _ = connection;
        await using var __ = dbContext;

        await SeedSessionAsync(dbContext, CandidateApplicationStatus.NotTested, CandidateApplicationStatus.NotTested);

        var count = await dbContext.Sessions
            .Select(s => s.Candidates.Count(c => c.ApplicationStatus != CandidateApplicationStatus.NotTested))
            .SingleAsync();

        Assert.Equal(0, count);
    }
}
