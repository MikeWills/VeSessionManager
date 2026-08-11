using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using VeSessionManager.Core.Data;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// The converter's read path returns the raw stored value when it cannot decrypt. That is what makes
/// the legacy-plaintext migration safe, and it is also what made a wrong key ring completely silent
/// (issue #160).
///
/// <para>The fix is not to remove the fallback — failing a read would take down whatever job was
/// running, for a condition <see cref="DataProtectionKeyRingGuard"/> already refuses to start the
/// host over, and it would do it mid-run rather than at startup. The fix is to stop it being
/// silent, and to be **precise about which case is alarming**: un-migrated plaintext is expected,
/// undecryptable ciphertext is not.</para>
/// </summary>
public class EncryptedStringConverterTests : IDisposable
{
    private readonly List<string> _reported = [];

    public EncryptedStringConverterTests()
    {
        ResetHook();
        EncryptedStringConverter.OnUndecryptableValueRead = _reported.Add;
    }

    public void Dispose() => ResetHook();

    /// <summary>
    /// The report-once latch is static, so it has to be cleared between tests — otherwise the first
    /// test to trip it would silence every later one and they would pass for the wrong reason.
    /// </summary>
    private static void ResetHook()
    {
        EncryptedStringConverter.OnUndecryptableValueRead = null;
        typeof(EncryptedStringConverter)
            .GetField("_undecryptableReported", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .SetValue(null, 0);
    }

    private static Func<string?, string?> ReadPathOf(IDataProtector protector) =>
        new EncryptedStringConverter(protector).ConvertFromProviderExpression.Compile();

    [Fact]
    public void AValueEncryptedWithTheSameKeyRoundTrips()
    {
        var protector = new EphemeralDataProtectionProvider().CreateProtector(EncryptedStringConverter.TeamCredentialsPurpose);
        var stored = protector.Protect("hunter2");

        Assert.Equal("hunter2", ReadPathOf(protector)(stored));
        Assert.Empty(_reported);
    }

    /// <summary>
    /// The expected case: a row written before encryption existed. It must pass through untouched
    /// and must NOT warn — a signal that fires on every legitimately un-migrated row is one nobody
    /// reads.
    /// </summary>
    [Theory]
    [InlineData("plain-old-password")]
    [InlineData("CfDJ")]        // shares a prefix but is not a payload
    [InlineData("cfdj8lower")]  // the prefix check is case-sensitive on purpose
    public void LegacyPlaintextPassesThroughWithoutWarning(string stored)
    {
        var protector = new EphemeralDataProtectionProvider().CreateProtector(EncryptedStringConverter.TeamCredentialsPurpose);

        Assert.Equal(stored, ReadPathOf(protector)(stored));
        Assert.Empty(_reported);
    }

    /// <summary>
    /// The alarming case, and the one this issue is about: a value that was definitely encrypted,
    /// read by a process holding a different key. Encrypted with one ephemeral provider, read with
    /// another — exactly the key-ring drift CLAUDE.md warns about between Web and Worker.
    /// </summary>
    [Fact]
    public void CiphertextFromAnotherKeyRingWarnsAndStillReturnsTheRawValue()
    {
        var stored = new EphemeralDataProtectionProvider()
            .CreateProtector(EncryptedStringConverter.TeamCredentialsPurpose)
            .Protect("hunter2");

        var otherKeyRing = new EphemeralDataProtectionProvider().CreateProtector(EncryptedStringConverter.TeamCredentialsPurpose);

        // Returned unchanged rather than thrown, so a job in flight is not killed by it.
        Assert.Equal(stored, ReadPathOf(otherKeyRing)(stored));

        var message = Assert.Single(_reported);
        Assert.Contains("could not be decrypted", message);
        // The message must talk the reader out of the one action that destroys the originals.
        Assert.Contains("Do NOT re-enter credentials", message);
    }

    /// <summary>
    /// A broken key ring affects every read of every credential. Warning per read would bury the
    /// first, most useful line under thousands of duplicates, so it reports once per process.
    /// </summary>
    [Fact]
    public void TheWarningIsReportedOncePerProcess()
    {
        var stored = new EphemeralDataProtectionProvider()
            .CreateProtector(EncryptedStringConverter.TeamCredentialsPurpose)
            .Protect("hunter2");
        var read = ReadPathOf(new EphemeralDataProtectionProvider().CreateProtector(EncryptedStringConverter.TeamCredentialsPurpose));

        for (var i = 0; i < 5; i++)
        {
            read(stored);
        }

        Assert.Single(_reported);
    }

    [Fact]
    public void NullStaysNull()
    {
        var protector = new EphemeralDataProtectionProvider().CreateProtector(EncryptedStringConverter.TeamCredentialsPurpose);

        Assert.Null(ReadPathOf(protector)(null));
        Assert.Empty(_reported);
    }
}
