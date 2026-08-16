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
///
/// <para><b>Do not add plain <c>AsNoTracking()</c> to a query that materializes Team.</b> This
/// converter runs per property per materialization, so each Team costs one Unprotect per encrypted
/// column. The change tracker's identity resolution is what stops that repeating: the same Team
/// appearing on fifty rows is materialized once. <c>AsNoTracking()</c> <b>disables</b> identity
/// resolution, so the obvious "this is a read, make it faster" change multiplies decryption by row
/// count and makes the query slower than it was.</para>
///
/// <para>Use <c>AsNoTrackingWithIdentityResolution()</c> if a read path genuinely needs it, or
/// better, project to what the caller actually reads — see
/// <c>SessionAccessScope.GetAvailableTeamsAsync</c>, which was rewritten to a projection after
/// materializing whole Teams decrypted every team's credentials on every render of the team
/// picker.</para>
///
/// <para>Recorded here rather than in an issue (#295, closed 2026-08-16 as won't-fix) because this
/// is the type that makes it true, and it is the thing anyone reaching for <c>AsNoTracking</c> will
/// not think to look at.</para>
/// </summary>
public sealed class EncryptedStringConverter : ValueConverter<string?, string?>
{
    /// <summary>
    /// Purpose string for Team's credential columns — TeamSecretsMigrationService must create its
    /// own IDataProtector with this exact same purpose, or it won't be able to read/re-encrypt what
    /// this converter wrote.
    /// </summary>
    public const string TeamCredentialsPurpose = "Team.Credentials.v1";

    /// <summary>
    /// Every ASP.NET Core Data Protection payload starts with this — base64url of the four-byte
    /// magic header <c>09 F0 C9 F0</c>. Shared with <see cref="DataProtectionKeyRingGuard"/> so
    /// "does this look like ciphertext?" has one definition.
    /// </summary>
    public const string ProtectedPayloadPrefix = "CfDJ8";

    /// <summary>
    /// Called the first time this process reads a value that <b>looks like ciphertext but cannot be
    /// decrypted</b> — wired to a logger by each host at startup (issue #160).
    ///
    /// <para>Static because the converter is baked into EF's cached model, which outlives any scope;
    /// a captured scoped logger would be wrong. Fired once per process, not per read: a key-ring
    /// problem affects every read of every credential, and a warning per read would bury the first
    /// one under thousands of copies.</para>
    /// </summary>
    public static Action<string>? OnUndecryptableValueRead { get; set; }

    private static int _undecryptableReported;

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
            // Not valid protected payload. Two very different situations land here, and only one is
            // a problem:
            //
            //   * legacy plaintext from before encryption existed — expected, harmless, and the
            //     whole reason this fallback exists;
            //   * a value that *does* carry the Data Protection prefix and still would not
            //     decrypt — this process cannot read a credential that was definitely encrypted,
            //     which means the wrong key ring, a lost key ring, or tampering.
            //
            // Distinguishing them by the prefix is what makes the warning worth having: without it
            // the signal would fire on every legitimately un-migrated row and be ignored.
            //
            // Still returns the raw value either way. Failing the read here would take down whatever
            // was running for a problem DataProtectionKeyRingGuard already refuses to start the host
            // over, and would do it in the middle of a job rather than at startup.
            if (value.StartsWith(ProtectedPayloadPrefix, StringComparison.Ordinal)
                && Interlocked.Exchange(ref _undecryptableReported, 1) == 0)
            {
                OnUndecryptableValueRead?.Invoke(
                    "A stored credential carries the Data Protection payload marker but could not be decrypted. "
                    + "This process is using a key ring that did not encrypt it — check DataProtection:KeyRingPath "
                    + "and that Web and Worker agree on it. Do NOT re-enter credentials to work around this: that "
                    + "overwrites the originals under the new key and makes them unrecoverable. "
                    + "Further occurrences in this process are not logged.");
            }

            return value;
        }
    }
}
