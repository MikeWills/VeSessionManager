using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Data;

/// <summary>
/// Refuses to start when the Data Protection key ring cannot decrypt the credentials already in the
/// database — the failure that otherwise has no symptom at all.
///
/// <para><b>Why this needs a guard rather than care.</b> <see cref="EncryptedStringConverter"/>'s
/// read path falls back to the raw stored value when Unprotect throws, which is what makes the
/// legacy-plaintext migration safe. The cost is that a *wrong or missing* key ring looks exactly
/// like a not-yet-migrated row: nothing throws, nothing is logged, and the app runs normally while
/// every integration quietly authenticates with a base64 blob instead of a password. External calls
/// then fail for reasons that point anywhere but here.</para>
///
/// <para>Three ways to arrive at that state, all real: pointing
/// <c>DataProtection:KeyRingPath</c> at a new directory before copying the existing keys into it
/// (see docs/deployment.md), losing the key ring in a restore, or letting Web and Worker drift onto
/// different application names or paths — the constraint CLAUDE.md records, which until now had no
/// enforcement behind it.</para>
///
/// <para>Detection needs no raw SQL and no new column. A Data Protection payload is base64url of a
/// blob whose first four bytes are the magic header <c>09 F0 C9 F0</c>, which always encodes to the
/// prefix below. Because the converter hands back the raw value when it cannot decrypt, a credential
/// that <i>still looks like ciphertext after being read through the converter</i> is precisely a
/// credential this process cannot decrypt.</para>
/// </summary>
public static class DataProtectionKeyRingGuard
{
    /// <summary>Shared with the converter, so "looks like ciphertext" has one definition.</summary>
    private const string ProtectedPayloadPrefix = EncryptedStringConverter.ProtectedPayloadPrefix;

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> if any stored team credential is unreadable.
    /// Call after <c>Database.Migrate()</c> and before the host starts doing work — a startup crash
    /// with a clear message is the whole point, and it is strictly better than the silent
    /// alternative.
    /// </summary>
    public static async Task VerifyAsync(AppDbContext dbContext, ILogger logger, CancellationToken cancellationToken = default)
    {
        // Reads through the converter on purpose: that is what makes an undecryptable value visible.
        var teams = await dbContext.Teams.AsNoTracking().ToListAsync(cancellationToken);

        var affected = teams
            .Select(team => (team, columns: UnreadableColumns(team)))
            .Where(x => x.columns.Count > 0)
            .ToList();

        if (affected.Count == 0)
        {
            logger.LogInformation("Data Protection key ring verified — {TeamCount} team(s), all stored credentials readable", teams.Count);
            return;
        }

        var detail = string.Join("; ", affected.Select(x => $"{x.team.Name} ({string.Join(", ", x.columns)})"));
        throw new InvalidOperationException(
            "Data Protection key ring cannot decrypt stored team credentials: " + detail + ". " +
            "This process is using a key ring that did not encrypt them — the usual causes are a changed " +
            "DataProtection:KeyRingPath whose new directory was never populated with the existing keys, a lost " +
            "key ring, or Web and Worker disagreeing on application name or path. " +
            "Do NOT start the app and re-save credentials: that would overwrite them with values encrypted " +
            "under the new key and make the originals unrecoverable. Restore the original key ring first " +
            "(see docs/deployment.md). If this is a deliberate key rotation, re-enter each credential through " +
            "Team Settings while the OLD key ring is still in place.");
    }

    /// <summary>
    /// The five encrypted columns, by the names an operator sees in Team Settings. Kept in step with
    /// AppDbContext's <c>HasConversion(encryptedString)</c> list — a column added there and missed
    /// here is simply not checked, which is a gap rather than a false alarm.
    /// </summary>
    private static List<string> UnreadableColumns(Team team)
    {
        List<(string Name, string? Value)> encrypted =
        [
            (nameof(Team.ExamToolsPassword), team.ExamToolsPassword),
            (nameof(Team.ZoomClientSecret), team.ZoomClientSecret),
            (nameof(Team.SquareAccessToken), team.SquareAccessToken),
            (nameof(Team.SquareWebhookSignatureKey), team.SquareWebhookSignatureKey),
            (nameof(Team.SmtpPassword), team.SmtpPassword)
        ];

        return encrypted
            .Where(c => LooksLikeCiphertext(c.Value))
            .Select(c => c.Name)
            .ToList();
    }

    /// <summary>
    /// A value that still carries the Data Protection prefix *after* the converter has run means the
    /// converter's fallback fired — i.e. this process could not decrypt it.
    /// </summary>
    private static bool LooksLikeCiphertext(string? value) =>
        value is not null && value.StartsWith(ProtectedPayloadPrefix, StringComparison.Ordinal);
}
