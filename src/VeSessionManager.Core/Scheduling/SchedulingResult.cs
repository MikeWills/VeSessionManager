namespace VeSessionManager.Core.Scheduling;

/// <summary>Per-run counters, logged by the scheduling job so JobRunHistory stays a one-line summary.</summary>
public class SchedulingResult
{
    public int SessionsSynced { get; set; }
    public int SessionsCleanedUp { get; set; }
    public int SessionsFailed { get; set; }

    public override string ToString() =>
        $"synced {SessionsSynced}, cleaned up {SessionsCleanedUp}, failed {SessionsFailed}";
}
