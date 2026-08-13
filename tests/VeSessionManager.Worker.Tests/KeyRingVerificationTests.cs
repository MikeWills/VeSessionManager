using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Worker.Tests;

/// <summary>
/// The Worker's <c>--verify-keyring</c> switch. <see cref="DataProtectionKeyRingGuard"/> has its own
/// tests; these cover the three decisions layered on top of it, each of which exists because the
/// caller is a script or a person mid-restore rather than a booting host: it never migrates, zero
/// teams is a failure rather than a vacuous pass, and a verdict arrives as an exit code and a
/// message rather than an exception.
///
/// <para><b>Real SQLite, not EF InMemory</b>, and specifically a database with <i>no schema</i> in
/// two of these tests — "the table isn't there" is a state InMemory cannot represent at all, and it
/// is exactly the state a restored-backup-older-than-the-binary produces.</para>
///
/// <para>Undecryptable credentials are simulated the same way the guard's own tests do it: these
/// contexts have no value converter attached, so a stored string that still carries the Data
/// Protection payload prefix is precisely what the real converter hands back when it cannot
/// decrypt.</para>
/// </summary>
public class KeyRingVerificationTests
{
    /// <summary>A real Data Protection payload prefix — base64url of the magic header 09 F0 C9 F0.</summary>
    private const string Ciphertext = "CfDJ8AAAAAAAAAAAAAAAAAAAAAAsomethingopaque";

    /// <summary>
    /// Opens a throwaway SQLite database. <paramref name="createSchema"/> false leaves it completely
    /// empty — no tables — which is how the "could not complete" path is reached honestly rather
    /// than by mocking a failure.
    /// </summary>
    private static async Task<(SqliteConnection Connection, AppDbContext Context)> CreateAsync(bool createSchema = true)
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options);

        if (createSchema)
        {
            await context.Database.EnsureCreatedAsync();
        }

        return (connection, context);
    }

    private static Team NewTeam(string name) => new() { Name = name };

    [Fact]
    public async Task ReadableCredentials_ReturnsZero_AndWritesNothingToStdErr()
    {
        var (connection, context) = await CreateAsync();
        await using var _ = connection;
        await using var __ = context;

        var team = NewTeam("HRCC");
        team.SmtpPassword = "a-real-password";
        team.ExamToolsPassword = "another-real-password";
        context.Teams.Add(team);
        await context.SaveChangesAsync();

        var error = new StringWriter();
        var exitCode = await KeyRingVerification.RunAsync(context, NullLogger.Instance, error);

        Assert.Equal(0, exitCode);
        Assert.Empty(error.ToString());
    }

    /// <summary>
    /// The reason this command is not just "call the guard". The guard passes when it finds nothing
    /// unreadable, so an empty database satisfies it having checked nothing — fine at startup, and
    /// worthless as evidence that a restore brought the data back. A green exit here would be the
    /// most dangerous possible answer: it is read as "the backup is good."
    /// </summary>
    [Fact]
    public async Task NoTeams_ReturnsOne_BecauseAVacuousPassIsWorseThanAFailure()
    {
        var (connection, context) = await CreateAsync();
        await using var _ = connection;
        await using var __ = context;

        var error = new StringWriter();
        var exitCode = await KeyRingVerification.RunAsync(context, NullLogger.Instance, error);

        Assert.Equal(1, exitCode);
        Assert.Contains("no teams", error.ToString(), StringComparison.OrdinalIgnoreCase);
        // Says what it means for the restore, not just what it observed.
        Assert.Contains("restored backup", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A team whose credential columns are all null is a real state (a team configured through the
    /// UI but with no integrations set up yet), and it is not the empty-database case: there is
    /// something to check, and nothing about it is unreadable.
    /// </summary>
    [Fact]
    public async Task ATeamWithNoCredentials_ReturnsZero_NotTheNoTeamsFailure()
    {
        var (connection, context) = await CreateAsync();
        await using var _ = connection;
        await using var __ = context;

        context.Teams.Add(NewTeam("WX0MIK"));
        await context.SaveChangesAsync();

        var error = new StringWriter();
        var exitCode = await KeyRingVerification.RunAsync(context, NullLogger.Instance, error);

        Assert.Equal(0, exitCode);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task UndecryptableCredential_ReturnsOne_AndPassesTheGuardsMessageThrough()
    {
        var (connection, context) = await CreateAsync();
        await using var _ = connection;
        await using var __ = context;

        var team = NewTeam("MARC");
        team.SquareAccessToken = Ciphertext;
        context.Teams.Add(team);
        await context.SaveChangesAsync();

        var error = new StringWriter();
        var exitCode = await KeyRingVerification.RunAsync(context, NullLogger.Instance, error);

        Assert.Equal(1, exitCode);
        var message = error.ToString();
        Assert.Contains("MARC", message);
        Assert.Contains(nameof(Team.SquareAccessToken), message);
        // The instruction not to "fix" this by re-entering credentials has to survive being routed
        // through this command — following it would destroy the originals permanently.
        Assert.Contains("unrecoverable", message);
        // Reported as a verdict, not as a malfunction. Wrapping it in "the check did not complete"
        // would keep every word above and still send the reader looking for a broken invocation
        // instead of a wrong key ring — and the wrapper preserves the inner message, so nothing
        // else in this test would notice.
        Assert.DoesNotContain("did not complete", message);
    }

    /// <summary>
    /// A restored database older than the running binary, or pointed at the wrong file entirely.
    /// This must not read as "the key ring is wrong" — the check never ran, and the fix is a
    /// different one.
    /// </summary>
    [Fact]
    public async Task UnreadableDatabase_ReturnsOne_AndSaysTheCheckDidNotComplete()
    {
        var (connection, context) = await CreateAsync(createSchema: false);
        await using var _ = connection;
        await using var __ = context;

        var error = new StringWriter();
        var exitCode = await KeyRingVerification.RunAsync(context, NullLogger.Instance, error);

        Assert.Equal(1, exitCode);
        Assert.Contains("did not complete", error.ToString());
        // Distinguishable from the guard's verdict, which is the whole point of separating them.
        Assert.DoesNotContain("cannot decrypt", error.ToString());
    }

    /// <summary>
    /// The safety property that lets this run against a restored copy, or on a schedule: it must not
    /// write to the database it is checking. An empty database stays empty — no schema created, no
    /// migration applied — even though the command ran to completion and returned a verdict.
    /// </summary>
    [Fact]
    public async Task NeverMigrates_SoAnUnmigratedDatabaseIsLeftExactlyAsItWas()
    {
        var (connection, context) = await CreateAsync(createSchema: false);
        await using var _ = connection;
        await using var __ = context;

        await KeyRingVerification.RunAsync(context, NullLogger.Instance, new StringWriter());

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table';";
        var tableCount = Convert.ToInt32(await command.ExecuteScalarAsync());

        Assert.Equal(0, tableCount);
    }
}
