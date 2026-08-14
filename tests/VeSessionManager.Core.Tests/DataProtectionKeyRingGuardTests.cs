using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// The guard's whole value is turning a silent state into a loud one, so these tests are about
/// *whether it fires*, not about cryptography.
///
/// <para>Note what is being simulated. These contexts have no value converter attached, so the
/// stored string is whatever is written — which is exactly the shape the real system produces when
/// decryption fails: <see cref="EncryptedStringConverter"/> catches the CryptographicException and
/// hands back the raw ciphertext. A credential that still looks like a Data Protection payload after
/// being read is one this process could not decrypt.</para>
/// </summary>
public class DataProtectionKeyRingGuardTests
{
    /// <summary>A real Data Protection payload prefix — base64url of the magic header 09 F0 C9 F0.</summary>
    private const string Ciphertext = "CfDJ8AAAAAAAAAAAAAAAAAAAAAAsomethingopaque";

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static Team NewTeam(string name) => new() { Name = name };

    /// <summary>
    /// Correct for a startup guard — a fresh deployment has no credentials to be wrong about, and
    /// refusing to boot over it would be absurd. It does mean "passed" and "checked something" are
    /// not the same outcome, which matters when the guard is used as proof that a *restored* backup
    /// is intact: an empty database passes here having verified nothing. The Worker's
    /// `--verify-keyring` switch rejects zero teams for exactly that reason; keep the two in step.
    /// </summary>
    [Fact]
    public async Task NoTeams_Passes()
    {
        await using var dbContext = CreateContext();

        await DataProtectionKeyRingGuard.VerifyAsync(dbContext, NullLogger.Instance);
    }

    [Fact]
    public async Task ReadableCredentials_Pass()
    {
        await using var dbContext = CreateContext();
        var team = NewTeam("HRCC");
        team.SmtpPassword = "a-real-password";
        team.SquareAccessToken = "EAAA-real-looking-token";
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();

        await DataProtectionKeyRingGuard.VerifyAsync(dbContext, NullLogger.Instance);
    }

    [Fact]
    public async Task ATeamWithNoCredentialsAtAll_Passes()
    {
        await using var dbContext = CreateContext();
        dbContext.Teams.Add(NewTeam("Brand new team"));
        await dbContext.SaveChangesAsync();

        await DataProtectionKeyRingGuard.VerifyAsync(dbContext, NullLogger.Instance);
    }

    /// <summary>
    /// The case the guard exists for: the key ring changed, so the converter's fallback returned
    /// ciphertext. Before this guard the app started happily and authenticated with that blob.
    /// </summary>
    [Fact]
    public async Task AnUndecryptableCredential_ThrowsAndNamesTheTeamAndColumn()
    {
        await using var dbContext = CreateContext();
        var team = NewTeam("MARC");
        team.SmtpPassword = Ciphertext;
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DataProtectionKeyRingGuard.VerifyAsync(dbContext, NullLogger.Instance));

        Assert.Contains("MARC", ex.Message);
        Assert.Contains(nameof(Team.SmtpPassword), ex.Message);
        // The message has to stop someone "fixing" it by re-entering credentials, which would
        // overwrite the originals under the new key and make them unrecoverable.
        Assert.Contains("unrecoverable", ex.Message);
    }

    [Fact]
    public async Task EveryEncryptedColumnIsChecked()
    {
        foreach (var (name, apply) in new (string, Action<Team>)[]
                 {
                     (nameof(Team.ExamToolsPassword), t => t.ExamToolsPassword = Ciphertext),
                     (nameof(Team.ZoomClientSecret), t => t.ZoomClientSecret = Ciphertext),
                     (nameof(Team.SquareAccessToken), t => t.SquareAccessToken = Ciphertext),
                     (nameof(Team.SquareWebhookSignatureKey), t => t.SquareWebhookSignatureKey = Ciphertext),
                     (nameof(Team.SmtpPassword), t => t.SmtpPassword = Ciphertext)
                 })
        {
            await using var dbContext = CreateContext();
            var team = NewTeam("WX0MIK");
            apply(team);
            dbContext.Teams.Add(team);
            await dbContext.SaveChangesAsync();

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => DataProtectionKeyRingGuard.VerifyAsync(dbContext, NullLogger.Instance));
            Assert.Contains(name, ex.Message);
        }
    }

    /// <summary>
    /// One healthy team must not mask a broken one — the message names every affected team, since
    /// an operator reading it is deciding whether to restore a key ring.
    /// </summary>
    [Fact]
    public async Task OneBrokenTeamAmongHealthyOnes_StillThrows()
    {
        await using var dbContext = CreateContext();
        var healthy = NewTeam("HRCC");
        healthy.SmtpPassword = "fine";
        var broken = NewTeam("MARC");
        broken.ZoomClientSecret = Ciphertext;
        dbContext.Teams.AddRange(healthy, broken);
        await dbContext.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DataProtectionKeyRingGuard.VerifyAsync(dbContext, NullLogger.Instance));

        Assert.Contains("MARC", ex.Message);
        Assert.DoesNotContain("HRCC", ex.Message);
    }

    /// <summary>
    /// A legacy plaintext row from before encryption existed must not trip the guard — that is the
    /// migration path EncryptedStringConverter's fallback was built for, and a false alarm here
    /// would refuse to start a perfectly healthy deployment.
    /// </summary>
    [Fact]
    public async Task LegacyPlaintextIsNotMistakenForCiphertext()
    {
        await using var dbContext = CreateContext();
        var team = NewTeam("Legacy");
        team.SmtpPassword = "CfDJ";           // shares a prefix, but is not a payload
        team.ExamToolsPassword = "cfdj8lower"; // prefix match is case-sensitive on purpose
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();

        await DataProtectionKeyRingGuard.VerifyAsync(dbContext, NullLogger.Instance);
    }

    // ---- #243: the sixth encrypted column lives on SystemSettings, not Team ----

    /// <summary>
    /// The finding. The guard iterated Teams only, so an unreadable SystemSmtpPassword sailed past
    /// it — and that one is the worst to miss: it is used verbatim as an SMTP password, so password
    /// reset and VE self-service links fail to authenticate, and PasswordResetService swallows send
    /// failures on purpose to avoid an enumeration oracle. The user is told "check your inbox" and
    /// waits forever.
    /// </summary>
    [Fact]
    public async Task UnreadableSystemSmtpPassword_Throws()
    {
        await using var dbContext = CreateContext();
        dbContext.SystemSettings.Add(new SystemSettings { SystemSmtpPassword = Ciphertext });
        await dbContext.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DataProtectionKeyRingGuard.VerifyAsync(dbContext, NullLogger.Instance));

        Assert.Contains(nameof(SystemSettings.SystemSmtpPassword), ex.Message);
    }

    /// <summary>
    /// The worst case in the finding, and the one that reads as fine: zero teams but a configured
    /// system sender. The guard iterated an empty list, logged "verified", and checked nothing at
    /// all — a deployment where the only stored credential is the unreadable one.
    /// </summary>
    [Fact]
    public async Task NoTeamsButUnreadableSystemSmtpPassword_Throws()
    {
        await using var dbContext = CreateContext();
        dbContext.SystemSettings.Add(new SystemSettings { SystemSmtpPassword = Ciphertext });
        await dbContext.SaveChangesAsync();

        Assert.Empty(dbContext.Teams);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => DataProtectionKeyRingGuard.VerifyAsync(dbContext, NullLogger.Instance));
    }

    [Fact]
    public async Task ReadableSystemSmtpPassword_Passes()
    {
        await using var dbContext = CreateContext();
        dbContext.SystemSettings.Add(new SystemSettings { SystemSmtpPassword = "a-real-password" });
        await dbContext.SaveChangesAsync();

        await DataProtectionKeyRingGuard.VerifyAsync(dbContext, NullLogger.Instance);
    }

    /// <summary>A deployment that never configured a system sender has nothing to verify here.</summary>
    [Fact]
    public async Task NullSystemSmtpPassword_Passes()
    {
        await using var dbContext = CreateContext();
        dbContext.SystemSettings.Add(new SystemSettings());
        await dbContext.SaveChangesAsync();

        await DataProtectionKeyRingGuard.VerifyAsync(dbContext, NullLogger.Instance);
    }
}
