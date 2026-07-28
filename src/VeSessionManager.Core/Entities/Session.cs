namespace VeSessionManager.Core.Entities;

public class Session
{
    public int Id { get; set; }

    /// <summary>External reference into ExamTools/HamStudy.</summary>
    public required string ExamToolsSessionId { get; set; }

    public required string Title { get; set; }

    public DateTime ScheduledStartUtc { get; set; }

    /// <summary>From ExamTools' sessionDef.duration (seconds), converted at ingestion time. Not in the original shared data model — added in Phase 2 because both the Zoom meeting and the Discord event require an explicit length/end time.</summary>
    public int DurationMinutes { get; set; }

    // Populated once Phase 2 creates the Zoom meeting/Discord event.
    public string? ZoomMeetingId { get; set; }
    public string? ZoomJoinUrl { get; set; }
    public string? DiscordEventId { get; set; }

    /// <summary>The ScheduledStartUtc value last successfully pushed to *both* Zoom and Discord. Null means never synced (a brand-new session). Mismatching ScheduledStartUtc is exactly the "needs Zoom/Discord create-or-update" signal Phase 2's scheduling job scans for — no separate event queue needed.</summary>
    public DateTime? ZoomDiscordSyncedStartUtc { get; set; }

    /// <summary>Denormalized copy for easy filtering/reporting without joining through FeeConfiguration.</summary>
    public int VecId { get; set; }
    public Vec Vec { get; set; } = null!;

    /// <summary>Not in the original shared data model — added as a multi-team foundation. Which team operationally ran this session (owns its ExamTools/Zoom/Discord/Square credentials) — independent of VecId (which VEC/fee schedule applies). See Team's own doc comment for why these are separate, unrelated FKs.</summary>
    public int TeamId { get; set; }
    public Team Team { get; set; } = null!;

    /// <summary>Snapshot of whichever config was active when the session was created, so historical sessions keep an accurate fee record even after rates change.</summary>
    public int FeeConfigurationId { get; set; }
    public FeeConfiguration FeeConfiguration { get; set; } = null!;

    public SessionStatus Status { get; set; } = SessionStatus.Active;
    public DateTime? CancelledUtc { get; set; }

    /// <summary>Set when a reschedule is detected while the session already has candidates — a "something needs a human" flag, not an automatic action.</summary>
    public bool RescheduleFlaggedForReview { get; set; }
    public DateTime? RescheduleFlaggedUtc { get; set; }

    /// <summary>Set by the Session Manager's "mark session as completed" action; bulk-flips Candidate.Tested = true for every non-terminal candidate in the session.</summary>
    public DateTime? TestingCompletedUtc { get; set; }
    public int? TestingCompletedByUserId { get; set; }
    public User? TestingCompletedByUser { get; set; }

    /// <summary>Renamed from Arrl* to Vec* (Phase 8) — submission goes to whichever VEC this session is actually under (VecId), not always ARRL specifically.</summary>
    public VecSubmissionStatus VecSubmissionStatus { get; set; } = VecSubmissionStatus.NotSubmitted;
    public DateTime? VecSubmittedDate { get; set; }
    public int? VecSubmittedByUserId { get; set; }
    public User? VecSubmittedByUser { get; set; }

    public DateTime CreatedUtc { get; set; }

    public List<Candidate> Candidates { get; } = [];
    public List<SessionVolunteerExaminer> SessionVolunteerExaminers { get; } = [];

    /// <summary>True once the session's scheduled window has fully elapsed — used to keep
    /// backfilled/late-ingested past sessions (see SessionIngestionService's completed-session
    /// backfill) from triggering live Zoom/Discord scheduling or a "you're registered" email for
    /// something that already happened. Not EF-mapped, computed on demand — always call with the
    /// same TimeProvider-sourced `now` a service is already using, not DateTime.UtcNow directly.</summary>
    public bool HasEnded(DateTime now) => ScheduledStartUtc.AddMinutes(DurationMinutes) <= now;
}
