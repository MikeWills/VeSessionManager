using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Every column encrypted with <c>EncryptedStringConverter</c> must be checked by
/// <see cref="DataProtectionKeyRingGuard"/>.
///
/// <para><b>Why this is a test and not a comment.</b> The guard's own doc comment already predicted
/// the gap — <i>"a column added there and missed here is simply not checked"</i> — and then the gap
/// happened anyway (#243): <c>SystemSettings.SystemSmtpPassword</c> was added before the guard
/// existed, the guard iterated <c>Teams</c> only, and nothing noticed for months.</para>
///
/// <para><b>Nothing else can catch it.</b> A missed column produces no error and no log line. The
/// converter's read path deliberately returns the raw stored value when <c>Unprotect</c> throws —
/// that fallback is what makes the legacy-plaintext migration safe — so an unreadable credential is
/// indistinguishable from an un-migrated one, and the guard reports success either way. For the
/// column that was actually missed, the first symptom is a user who never receives a password-reset
/// email, because <c>PasswordResetService</c> swallows send failures on purpose to avoid an
/// enumeration oracle.</para>
///
/// <para>This asserts against the EF model rather than a hand-written list, so a seventh encrypted
/// column fails the build the day it is registered.</para>
/// </summary>
public class EncryptedColumnCoverageTests
{
    /// <summary>
    /// Entity.Property pairs the guard knows how to read. Adding a column to AppDbContext's
    /// <c>HasConversion(encryptedString)</c> list without adding it here — and to the guard — is
    /// exactly the failure this test exists to force.
    /// </summary>
    private static readonly HashSet<string> CheckedByTheGuard =
    [
        "Team.ExamToolsPassword",
        "Team.ZoomClientSecret",
        "Team.SquareAccessToken",
        "Team.SquareWebhookSignatureKey",
        "Team.SmtpPassword",
        "SystemSettings.SystemSmtpPassword"
    ];

    [Fact]
    public void EveryEncryptedColumnIsVerifiedByTheKeyRingGuard()
    {
        using var dbContext = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        var encrypted = dbContext.Model.GetEntityTypes()
            .SelectMany(e => e.GetProperties()
                .Where(p => p.GetValueConverter() is EncryptedStringConverter)
                .Select(p => $"{e.ClrType.Name}.{p.Name}"))
            .OrderBy(n => n)
            .ToList();

        // If this trips, the model has no encrypted columns at all — which means the converter was
        // unregistered and every credential is being written in plaintext. That is a louder failure
        // than the one this test was written for, so it gets its own assertion.
        Assert.NotEmpty(encrypted);

        var unchecked_ = encrypted.Where(c => !CheckedByTheGuard.Contains(c)).ToList();
        Assert.True(unchecked_.Count == 0,
            "These columns are encrypted but not verified by DataProtectionKeyRingGuard: " +
            string.Join(", ", unchecked_) +
            ". A column it does not check is a credential that can be silently undecryptable — the guard " +
            "will log 'key ring verified' and the app will authenticate with a base64 blob. Add it to " +
            "the guard (UnreadableColumns for a Team column, VerifyAsync otherwise) and to this list.");

        // The reverse direction: a name left here after the column is renamed or removed makes the
        // list above look like coverage it no longer provides.
        var stale = CheckedByTheGuard.Where(c => !encrypted.Contains(c)).ToList();
        Assert.True(stale.Count == 0,
            "These are listed as checked but no longer exist as encrypted columns: " + string.Join(", ", stale));
    }
}
