namespace VeSessionManager.Core.ExamTools;

public class ExamToolsOptions
{
    public const string SectionName = "ExamTools";

    /// <summary>e.g. https://examtools.dev (test) or https://exam.tools (prod).</summary>
    public string BaseUrl { get; set; } = "https://examtools.dev";

    /// <summary>Team id as used by the sessions API's ?team= filter (e.g. WX0MIK on dev, HRCC on prod).</summary>
    public string Team { get; set; } = "";

    // Username/Password come from user-secrets or environment variables, never from appsettings files.
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}
