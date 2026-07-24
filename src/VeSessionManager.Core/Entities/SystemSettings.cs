namespace VeSessionManager.Core.Entities;

/// <summary>Deployment-wide settings, single row (Id = 1). Not per-team — see Team for per-team credentials/settings.</summary>
public class SystemSettings
{
    public int Id { get; set; }

    /// <summary>Phase 10's PII purge job input. Null means "not yet set" per spec.md: no default is assumed, an admin must set this explicitly before the purge job can run.</summary>
    public int? PiiRetentionWindowDays { get; set; }

    /// <summary>
    /// Hours between FccDailyWatcherJob retry checks, anchored to FccDailyWatcherStartHourEt rather
    /// than Worker start time (default 8/12 -> checks at 8am and 8pm ET). FCC publishes each day's
    /// file ~5am ET; a fixed Worker-start-relative timer could tick before that window and then not
    /// retry until the same day-name file rolls around again next week. See docs/fcc-uls-watcher.md.
    /// </summary>
    public int FccDailyWatcherIntervalHours { get; set; }

    /// <summary>First daily-watcher check of the day, in US Eastern hour-of-day (0-23). Default 8 —
    /// comfortably after FCC's ~5am ET publish window with margin for a late publish.</summary>
    public int FccDailyWatcherStartHourEt { get; set; }

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

    /// <summary>
    /// While on, SmtpEmailSender redirects every real send (registration confirmations, reminders,
    /// felony-disclosure/youth-program instructions, payment expiration notices — everything) to
    /// TestModeOverrideEmail instead of the real candidate/admin recipient, so a team can run real
    /// ExamTools data through the full pipeline without emailing anyone for real. Deployment-wide,
    /// not per-team, same as every other SystemSettings field — while it's on, nothing anywhere
    /// sends a real email, regardless of which team's data is being exercised.
    /// </summary>
    public bool TestModeEnabled { get; set; }

    /// <summary>Required whenever TestModeEnabled is true (enforced by SystemSettingsService.UpdateAsync) — every redirected email lands here.</summary>
    public string? TestModeOverrideEmail { get; set; }

    public int? UpdatedByUserId { get; set; }
    public User? UpdatedByUser { get; set; }
    public DateTime? UpdatedUtc { get; set; }
}
