namespace VeSessionManager.Core.Entities;

public class JobRunHistory
{
    public int Id { get; set; }
    public required string JobName { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>Not in the original shared data model — added as a multi-team foundation. Null for jobs that aren't per-team (e.g. UlsWatcherJob, the global Zoom/Discord/Square/Email steps still shared across all teams pending their own fast-follow) — set for per-team runs (e.g. SessionIngestionJob's per-team loop) so the future ops dashboard (Phase 9) can filter by team.</summary>
    public int? TeamId { get; set; }
    public Team? Team { get; set; }
}
