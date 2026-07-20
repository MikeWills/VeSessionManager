namespace VeSessionManager.Core.ExamTools;

public class ExamToolsOptions
{
    public const string SectionName = "ExamTools";

    /// <summary>e.g. https://examtools.dev (test) or https://exam.tools (prod). Stays a global, environment-level appsettings value — every Team on one deployment hits the same host. Team-specific values (the sessions API's ?team= filter, Username, Password) live on the Team entity instead — see docs/multi-team.md.</summary>
    public string BaseUrl { get; set; } = "https://examtools.dev";
}
