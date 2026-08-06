namespace VeSessionManager.Core.Entities;

public class JobRunHistory
{
    public int Id { get; set; }
    public required string JobName { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// What the run actually did, as the job's own result summary — "sent 0, failed 1",
    /// "reminders sent 0, expirations processed 0", and so on. Null for jobs whose work returns
    /// nothing to summarise, and for runs that threw (where <see cref="ErrorMessage"/> is the story).
    ///
    /// <para><b>Why this exists.</b> Success/ErrorMessage alone made three very different outcomes
    /// identical on the ops dashboard: sent five emails, sent none because nothing qualified, and
    /// sent none because every attempt failed. A job is marked Success when the *job* completes, and
    /// per-item failures are caught inside it deliberately — one bad address must not abort the
    /// batch — so a wall of green rows was hiding every failing send. Cost a real evening of
    /// debugging on 2026-08-05, when the summary line the Worker log already printed
    /// ("sent 0, failed 1") simply never reached the database.</para>
    /// </summary>
    public string? ResultSummary { get; set; }

    /// <summary>Not in the original shared data model — added as a multi-team foundation. Null for jobs that aren't per-team (e.g. UlsWatcherJob, the global Zoom/Discord/Square/Email steps still shared across all teams pending their own fast-follow) — set for per-team runs (e.g. SessionIngestionJob's per-team loop) so the future ops dashboard (Phase 9) can filter by team.</summary>
    public int? TeamId { get; set; }
    public Team? Team { get; set; }
}
