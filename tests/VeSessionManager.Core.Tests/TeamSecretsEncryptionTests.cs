using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Covers EncryptedStringConverter + TeamSecretsMigrationService together (2026-07-30 security
/// review follow-up) — needs a real SQLite database, not the InMemory provider, since the whole
/// point is proving what's actually stored on disk isn't plaintext. Uses an open in-memory
/// SqliteConnection kept alive for the test's duration (standard EF Core SQLite testing pattern —
/// ":memory:" only persists for as long as at least one connection to it stays open).
/// </summary>
public class TeamSecretsEncryptionTests
{
    private const string PlaintextSecret = "sq0atp-super-secret-access-token";

    private static (SqliteConnection Connection, DbContextOptions<AppDbContext> Options) CreateSqliteOptions()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        return (connection, options);
    }

    [Fact]
    public async Task SquareAccessToken_IsEncryptedOnDisk_AndRoundTripsThroughEf()
    {
        var (connection, options) = CreateSqliteOptions();
        await using var _ = connection;

        var protector = new EphemeralDataProtectionProvider();

        await using (var setupContext = new AppDbContext(options, protector))
        {
            await setupContext.Database.EnsureCreatedAsync();
        }

        await using (var writeContext = new AppDbContext(options, protector))
        {
            writeContext.Teams.Add(new Team { Name = "TESTTEAM", SquareAccessToken = PlaintextSecret, CreatedUtc = DateTime.UtcNow });
            await writeContext.SaveChangesAsync();
        }

        // Bypass EF entirely — read the raw column value straight off disk to prove it isn't plaintext.
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT SquareAccessToken FROM Teams LIMIT 1";
            var rawValue = (string?)await command.ExecuteScalarAsync();
            Assert.NotNull(rawValue);
            Assert.NotEqual(PlaintextSecret, rawValue);
        }

        // A separate AppDbContext instance (simulating a different request/process) sharing the
        // same protector reads it back correctly.
        await using var readContext = new AppDbContext(options, protector);
        var reloaded = await readContext.Teams.SingleAsync();
        Assert.Equal(PlaintextSecret, reloaded.SquareAccessToken);
    }

    [Fact]
    public async Task LegacyPlaintextRow_IsReadableWithoutCrashing_BeforeMigrationRuns()
    {
        var (connection, options) = CreateSqliteOptions();
        await using var _ = connection;

        var protector = new EphemeralDataProtectionProvider();

        await using (var setupContext = new AppDbContext(options, protector))
        {
            await setupContext.Database.EnsureCreatedAsync();
        }

        await InsertLegacyPlaintextTeamAsync(connection, "LEGACYTEAM", "legacy-plaintext-token");

        await using var readContext = new AppDbContext(options, protector);
        var team = await readContext.Teams.SingleAsync();
        // EncryptedStringConverter's Unprotect fallback means this doesn't throw on invalid
        // (i.e. never-encrypted) payload — it passes the raw legacy value through unchanged.
        Assert.Equal("legacy-plaintext-token", team.SquareAccessToken);
    }

    [Fact]
    public async Task MigrationService_EncryptsLegacyPlaintextRow_AndIsIdempotent()
    {
        var (connection, options) = CreateSqliteOptions();
        await using var _ = connection;

        var protector = new EphemeralDataProtectionProvider();

        await using (var setupContext = new AppDbContext(options, protector))
        {
            await setupContext.Database.EnsureCreatedAsync();
        }

        await InsertLegacyPlaintextTeamAsync(connection, "LEGACYTEAM", "legacy-plaintext-token");

        await using (var migrationContext = new AppDbContext(options, protector))
        {
            var migrated = await new TeamSecretsMigrationService(migrationContext, NullLogger<TeamSecretsMigrationService>.Instance)
                .MigrateAsync(CancellationToken.None);
            Assert.Equal(1, migrated);
        }

        // The raw column is no longer the legacy plaintext — it's been rewritten as ciphertext.
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT SquareAccessToken FROM Teams LIMIT 1";
            var rawValue = (string?)await command.ExecuteScalarAsync();
            Assert.NotEqual("legacy-plaintext-token", rawValue);
        }

        // Still reads back correctly through EF.
        await using (var readContext = new AppDbContext(options, protector))
        {
            var team = await readContext.Teams.SingleAsync();
            Assert.Equal("legacy-plaintext-token", team.SquareAccessToken);
        }

        // Safe to re-run — running it again against already-encrypted data must not corrupt it
        // (see TeamSecretsMigrationService's remarks: the read path always normalizes to the true
        // plaintext first, so a second migration run's re-encrypt is never a double-encrypt).
        await using (var secondRunContext = new AppDbContext(options, protector))
        {
            await new TeamSecretsMigrationService(secondRunContext, NullLogger<TeamSecretsMigrationService>.Instance)
                .MigrateAsync(CancellationToken.None);
        }

        await using (var finalReadContext = new AppDbContext(options, protector))
        {
            var team = await finalReadContext.Teams.SingleAsync();
            Assert.Equal("legacy-plaintext-token", team.SquareAccessToken);
        }
    }

    [Fact]
    public async Task MigrationService_NullCredentialColumns_AreSkippedWithoutError()
    {
        var (connection, options) = CreateSqliteOptions();
        await using var _ = connection;

        var protector = new EphemeralDataProtectionProvider();

        await using (var setupContext = new AppDbContext(options, protector))
        {
            await setupContext.Database.EnsureCreatedAsync();
            // A team with none of the 5 credential columns set (the common case for a brand-new team).
            setupContext.Teams.Add(new Team { Name = "NOCREDSTEAM", CreatedUtc = DateTime.UtcNow });
            await setupContext.SaveChangesAsync();
        }

        await using var migrationContext = new AppDbContext(options, protector);
        var migrated = await new TeamSecretsMigrationService(migrationContext, NullLogger<TeamSecretsMigrationService>.Instance)
            .MigrateAsync(CancellationToken.None);

        Assert.Equal(0, migrated);
    }

    private static async Task InsertLegacyPlaintextTeamAsync(SqliteConnection connection, string name, string squareAccessToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO Teams (Name, SquareAccessToken, CreatedUtc) VALUES (@name, @token, @createdUtc)";
        command.Parameters.AddWithValue("@name", name);
        command.Parameters.AddWithValue("@token", squareAccessToken);
        command.Parameters.AddWithValue("@createdUtc", DateTime.UtcNow.ToString("o"));
        await command.ExecuteNonQueryAsync();
    }
}
