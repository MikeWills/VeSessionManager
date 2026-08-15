using System.Reflection;
using VeSessionManager.Core.Email;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// The BCC exists so a team can see what the app actually sends to candidates (issue #207). These
/// tests are mostly about what it must <b>not</b> do.
/// </summary>
public class CandidateEmailBccTests
{
    private static EmailMessage Message(string? bcc = null) =>
        new("candidate@example.org", "noreply@example.org", "VE Team", "reply@example.org",
            "Your session", "<p>hi</p>", InlineLogo: null, BccAddress: bcc);

    [Fact]
    public void WithoutTestMode_TheBccSurvives()
    {
        var (result, redirected) = TestModeEmailRedirector.Apply(Message("watch@example.org"), testModeEnabled: false, overrideEmail: null);

        Assert.False(redirected);
        Assert.Equal("watch@example.org", result.BccAddress);
    }

    /// <summary>
    /// Test Mode already routes everything to one monitoring inbox. Leaving the BCC on would deliver
    /// the same message there twice — and the copy would be the *un*redirected one, with no
    /// "[TEST MODE]" marking, so it would read like real mail that had genuinely gone to a candidate.
    /// </summary>
    [Fact]
    public void UnderTestMode_TheBccIsDropped()
    {
        var (result, redirected) = TestModeEmailRedirector.Apply(
            Message("watch@example.org"), testModeEnabled: true, overrideEmail: "tester@example.org");

        Assert.True(redirected);
        Assert.Equal("tester@example.org", result.ToAddress);
        Assert.Null(result.BccAddress);
    }

    [Fact]
    public void TheBccDefaultsToOff()
    {
        Assert.Null(Message().BccAddress);
    }

    /// <summary>
    /// ⚠️ The property this whole feature depends on. Three senders carry access tokens — a password
    /// reset, a VE self-service link, an email-change confirmation — and a copy of any of them in a
    /// shared inbox is an account-takeover path, not a monitoring convenience.
    ///
    /// <para>Enforced by *which call sites set BccAddress*, so this test reads the source rather than
    /// the behavior: a behavioral test would need each service stood up with a live sender, and
    /// would still pass if someone added a fourth token-bearing sender tomorrow. Source inspection
    /// catches the case this is actually guarding against — someone "finishing the job" by wiring
    /// BccAddress into the remaining senders.</para>
    /// </summary>
    [Theory]
    [InlineData("Authorization/PasswordResetService.cs")]
    [InlineData("VolunteerExaminers/VeSelfServiceLinkService.cs")]
    [InlineData("VolunteerExaminers/VeEmailChangeService.cs")]
    public void TokenBearingSendersNeverSetABcc(string relativePath)
    {
        var source = File.ReadAllText(Path.Combine(CoreProjectRoot(), relativePath));

        Assert.DoesNotContain("BccAddress", source);
    }

    /// <summary>Walks up from the test assembly to the repo, then into the Core project.</summary>
    private static string CoreProjectRoot()
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "VeSessionManager.Core")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "src", "VeSessionManager.Core");
    }
}
