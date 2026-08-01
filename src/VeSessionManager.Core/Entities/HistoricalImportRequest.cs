namespace VeSessionManager.Core.Entities;

/// <summary>
/// Issue #67 part 2: a queued, one-off request to import a team's completed sessions over a chosen
/// date range — typically "January 1 through July 31, so the stats page has a full year to work
/// with". Deliberately a *request row* rather than work done inline in the web request.
///
/// The Web app and the Worker are separate processes sharing one SQLite file. Web writes a Pending
/// row; HistoricalImportJob in the Worker picks it up on its next tick and processes it in chunks.
/// That means: the admin isn't held on a spinner for a year of API calls, a browser navigation or an
/// app recycle can't abandon a half-finished import, and ExamTools polling stays owned by the one
/// process that already owns it rather than two hammering it concurrently.
///
/// Progress is on the row itself (chunk counters), so the page can report it without inventing a
/// second channel — the per-chunk JobRunHistory entries exist too, but they're for the ops
/// dashboard, not for this page's progress bar.
/// </summary>
public class HistoricalImportRequest
{
    public int Id { get; set; }

    public int TeamId { get; set; }
    public Team? Team { get; set; }

    /// <summary>Inclusive start of the range to import, as a plain calendar date — ExamTools' closed-session feed takes DateOnly bounds, so no timezone conversion is involved or wanted.</summary>
    public DateOnly StartDate { get; set; }

    /// <summary>Inclusive end of the range to import.</summary>
    public DateOnly EndDate { get; set; }

    public HistoricalImportStatus Status { get; set; } = HistoricalImportStatus.Pending;

    public int RequestedByUserId { get; set; }
    public User? RequestedByUser { get; set; }

    public DateTime RequestedUtc { get; set; }
    public DateTime? StartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }

    /// <summary>How many month-sized chunks the range was split into, and how many have finished — the progress the page shows.</summary>
    public int ChunksTotal { get; set; }
    public int ChunksCompleted { get; set; }

    public int SessionsImported { get; set; }
    public int CandidatesImported { get; set; }

    /// <summary>Set when Status is Failed. Truncated by the service — an ExamTools stack trace is not something to render on an admin page.</summary>
    public string? ErrorMessage { get; set; }
}

public enum HistoricalImportStatus
{
    /// <summary>Queued; the Worker has not picked it up yet.</summary>
    Pending,

    /// <summary>The Worker is working through the chunks right now.</summary>
    Running,

    Completed,

    /// <summary>A chunk threw and the run stopped. Whatever earlier chunks imported is kept — ingestion is idempotent, so re-queueing the same range resumes rather than duplicating.</summary>
    Failed
}
