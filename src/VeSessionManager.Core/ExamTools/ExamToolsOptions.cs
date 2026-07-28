namespace VeSessionManager.Core.ExamTools;

public class ExamToolsOptions
{
    public const string SectionName = "ExamTools";

    /// <summary>e.g. https://examtools.dev (test) or https://alpha.exam.tools (prod). The deployment-wide default host — a Team can override it via Team.ExamToolsBaseUrl (e.g. a dev team running against a different ExamTools instance than the rest of the deployment); see ExamToolsCredentials.For and docs/examtools-api.md.</summary>
    public string BaseUrl { get; set; } = "https://examtools.dev";
}
