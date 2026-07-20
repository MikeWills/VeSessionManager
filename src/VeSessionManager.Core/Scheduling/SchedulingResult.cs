namespace VeSessionManager.Core.Scheduling;

/// <summary>Per-run counters, logged by the scheduling job so JobRunHistory stays a one-line summary.</summary>
public class SchedulingResult
{
    public int SessionsSynced { get; set; }

    /// <summary>Whatever could run this pass did; the rest is waiting on Zoom and/or Discord to be configured (both optional integrations) — not a failure, just waiting.</summary>
    public int SessionsAwaitingIntegrationConfig { get; set; }

    public int SessionsCleanedUp { get; set; }
    public int SessionsFailed { get; set; }

    public override string ToString() =>
        $"synced {SessionsSynced}, awaiting integration config {SessionsAwaitingIntegrationConfig}, cleaned up {SessionsCleanedUp}, failed {SessionsFailed}";
}
