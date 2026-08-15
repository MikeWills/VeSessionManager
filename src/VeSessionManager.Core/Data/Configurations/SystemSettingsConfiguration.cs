using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Data.Configurations;

/// <summary>
/// Model configuration for <see cref="SystemSettings"/>, split out of <c>AppDbContext.OnModelCreating</c>
/// (#311, S-04). The comments here came with the rules they explain — several record decisions that
/// cost real debugging, so they travel with the configuration rather than staying behind in a file
/// that no longer holds it.
/// </summary>
public class SystemSettingsConfiguration : IEntityTypeConfiguration<SystemSettings>
{
    private readonly EncryptedStringConverter encryptedString;

    /// <summary>
    /// Takes the converter rather than building one, so this and TeamConfiguration share a single
    /// instance under a single protector purpose. That was already the stated intent here — "under
    /// the same protector purpose so there is one key path to back up rather than two" — it just had
    /// to be re-derived from the provider at each site to be true.
    /// </summary>
    public SystemSettingsConfiguration(EncryptedStringConverter encryptedString) =>
        this.encryptedString = encryptedString;

    public void Configure(EntityTypeBuilder<SystemSettings> b)
    {
        b.HasOne(s => s.UpdatedByUser).WithMany().HasForeignKey(s => s.UpdatedByUserId).OnDelete(DeleteBehavior.Restrict);
        // Encrypted like Team's credential columns, under the same protector purpose so there is
        // one key path to back up rather than two. IsSystemEmailConfigured is a computed
        // property with no setter, so EF ignores it without being told to.
        b.Property(s => s.SystemSmtpPassword).HasConversion(encryptedString);
        
    }
}
