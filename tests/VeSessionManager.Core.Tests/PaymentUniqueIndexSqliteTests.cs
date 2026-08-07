using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// The unique index on Payments (CandidateId, Reason) filtered to <c>"Reason" = 0</c> (InitialExam)
/// is pure provider behaviour: EF InMemory enforces neither unique indexes nor index filters, so an
/// InMemory test here would pass whether or not the index existed at all. Everything in this file
/// therefore runs against real SQLite — the same provider production runs on — following the
/// <see cref="VecExamToolsCodeSqliteTests"/> pattern.
/// </summary>
public class PaymentUniqueIndexSqliteTests
{
    private static readonly DateTime Now = new(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Schema built from the current model (EnsureCreated), which includes the filtered unique index.</summary>
    private static async Task<(SqliteConnection Connection, AppDbContext Context)> CreateSqliteContextAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(); // an in-memory DB lives only as long as its connection
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var context = new AppDbContext(options);
        await context.Database.EnsureCreatedAsync();
        return (connection, context);
    }

    /// <summary>Seeds Vec/User/FeeConfiguration/Team/Session and returns a saved Candidate.</summary>
    /// <param name="existingUserId">
    /// A user inserted by the caller, for tests pinned to an older migration. Same reason as
    /// <c>existingTeamId</c>: seeding through EF uses the CURRENT model, so any column added to
    /// AspNetUsers later fails against a schema that predates it. Adding
    /// <c>User.MustChangePassword</c> broke these two tests exactly that way (2026-08-07).
    /// </param>
    internal static async Task<Candidate> SeedCandidateAsync(AppDbContext dbContext, string applicantId, Session? session = null, int? existingTeamId = null, int? existingUserId = null)
    {
        if (session is null)
        {
            var vec = new Vec { Name = $"VEC-{applicantId}" };
            User? user = existingUserId is null
                ? new User { Name = "System", Email = $"system-{applicantId}@localhost", Role = UserRole.SystemAdmin }
                : null;
            Team? team = existingTeamId is null ? new Team { Name = $"TEAM-{applicantId}", CreatedUtc = Now } : null;
            session = new Session
            {
                ExamToolsSessionId = $"session-{applicantId}",
                Title = "August Session",
                ScheduledStartUtc = Now.AddDays(4),
                DurationMinutes = 60,
                Vec = vec,
                FeeConfiguration = new FeeConfiguration
                {
                    Vec = vec,
                    EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    FeeCollectionEnabled = true,
                    ExamFeeAmount = 15m,
                    CreatedUtc = Now
                },
                Status = SessionStatus.Active,
                CreatedUtc = Now
            };

            if (team is not null) session.Team = team; else session.TeamId = existingTeamId!.Value;
            if (user is not null) session.FeeConfiguration.CreatedByUser = user;
            else session.FeeConfiguration.CreatedByUserId = existingUserId!.Value;
        }

        var candidate = new Candidate
        {
            ExamToolsApplicantId = applicantId,
            Session = session,
            Name = "Roana Glory",
            Email = $"{applicantId}@example.com",
            DateRegisteredUtc = Now
        };
        dbContext.Candidates.Add(candidate);
        await dbContext.SaveChangesAsync();
        return candidate;
    }

    private static Payment NewPayment(int candidateId, PaymentReason reason) => new()
    {
        CandidateId = candidateId,
        Reason = reason,
        Amount = 15m,
        Status = PaymentStatus.Unpaid,
        CreatedUtc = Now
    };

    /// <summary>
    /// The core proof: the Web-vs-Worker race that produced two Unpaid rows, two live Square links
    /// and two reminder emails for one candidate is now a database-level constraint violation.
    /// </summary>
    [Fact]
    public async Task SecondInitialExamPaymentForSameCandidate_ViolatesUniqueIndex()
    {
        var (connection, dbContext) = await CreateSqliteContextAsync();
        await using var _ = connection;
        await using var __ = dbContext;
        var candidate = await SeedCandidateAsync(dbContext, "applicant-1");

        dbContext.Payments.Add(NewPayment(candidate.Id, PaymentReason.InitialExam));
        await dbContext.SaveChangesAsync();

        dbContext.Payments.Add(NewPayment(candidate.Id, PaymentReason.InitialExam));
        await Assert.ThrowsAsync<DbUpdateException>(() => dbContext.SaveChangesAsync());
    }

    /// <summary>
    /// Proves the index filter is real and this is not a blanket unique index: a candidate who fails
    /// and retests more than once legitimately owes a second (and third) retest fee.
    /// </summary>
    [Fact]
    public async Task TwoRetestPaymentsForSameCandidate_AreAllowedByTheIndexFilter()
    {
        var (connection, dbContext) = await CreateSqliteContextAsync();
        await using var _ = connection;
        await using var __ = dbContext;
        var candidate = await SeedCandidateAsync(dbContext, "applicant-1");

        dbContext.Payments.Add(NewPayment(candidate.Id, PaymentReason.Retest));
        await dbContext.SaveChangesAsync();
        dbContext.Payments.Add(NewPayment(candidate.Id, PaymentReason.Retest));
        await dbContext.SaveChangesAsync();

        Assert.Equal(2, await dbContext.Payments.CountAsync(p => p.Reason == PaymentReason.Retest));
    }

    [Fact]
    public async Task InitialExamAndRetestForSameCandidate_AreAllowed()
    {
        var (connection, dbContext) = await CreateSqliteContextAsync();
        await using var _ = connection;
        await using var __ = dbContext;
        var candidate = await SeedCandidateAsync(dbContext, "applicant-1");

        dbContext.Payments.Add(NewPayment(candidate.Id, PaymentReason.InitialExam));
        await dbContext.SaveChangesAsync();
        dbContext.Payments.Add(NewPayment(candidate.Id, PaymentReason.Retest));
        await dbContext.SaveChangesAsync();

        Assert.Equal(2, await dbContext.Payments.CountAsync(p => p.CandidateId == candidate.Id));
    }

    [Fact]
    public async Task InitialExamPaymentsForDifferentCandidates_AreAllowed()
    {
        var (connection, dbContext) = await CreateSqliteContextAsync();
        await using var _ = connection;
        await using var __ = dbContext;
        var candidateA = await SeedCandidateAsync(dbContext, "applicant-1");
        var candidateB = await SeedCandidateAsync(dbContext, "applicant-2");

        dbContext.Payments.AddRange(
            NewPayment(candidateA.Id, PaymentReason.InitialExam),
            NewPayment(candidateB.Id, PaymentReason.InitialExam));
        await dbContext.SaveChangesAsync();

        Assert.Equal(2, await dbContext.Payments.CountAsync());
    }

    // ---- The migration's own dedupe SQL ----

    private const string MigrationBeforeTheIndex = "20260801154402_PasswordResetAndSystemEmail";

    /// <summary>
    /// Inserts a Team with raw SQL, naming only the columns that existed at
    /// <see cref="MigrationBeforeTheIndex"/>.
    ///
    /// <para><b>Why not just use the model.</b> These two tests deliberately run against a
    /// *historical* schema, but the DbContext is always the *current* model — so the moment anyone
    /// adds a column to Team, EF emits an INSERT naming it and SQLite rejects the whole test with
    /// "table Teams has no column named X". That is exactly what happened when Team gained its logo
    /// columns. Any table seeded by a migration test has the same hazard; this is the seam where it
    /// bites first because Team is the most-extended entity.</para>
    /// </summary>
    /// <summary>
    /// Inserts a user with raw SQL, listing only the columns the pinned migration knows about — the
    /// same trick as <see cref="SeedTeamViaSqlAsync"/>. EF would insert every column the current
    /// model has, which fails on a schema deliberately held at an older migration.
    /// </summary>
    private static async Task<int> SeedUserViaSqlAsync(AppDbContext dbContext, string email)
    {
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO AspNetUsers
                (UserName, NormalizedUserName, Email, NormalizedEmail, EmailConfirmed, Name, Role,
                 PhoneNumberConfirmed, TwoFactorEnabled, LockoutEnabled, AccessFailedCount, SecurityStamp, ConcurrencyStamp)
            VALUES ({0}, {1}, {0}, {1}, 1, 'System', 0, 0, 0, 0, 0, {2}, {2})
            """,
            email, email.ToUpperInvariant(), Guid.NewGuid().ToString());

        return await dbContext.Users.Select(u => u.Id).OrderByDescending(id => id).FirstAsync();
    }

    private static async Task<int> SeedTeamViaSqlAsync(AppDbContext dbContext, string name)
    {
        await dbContext.Database.ExecuteSqlRawAsync(
            "INSERT INTO Teams (Name, CreatedUtc, PurgeUnpaidLinkDays, ZoomBreakoutRoomCount) VALUES ({0}, {1}, 0, 0)",
            name, Now);
        return await dbContext.Teams.Select(t => t.Id).OrderByDescending(id => id).FirstAsync();
    }

    /// <summary>
    /// Both Web and Worker call Database.Migrate() at startup, so CreateIndex failing on a database
    /// that already holds duplicates is not "a migration didn't apply" — it is "the deployment will
    /// not boot". This walks the real upgrade path: migrate up to the migration *before* the index,
    /// seed the exact duplicate shape the buggy code produced, then apply the rest.
    /// </summary>
    [Fact]
    public async Task Migration_RemovesInertDuplicateInitialExamPayments_KeepingTheOldestRow()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var _ = connection;
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var dbContext = new AppDbContext(options);

        await dbContext.GetService<IMigrator>().MigrateAsync(MigrationBeforeTheIndex);
        var teamId = await SeedTeamViaSqlAsync(dbContext, "TEAM-applicant-1");
        var userId = await SeedUserViaSqlAsync(dbContext, "system-applicant-1@localhost");
        var candidate = await SeedCandidateAsync(dbContext, "applicant-1", existingTeamId: teamId, existingUserId: userId);

        // Two provably inert duplicates: Unpaid, never linked, never given a Square order id.
        dbContext.Payments.AddRange(
            NewPayment(candidate.Id, PaymentReason.InitialExam),
            NewPayment(candidate.Id, PaymentReason.InitialExam));
        await dbContext.SaveChangesAsync();
        var ids = await dbContext.Payments.Select(p => p.Id).OrderBy(id => id).ToListAsync();
        Assert.Equal(2, ids.Count); // no index yet, so the duplicate really did get in

        await dbContext.Database.MigrateAsync();

        dbContext.ChangeTracker.Clear();
        var survivor = Assert.Single(await dbContext.Payments.ToListAsync());
        Assert.Equal(ids[0], survivor.Id); // MIN(Id) kept, higher-Id duplicate removed
    }

    /// <summary>
    /// The deliberate other half: a duplicate that was ever linked or paid means two live checkout
    /// links existed for one candidate and money may have moved twice. The migration must NOT delete
    /// that — it must fail loudly so a human looks at it.
    /// </summary>
    [Fact]
    public async Task Migration_LeavesALinkedDuplicateInPlace_AndFailsLoudly()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var _ = connection;
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var dbContext = new AppDbContext(options);

        await dbContext.GetService<IMigrator>().MigrateAsync(MigrationBeforeTheIndex);
        var teamId = await SeedTeamViaSqlAsync(dbContext, "TEAM-applicant-1");
        var userId = await SeedUserViaSqlAsync(dbContext, "system-applicant-1@localhost");
        var candidate = await SeedCandidateAsync(dbContext, "applicant-1", existingTeamId: teamId, existingUserId: userId);

        var first = NewPayment(candidate.Id, PaymentReason.InitialExam);
        var duplicate = NewPayment(candidate.Id, PaymentReason.InitialExam);
        duplicate.PaymentLinkUrl = "https://square.link/u/order-5001"; // someone could have paid this
        dbContext.Payments.AddRange(first, duplicate);
        await dbContext.SaveChangesAsync();

        await Assert.ThrowsAnyAsync<Exception>(() => dbContext.Database.MigrateAsync());

        dbContext.ChangeTracker.Clear();
        Assert.Equal(2, await dbContext.Payments.CountAsync()); // evidence preserved
    }
}
