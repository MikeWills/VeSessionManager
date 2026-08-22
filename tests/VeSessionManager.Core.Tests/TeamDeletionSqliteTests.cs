using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.VecSubmissions;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Deleting a team, and everything it owns.
///
/// <para><b>Real SQLite, and it has to be.</b> Thirteen of this team's child tables are
/// <c>Restrict</c>, which is the entire difficulty — a missed table throws on save. EF's in-memory
/// provider does not enforce foreign keys at all, so a test on it would pass with the deletion order
/// wrong and prove nothing. Same trap recorded for #233/#234, both of which shipped with tests that
/// could not fail.</para>
///
/// <para>The counterpart is <see cref="TeamDeletionCoverageTests"/>, which reads the model rather
/// than the data: it fails when a new team-scoped table is added and nobody teaches the service about
/// it. This file proves the delete works today; that one proves it keeps working.</para>
/// </summary>
public class TeamDeletionSqliteTests
{
    private static readonly DateTime Now = new(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private static async Task<(SqliteConnection Connection, AppDbContext Context)> CreateAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var dbContext = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await dbContext.Database.EnsureCreatedAsync();
        return (connection, dbContext);
    }

    private static TeamDeletionService NewService(AppDbContext dbContext, string? archiveRoot = null) =>
        new(dbContext,
            new ArrlSubmissionArchiveStore(
                Options.Create(new ArrlSubmissionOptions { ArchiveRootPath = archiveRoot }),
                NullLogger<ArrlSubmissionArchiveStore>.Instance),
            new FixedTimeProvider(Now),
            NullLogger<TeamDeletionService>.Instance);

    /// <summary>
    /// A team with one of nearly everything hanging off it, so the delete has to walk the whole graph
    /// rather than the two or three tables a hand-written fixture usually remembers.
    /// </summary>
    private static async Task<(Team Team, User Admin, Vec Vec)> SeedFullTeamAsync(AppDbContext dbContext, string name = "TEAMA")
    {
        var admin = await dbContext.Users.FirstOrDefaultAsync(u => u.Email == "admin@example.org");
        if (admin is null)
        {
            admin = new User { Name = "Admin", Email = "admin@example.org", Role = UserRole.SystemAdmin };
            dbContext.Users.Add(admin);
        }

        var vec = await dbContext.Vecs.FirstOrDefaultAsync(v => v.Name == "ARRL");
        if (vec is null)
        {
            vec = new Vec { Name = "ARRL" };
            dbContext.Vecs.Add(vec);
        }

        await dbContext.SaveChangesAsync();

        var feeConfiguration = await dbContext.FeeConfigurations.FirstOrDefaultAsync();
        if (feeConfiguration is null)
        {
            feeConfiguration = new FeeConfiguration
            {
                Vec = vec,
                EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                FeeCollectionEnabled = true,
                ExamFeeAmount = 15m,
                CreatedByUser = admin,
                CreatedUtc = Now
            };
            dbContext.FeeConfigurations.Add(feeConfiguration);
            await dbContext.SaveChangesAsync();
        }

        var team = new Team { Name = name, ExamToolsTeamCode = name.ToLowerInvariant(), CreatedUtc = Now };
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

        var session = new Session
        {
            ExamToolsSessionId = $"{name}-session-1",
            Title = "August Session",
            ScheduledStartUtc = Now.AddDays(3),
            DurationMinutes = 60,
            VecId = vec.Id,
            TeamId = team.Id,
            FeeConfigurationId = feeConfiguration.Id,
            Status = SessionStatus.Active
        };
        dbContext.Sessions.Add(session);
        await dbContext.SaveChangesAsync();

        var candidate = new Candidate
        {
            SessionId = session.Id,
            ExamToolsApplicantId = $"{name}-applicant-1",
            Name = "Roana Glory",
            Email = "roana@example.org"
        };
        dbContext.Candidates.Add(candidate);
        await dbContext.SaveChangesAsync();

        var payment = new Payment
        {
            CandidateId = candidate.Id,
            Amount = 15m,
            Status = PaymentStatus.Paid,
            Reason = PaymentReason.InitialExam
        };
        dbContext.Payments.Add(payment);
        await dbContext.SaveChangesAsync();

        dbContext.Refunds.Add(new Refund
        {
            TeamId = team.Id,
            PaymentId = payment.Id,
            SquarePaymentId = "sq-payment-1",
            SquareIdempotencyKey = Guid.NewGuid().ToString(),
            AmountUsd = 5m,
            RequestedByUserId = admin.Id,
            Status = RefundStatus.Submitting
        });

        dbContext.CandidateEmailSends.Add(new CandidateEmailSend
        {
            CandidateId = candidate.Id,
            TemplateLabel = "Getting started locally",
            SentUtc = Now,
            SentByUserId = admin.Id
        });

        var messageRule = new MessageRule
        {
            TeamId = team.Id,
            Name = "Registration confirmation",
            Trigger = MessageTrigger.CandidateRegistered,
            Subject = "Registered",
            Body = "<p>Hi</p>",
            CreatedUtc = Now
        };
        dbContext.MessageRules.Add(messageRule);
        await dbContext.SaveChangesAsync();

        dbContext.MessageRuleRuns.Add(new MessageRuleRun
        {
            TeamId = team.Id,
            MessageRuleId = messageRule.Id,
            RuleName = messageRule.Name,
            Trigger = messageRule.Trigger,
            SubjectType = MessageSubjectType.Candidate,
            SubjectId = candidate.Id,
            Outcome = MessageRuleOutcome.Sent,
            FiredUtc = Now
        });

        var ve = new VolunteerExaminer { CallSign = $"{name}0VE", Name = "Sam Vale" };
        dbContext.VolunteerExaminers.Add(ve);
        await dbContext.SaveChangesAsync();

        dbContext.VeTeamMemberships.Add(new VeTeamMembership { TeamId = team.Id, VolunteerExaminerId = ve.Id, CreatedUtc = Now });
        dbContext.SessionVolunteerExaminers.Add(new SessionVolunteerExaminer { SessionId = session.Id, VolunteerExaminerId = ve.Id });
        dbContext.UserTeams.Add(new UserTeam { TeamId = team.Id, UserId = admin.Id });
        dbContext.WatchedLicenses.Add(new WatchedLicense { TeamId = team.Id, CallSign = "KE0ABC", AddedByUserId = admin.Id });
        dbContext.JobRunHistories.Add(new JobRunHistory { TeamId = team.Id, JobName = "Ingestion", StartedUtc = Now, Success = true });
        dbContext.AddAuditLog(admin.Id, "SomethingHappened", nameof(Session), session.Id, "details", Now, teamId: team.Id);
        await dbContext.SaveChangesAsync();

        return (team, admin, vec);
    }

    [Fact]
    public async Task DeletingATeam_RemovesEverythingItOwns()
    {
        var (connection, dbContext) = await CreateAsync();
        await using var _ = connection;
        await using var __ = dbContext;
        var (team, admin, _) = await SeedFullTeamAsync(dbContext);

        var (result, summary) = await NewService(dbContext).DeleteAsync(team.Id, admin.Id, CancellationToken.None);

        Assert.Equal(TeamActionResult.Success, result);
        Assert.NotNull(summary);
        Assert.Null(await dbContext.Teams.FirstOrDefaultAsync(t => t.Id == team.Id));

        Assert.Empty(await dbContext.Sessions.Where(x => x.TeamId == team.Id).ToListAsync());
        Assert.Empty(await dbContext.Candidates.ToListAsync());
        Assert.Empty(await dbContext.Payments.ToListAsync());
        Assert.Empty(await dbContext.Refunds.ToListAsync());
        Assert.Empty(await dbContext.CandidateEmailSends.ToListAsync());
        Assert.Empty(await dbContext.MessageRules.ToListAsync());
        Assert.Empty(await dbContext.MessageRuleRuns.ToListAsync());
        Assert.Empty(await dbContext.EmailSettings.ToListAsync());
        Assert.Empty(await dbContext.VeTeamMemberships.ToListAsync());
        Assert.Empty(await dbContext.SessionVolunteerExaminers.ToListAsync());
        Assert.Empty(await dbContext.UserTeams.ToListAsync());
        Assert.Empty(await dbContext.WatchedLicenses.ToListAsync());
        Assert.Empty(await dbContext.JobRunHistories.ToListAsync());
    }

    /// <summary>
    /// VEC and fee configuration are <b>parents</b> of a team, not children (docs/multi-team.md), and
    /// user accounts are people rather than team property. Deleting a team must not take any of them.
    /// </summary>
    [Fact]
    public async Task DeletingATeam_LeavesSharedRecordsAlone()
    {
        var (connection, dbContext) = await CreateAsync();
        await using var _ = connection;
        await using var __ = dbContext;
        var (team, admin, vec) = await SeedFullTeamAsync(dbContext);

        await NewService(dbContext).DeleteAsync(team.Id, admin.Id, CancellationToken.None);

        Assert.NotNull(await dbContext.Vecs.FirstOrDefaultAsync(v => v.Id == vec.Id));
        Assert.NotEmpty(await dbContext.FeeConfigurations.ToListAsync());
        Assert.NotNull(await dbContext.Users.FirstOrDefaultAsync(u => u.Id == admin.Id));
    }

    /// <summary>
    /// A second team must be untouched — the reason every query in the service is team-scoped rather
    /// than a convenient "delete all the candidates" sweep.
    /// </summary>
    [Fact]
    public async Task DeletingOneTeam_LeavesAnotherTeamsDataIntact()
    {
        var (connection, dbContext) = await CreateAsync();
        await using var _ = connection;
        await using var __ = dbContext;
        var (teamA, admin, _) = await SeedFullTeamAsync(dbContext, "TEAMA");
        var (teamB, _, _) = await SeedFullTeamAsync(dbContext, "TEAMB");

        await NewService(dbContext).DeleteAsync(teamA.Id, admin.Id, CancellationToken.None);

        Assert.NotNull(await dbContext.Teams.FirstOrDefaultAsync(t => t.Id == teamB.Id));
        Assert.Single(await dbContext.Sessions.ToListAsync());
        Assert.Single(await dbContext.Candidates.ToListAsync());
        Assert.Single(await dbContext.MessageRules.ToListAsync());
        Assert.Single(await dbContext.VeTeamMemberships.ToListAsync());
    }

    /// <summary>Mike, 2026-08-21: "If the VE is only linked to the team being deleted, delete them."</summary>
    [Fact]
    public async Task AVeWhoseOnlyTeamIsThisOne_IsDeleted()
    {
        var (connection, dbContext) = await CreateAsync();
        await using var _ = connection;
        await using var __ = dbContext;
        var (team, admin, _) = await SeedFullTeamAsync(dbContext);

        var (_, summary) = await NewService(dbContext).DeleteAsync(team.Id, admin.Id, CancellationToken.None);

        Assert.Empty(await dbContext.VolunteerExaminers.ToListAsync());
        Assert.Equal(1, summary!.VolunteerExaminersDeleted);
    }

    /// <summary>
    /// The other half of the same rule, and the more important one: a VE is a person, not team
    /// property. Someone examining for two clubs keeps existing when one of them is deleted — they
    /// just lose that membership.
    /// </summary>
    [Fact]
    public async Task AVeOnAnotherTeamToo_SurvivesWithoutThatMembership()
    {
        var (connection, dbContext) = await CreateAsync();
        await using var _ = connection;
        await using var __ = dbContext;
        var (teamA, admin, _) = await SeedFullTeamAsync(dbContext, "TEAMA");
        var (teamB, _, _) = await SeedFullTeamAsync(dbContext, "TEAMB");

        // One person, on both teams.
        var ve = await dbContext.VolunteerExaminers.FirstAsync(v => v.CallSign == "TEAMA0VE");
        dbContext.VeTeamMemberships.Add(new VeTeamMembership { TeamId = teamB.Id, VolunteerExaminerId = ve.Id, CreatedUtc = Now });
        await dbContext.SaveChangesAsync();

        await NewService(dbContext).DeleteAsync(teamA.Id, admin.Id, CancellationToken.None);

        Assert.NotNull(await dbContext.VolunteerExaminers.FirstOrDefaultAsync(v => v.Id == ve.Id));
        Assert.Empty(await dbContext.VeTeamMemberships.Where(m => m.VolunteerExaminerId == ve.Id && m.TeamId == teamA.Id).ToListAsync());
        Assert.Single(await dbContext.VeTeamMemberships.Where(m => m.VolunteerExaminerId == ve.Id).ToListAsync());
    }

    /// <summary>
    /// ⚠️ A VE whose only membership is this team can still be linked to a surviving user account
    /// (<c>User.VolunteerExaminerId</c>, a Restrict FK with a unique index). Deleting them would
    /// either throw or strand an account, so the sole-membership rule does not apply to them.
    /// </summary>
    [Fact]
    public async Task AVeLinkedToASurvivingUserAccount_IsKept()
    {
        var (connection, dbContext) = await CreateAsync();
        await using var _ = connection;
        await using var __ = dbContext;
        var (team, admin, _) = await SeedFullTeamAsync(dbContext);

        var ve = await dbContext.VolunteerExaminers.FirstAsync();
        admin.VolunteerExaminerId = ve.Id;
        await dbContext.SaveChangesAsync();

        var (result, summary) = await NewService(dbContext).DeleteAsync(team.Id, admin.Id, CancellationToken.None);

        Assert.Equal(TeamActionResult.Success, result);
        Assert.NotNull(await dbContext.VolunteerExaminers.FirstOrDefaultAsync(v => v.Id == ve.Id));
        Assert.Equal(0, summary!.VolunteerExaminersDeleted);
    }

    /// <summary>
    /// The deletion is recorded, and the record survives — which only works because the entry carries
    /// no <c>TeamId</c>. Attributed to the team it describes, it would be caught by the same sweep
    /// that clears the team's own audit rows and delete itself.
    /// </summary>
    [Fact]
    public async Task TheDeletionIsAudited_AndThatEntryOutlivesTheTeam()
    {
        var (connection, dbContext) = await CreateAsync();
        await using var _ = connection;
        await using var __ = dbContext;
        var (team, admin, _) = await SeedFullTeamAsync(dbContext);

        await NewService(dbContext).DeleteAsync(team.Id, admin.Id, CancellationToken.None);

        var entry = Assert.Single(await dbContext.AuditLogs.Where(a => a.Action == "TeamDeleted").ToListAsync());
        Assert.Null(entry.TeamId);
        Assert.Equal(team.Id, entry.EntityId);
        Assert.Contains("TEAMA", entry.Details);

        // And the team's own rows went with it, so this is the only one left.
        Assert.Empty(await dbContext.AuditLogs.Where(a => a.TeamId == team.Id).ToListAsync());
    }

    [Fact]
    public async Task DeletingATeamThatDoesNotExist_IsNotFound()
    {
        var (connection, dbContext) = await CreateAsync();
        await using var _ = connection;
        await using var __ = dbContext;

        var (result, summary) = await NewService(dbContext).DeleteAsync(999_999, 1, CancellationToken.None);

        Assert.Equal(TeamActionResult.NotFound, result);
        Assert.Null(summary);
    }

    /// <summary>
    /// The archive files are the one part of this that is not a database row, so nothing rolls them
    /// back. They go first and deliberately: a file left behind after the row naming it is gone is
    /// unreachable forever, whereas a failed delete after the files are removed is a retry.
    /// </summary>
    [Fact]
    public async Task ArrlArchiveFilesOnDisk_AreDeletedWithTheTeam()
    {
        var (connection, dbContext) = await CreateAsync();
        await using var _ = connection;
        await using var __ = dbContext;
        var (team, admin, vec) = await SeedFullTeamAsync(dbContext);

        var root = Path.Combine(Path.GetTempPath(), "vesm-team-delete-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "teama"));
        var archivePath = Path.Combine("teama", "archive.zip");
        await File.WriteAllTextAsync(Path.Combine(root, archivePath), "zip bytes");

        var session = await dbContext.Sessions.FirstAsync(s => s.TeamId == team.Id);
        dbContext.ArrlVecSubmissions.Add(new ArrlVecSubmission
        {
            TeamId = team.Id,
            SessionId = session.Id,
            FullName = "Sam Vale",
            CallSign = "TEAMA0VE",
            Email = "sam@example.org",
            Phone = "555-0100",
            SessionDate = "2026-08-24",
            Location = "Mankato, MN",
            AmountCharged = "15.00",
            ArchiveFileName = "archive.zip",
            ArchiveStoredPath = archivePath,
            SubmittedUtc = Now,
            SubmittedByUserId = admin.Id,
            Outcome = ArrlReceiptOutcome.Succeeded
        });
        await dbContext.SaveChangesAsync();

        var (result, summary) = await NewService(dbContext, root).DeleteAsync(team.Id, admin.Id, CancellationToken.None);

        Assert.Equal(TeamActionResult.Success, result);
        Assert.False(File.Exists(Path.Combine(root, archivePath)));
        Assert.Empty(await dbContext.ArrlVecSubmissions.ToListAsync());
        Assert.Equal(1, summary!.ArchiveFilesDeleted);

        Directory.Delete(root, recursive: true);
    }
}
