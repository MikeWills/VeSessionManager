using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.ExamTools;

/// <summary>Per-Team ExamTools login — TeamId keys ExamToolsClient's internal per-team cookie-jar/login cache. BaseUrl defaults to ExamToolsOptions' global appsettings value but can be overridden per-team via Team.ExamToolsBaseUrl (e.g. a dev team on examtools.dev alongside others on alpha.exam.tools).</summary>
public sealed record ExamToolsCredentials(int TeamId, string TeamCode, string Username, string Password, string BaseUrl)
{
    /// <summary>The one place team.ExamToolsBaseUrl-overrides-the-global-default fallback logic lives — every caller building credentials from a Team should go through this instead of re-deriving it.</summary>
    public static ExamToolsCredentials For(Team team, string globalDefaultBaseUrl) =>
        new(team.Id, team.ExamToolsTeamCode!, team.ExamToolsUsername!, team.ExamToolsPassword!,
            string.IsNullOrWhiteSpace(team.ExamToolsBaseUrl) ? globalDefaultBaseUrl : team.ExamToolsBaseUrl);
}
