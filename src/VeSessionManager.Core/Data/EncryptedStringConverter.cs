using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace VeSessionManager.Core.Data;

/// <summary>
/// EF Core value converter that encrypts a string column at rest via ASP.NET Core's Data
/// Protection API — added 2026-07-30 after a security review flagged Team's per-team integration
/// credentials (Zoom/Square/SMTP/ExamTools) as plaintext columns. Only applied to genuine bearer
/// secrets, not usernames/ids/URLs — see AppDbContext's Team configuration for the property list.
///
/// The read path (Unprotect, via TryUnprotect) falls back to the raw stored value unchanged if it
/// isn't valid protected payload, rather than throwing — this is what makes the one-time migration
/// of already-existing plaintext rows safe: every read normalizes to the true plaintext (whether it
/// was already encrypted or is still legacy plaintext), so there's no risk of a crash reading old
/// data before it's been migrated, and the app keeps working identically whether or not the
/// migration has run yet. The write path (Protect) always encrypts whatever's currently in memory.
/// See TeamSecretsMigrationService for the one-time sweep that forces every legacy row to be
/// rewritten through this converter — it works by simply forcing a re-save, not by hand-calling
/// Protect/Unprotect itself.
/// </summary>
public sealed class EncryptedStringConverter : ValueConverter<string?, string?>
{
    /// <summary>
    /// Purpose string for Team's credential columns — TeamSecretsMigrationService must create its
    /// own IDataProtector with this exact same purpose, or it won't be able to read/re-encrypt what
    /// this converter wrote.
    /// </summary>
    public const string TeamCredentialsPurpose = "Team.Credentials.v1";

    public EncryptedStringConverter(IDataProtector protector)
        : base(
            v => v == null ? null : protector.Protect(v),
            v => v == null ? null : TryUnprotect(protector, v))
    {
    }

    private static string TryUnprotect(IDataProtector protector, string value)
    {
        try
        {
            return protector.Unprotect(value);
        }
        catch (CryptographicException)
        {
            // Not valid protected payload — a pre-migration legacy plaintext row (or, in principle,
            // corrupted/tampered ciphertext). Pass through unchanged rather than crash the read;
            // TeamSecretsMigrationService is what upgrades these to real ciphertext.
            return value;
        }
    }
}
