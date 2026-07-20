namespace VeSessionManager.Core.Ingestion;

/// <summary>Per-run counters, logged by the polling job so JobRunHistory stays a one-line summary.</summary>
public class IngestionResult
{
    public int SessionsAdded { get; set; }
    public int SessionsRescheduled { get; set; }
    public int SessionsFlaggedForReview { get; set; }
    public int SessionsCancelled { get; set; }
    public int SessionsSkippedNoConfig { get; set; }
    public int CandidatesAdded { get; set; }
    public int CandidatesUpdated { get; set; }

    public override string ToString() =>
        $"sessions: +{SessionsAdded} rescheduled {SessionsRescheduled} flagged {SessionsFlaggedForReview} " +
        $"cancelled {SessionsCancelled} skipped(no config) {SessionsSkippedNoConfig}; " +
        $"candidates: +{CandidatesAdded} updated {CandidatesUpdated}";
}
