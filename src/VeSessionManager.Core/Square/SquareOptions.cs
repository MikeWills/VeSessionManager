namespace VeSessionManager.Core.Square;

public class SquareOptions
{
    public const string SectionName = "Square";

    /// <summary>"Sandbox" or "Production" — selects which Square API host the SDK talks to.</summary>
    public string Environment { get; set; } = "Sandbox";

    /// <summary>The Square location payment links are created under.</summary>
    public string LocationId { get; set; } = "";

    /// <summary>Must exactly match the webhook subscription's notification URL configured in the Square Developer portal — required input to signature verification, not just where Square happens to POST.</summary>
    public string WebhookNotificationUrl { get; set; } = "";

    // AccessToken/WebhookSignatureKey come from user-secrets or environment variables, never from appsettings files.
    public string AccessToken { get; set; } = "";
    public string WebhookSignatureKey { get; set; } = "";
}
