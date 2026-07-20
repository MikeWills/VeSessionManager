namespace VeSessionManager.Core.Square;

public class SquareOptions
{
    public const string SectionName = "Square";

    /// <summary>"Sandbox" or "Production" — selects which Square API host the SDK talks to. Stays global/environment-level (whole-deployment choice, like ExamTools:BaseUrl) — AccessToken/LocationId/WebhookSignatureKey/WebhookNotificationUrl all live on Team now (multi-team, each team has its own separate Square account). See docs/multi-team.md.</summary>
    public string Environment { get; set; } = "Sandbox";
}
