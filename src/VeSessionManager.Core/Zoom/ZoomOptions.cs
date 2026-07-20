namespace VeSessionManager.Core.Zoom;

public class ZoomOptions
{
    public const string SectionName = "Zoom";

    /// <summary>Which Zoom user's account meetings are created under (POST /users/{userId}/meetings). "me" resolves to the account tied to the Server-to-Server OAuth app.</summary>
    public string UserId { get; set; } = "me";

    // AccountId/ClientId/ClientSecret come from user-secrets or environment variables, never from appsettings files.
    public string AccountId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
}
