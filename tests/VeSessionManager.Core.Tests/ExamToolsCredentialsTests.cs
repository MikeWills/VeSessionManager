using VeSessionManager.Core.Entities;
using VeSessionManager.Core.ExamTools;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// <see cref="ExamToolsCredentials.IsTestEnvironment"/> — the check that keeps a team practicing
/// against ExamTools' test site from being able to file a real session with ARRL (issue raised
/// 2026-08-26 after asking "how do I indicate a team is in test mode").
/// </summary>
public class ExamToolsCredentialsTests
{
    private static Team NewTeam(string? baseUrlOverride = null) => new()
    {
        Name = "WX0MIK", CreatedUtc = DateTime.UtcNow, ExamToolsBaseUrl = baseUrlOverride
    };

    [Fact]
    public void ATeamOnExamToolsDev_IsATestEnvironment()
    {
        var credentials = ExamToolsCredentials.For(NewTeam(), "https://examtools.dev");

        Assert.True(credentials.IsTestEnvironment);
    }

    [Fact]
    public void ATeamOnAlphaExamTools_IsNotATestEnvironment()
    {
        var credentials = ExamToolsCredentials.For(NewTeam(), "https://alpha.exam.tools");

        Assert.False(credentials.IsTestEnvironment);
    }

    [Fact]
    public void ATeamOnExamTools_IsNotATestEnvironment()
    {
        var credentials = ExamToolsCredentials.For(NewTeam(), "https://exam.tools");

        Assert.False(credentials.IsTestEnvironment);
    }

    /// <summary>A team's own override wins over the deployment default — e.g. one dev team on examtools.dev on a deployment whose global default is production.</summary>
    [Fact]
    public void APerTeamOverride_WinsOverTheDeploymentDefault()
    {
        var credentials = ExamToolsCredentials.For(
            NewTeam(baseUrlOverride: "https://examtools.dev"), globalDefaultBaseUrl: "https://alpha.exam.tools");

        Assert.True(credentials.IsTestEnvironment);
    }
}
