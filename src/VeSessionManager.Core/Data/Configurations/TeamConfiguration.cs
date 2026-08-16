using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Data.Configurations;

/// <summary>
/// Model configuration for <see cref="Team"/>, split out of <c>AppDbContext.OnModelCreating</c>
/// (#311, S-04). The comments here came with the rules they explain — several record decisions that
/// cost real debugging, so they travel with the configuration rather than staying behind in a file
/// that no longer holds it.
/// </summary>
public class TeamConfiguration : IEntityTypeConfiguration<Team>
{
    private readonly EncryptedStringConverter encryptedString;

    public TeamConfiguration(EncryptedStringConverter encryptedString) => this.encryptedString = encryptedString;

    public void Configure(EntityTypeBuilder<Team> b)
    {
        b.HasIndex(t => t.Name).IsUnique();
        // C#'s "= 30" property initializer only applies to newly-constructed objects — without
        // this, the SQL column default (used for any row inserted outside EF, and by the
        // migration's own AddColumn for existing rows) would be 0, which means "purge
        // immediately" instead of "not configured yet."
        b.Property(t => t.PurgeUnpaidLinkDays).HasDefaultValue(30);
        // Same reasoning — without this, existing teams would retroactively get 0 (no breakout
        // rooms) from the migration's AddColumn instead of the intended default of 2.
        b.Property(t => t.ZoomBreakoutRoomCount).HasDefaultValue(2);
        // Sandbox is already 0, so this changes no value — it declares the SQL default so a
        // schema built from the model (EnsureCreated, i.e. the SQLite tests) matches the one the
        // migration actually produces. Without it a row inserted outside EF hits a NOT NULL
        // failure on one and succeeds on the other, which is drift that only shows up in tests.
        b.Property(t => t.SquareEnvironment).HasDefaultValue(SquareApiEnvironment.Sandbox);

        // The four integration switches default to ON in the database, not just in the C# initializer
        // (#64). Without this the generated migration writes defaultValue: false for every existing
        // row, which is invisible while IntegrationOverridesEnabled is off — and then mutes every
        // integration at once the moment an admin turns the master switch on, which is the exact
        // opposite of what they just asked for.
        // The master too, at false. Every one of these needs a database-level default, not just a
        // C# initializer: rows are also created by raw SQL in the legacy-plaintext migration tests,
        // and a NOT NULL column with no default fails those inserts outright.
        b.Property(t => t.IntegrationOverridesEnabled).HasDefaultValue(false);
        b.Property(t => t.ZoomEnabled).HasDefaultValue(true);
        b.Property(t => t.DiscordEnabled).HasDefaultValue(true);
        b.Property(t => t.SquareEnabled).HasDefaultValue(true);
        b.Property(t => t.EmailEnabled).HasDefaultValue(true);
        // Declared on the model, not only in the migration: a schema built by EnsureCreated takes its
        // columns from here, so a default set only in the migration leaves those NOT NULL with no
        // DEFAULT and every raw INSERT that omits the column fails. That is what the
        // DeclareColumnDefaultsOnModel migration was about, and this is the same trap (#191).
        b.Property(t => t.VeEmailSubscriptionsEnabled).HasDefaultValue(false);

        // Encrypted at rest (2026-07-30 security review) — genuine bearer secrets only, not the
        // usernames/ids/URLs alongside them (those stay plaintext, useful to read at a glance).
        // See EncryptedStringConverter's remarks and TeamSecretsMigrationService for existing data.
        b.Property(t => t.ExamToolsPassword).HasConversion(encryptedString);
        b.Property(t => t.ZoomClientSecret).HasConversion(encryptedString);
        b.Property(t => t.SquareAccessToken).HasConversion(encryptedString);
        b.Property(t => t.SquareWebhookSignatureKey).HasConversion(encryptedString);
        b.Property(t => t.SmtpPassword).HasConversion(encryptedString);
        
    }
}
