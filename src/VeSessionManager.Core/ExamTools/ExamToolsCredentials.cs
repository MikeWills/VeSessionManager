using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.ExamTools;

/// <summary>Per-Team ExamTools login — TeamId keys ExamToolsClient's internal per-team cookie-jar/login cache. BaseUrl defaults to ExamToolsOptions' global appsettings value but can be overridden per-team via Team.ExamToolsBaseUrl (e.g. a dev team on examtools.dev alongside others on alpha.exam.tools).</summary>
public sealed record ExamToolsCredentials(int TeamId, string TeamCode, string Username, string Password, string BaseUrl)
{
    /// <summary>The one place team.ExamToolsBaseUrl-overrides-the-global-default fallback logic lives — every caller building credentials from a Team should go through this instead of re-deriving it.</summary>
    public static ExamToolsCredentials For(Team team, string globalDefaultBaseUrl) =>
        new(team.Id, team.ExamToolsTeamCode!, team.ExamToolsUsername!, team.ExamToolsPassword!,
            string.IsNullOrWhiteSpace(team.ExamToolsBaseUrl) ? globalDefaultBaseUrl : team.ExamToolsBaseUrl);

    /// <summary>
    /// True when this team's <em>effective</em> host (its own override, or the deployment default) is
    /// ExamTools' test site rather than a production one. Checked before anything is ever filed with a
    /// real VEC (see <see cref="VecSubmissions.ArrlSubmissionPreviewService"/> and
    /// <see cref="VecSubmissions.ArrlSubmissionService"/>) — a team practicing against test data has no
    /// business posting a real archive to ARRL, whatever this deployment's environment is.
    /// </summary>
    public bool IsTestEnvironment => BaseUrl.Contains("examtools.dev", StringComparison.OrdinalIgnoreCase);
}
