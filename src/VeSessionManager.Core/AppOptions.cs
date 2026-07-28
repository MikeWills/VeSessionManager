namespace VeSessionManager.Core;

public class AppOptions
{
    public const string SectionName = "App";

    /// <summary>This deployment's own public Web host, e.g. https://ve.wx0mik.radio (prod) or
    /// https://localhost:5158 (dev) — global, environment-level, like ExamTools:BaseUrl, since one
    /// deployment serves one public host even though Team is otherwise multi-tenant. Used by the
    /// Worker (which has no HttpContext of its own) to build absolute links back into the Web app,
    /// e.g. the youth payment confirmation link embedded in the registration confirmation email.</summary>
    public string PublicBaseUrl { get; set; } = "https://localhost:5158";
}
