namespace VeSessionManager.Core.Notifications;

/// <summary>Per-run counters, logged by CandidateNotificationService's two job methods so JobRunHistory stays a one-line summary.</summary>
public class EmailNotificationResult
{
    public int Sent { get; set; }
    public int Failed { get; set; }

    public override string ToString() => $"sent {Sent}, failed {Failed}";
}
