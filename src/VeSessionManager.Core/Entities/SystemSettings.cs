namespace VeSessionManager.Core.Entities;

/// <summary>Deployment-wide settings, single row (Id = 1). Not per-team — see Team for per-team credentials/settings.</summary>
public class SystemSettings
{
    public int Id { get; set; }

    /// <summary>Phase 10's PII purge job input. Null means "not yet set" per spec.md: no default is assumed, an admin must set this explicitly before the purge job can run.</summary>
    public int? PiiRetentionWindowDays { get; set; }

    public int FccDailyWatcherIntervalHours { get; set; }
    public int FccWeeklyCatchupIntervalHours { get; set; }
    public DayOfWeek FccWeeklyCatchupDayOfWeek { get; set; }

    /// <summary>
    /// The "normal" per-team cadence (minutes) for SessionIngestionJob's full per-team pipeline
    /// (ingestion, VE roster sync, Zoom/Discord scheduling, Square payment links, confirmation
    /// emails) when none of that team's sessions are imminent. Default 60 — most teams run a
    /// session once a day or less, so polling ExamTools every 5 minutes around the clock has no
    /// real upside almost all the time. SessionIngestionJob automatically "surges" to its own tick
    /// interval (Jobs:SessionIngestionIntervalSeconds, still 5 min by default) for any team with an
    /// Active session starting within the next 60 minutes or still within its Duration, so a
    /// last-minute registrant is still caught quickly — see IngestionScheduleService.
    /// </summary>
    public int SessionIngestionIntervalMinutes { get; set; }

    public int? UpdatedByUserId { get; set; }
    public User? UpdatedByUser { get; set; }
    public DateTime? UpdatedUtc { get; set; }
}
