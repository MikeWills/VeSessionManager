namespace VeSessionManager.Core.Jobs;

/// <summary>
/// How a background job decides when to run. Two genuinely different shapes, and the difference is
/// the whole reason this type exists rather than a single "interval" number.
/// </summary>
public enum JobCadenceKind
{
    /// <summary>
    /// A <c>PeriodicTimer</c> started when the Worker process did, so its run times drift with every
    /// restart. The next run is knowable only as "the last one plus the interval", and only while
    /// the process stays up.
    /// </summary>
    IntervalFromWorkerStart,

    /// <summary>
    /// Pinned to a wall-clock hour in US Eastern (see <see cref="Uls.DailySlotSchedule"/>). Ticks
    /// hourly, but only *works* when the current slot has no successful run yet, so the schedule
    /// survives restarts and outages and the next run is genuinely predictable.
    /// </summary>
    AnchoredToEasternHour
}

/// <summary>
/// One background job's identity and schedule — the single definition shared by the Worker (which
/// obeys it) and the Web admin Job Schedule page (which reports it).
///
/// <para><b>Why a registry rather than each job owning its own numbers.</b> Every interval used to be
/// a literal at the job's own call site, invisible to Web — which cannot reference the Worker project
/// at all. A schedule page built by re-typing those numbers would be wrong the first time anyone
/// changed one, and wrong silently: a screen confidently reporting the wrong next-run time is worse
/// than no screen. Same lesson as <c>TeamPipeline</c> (the pipeline order that drifted while it was
/// written out three times).</para>
/// </summary>
/// <param name="JobName">
/// Must match the string passed to <c>JobRunHistoryLogger.RunAsync</c> exactly — it is the join key
/// back to <c>JobRunHistory</c>, which is where "when did this last run?" is answered.
/// </param>
/// <param name="DisplayName">Human-facing name for the admin page.</param>
/// <param name="Description">One line on what the job does, for the same page.</param>
/// <param name="Kind">Which of the two schedule shapes above.</param>
/// <param name="IntervalConfigKey">
/// Configuration key holding the interval, or null for a job whose interval is a constant.
/// **These now live in <c>src/Shared/appsettings.Shared.json</c>**, not the Worker's own appsettings:
/// Web resolves the same key to build this page, and a key present in only one host is precisely how
/// T04 happened.
/// </param>
/// <param name="DefaultIntervalHours">Fallback when the config key is absent. Null for sub-hour jobs.</param>
/// <param name="DefaultIntervalSeconds">Fallback for the two jobs that poll far more often than hourly.</param>
/// <param name="StartHourEt">Anchored jobs only: the Eastern hour the schedule is pinned to.</param>
/// <param name="SettingsBacked">
/// True when a SystemSettings row overrides the configured values at runtime, so the page must read
/// the database rather than trusting configuration alone.
/// </param>
public sealed record JobScheduleDescriptor(
    string JobName,
    string DisplayName,
    string Description,
    JobCadenceKind Kind,
    string? IntervalConfigKey = null,
    int? DefaultIntervalHours = null,
    int? DefaultIntervalSeconds = null,
    int? StartHourEt = null,
    bool SettingsBacked = false);

/// <summary>
/// Every scheduled background job in this deployment. Adding a job means adding it here too —
/// otherwise it runs but never appears on the Job Schedule page, which is the one screen that claims
/// to be complete.
/// </summary>
public static class JobSchedules
{
    public const string SessionIngestion = "SessionIngestion";
    public const string HistoricalImport = "HistoricalImport";
    public const string DayBeforeReminder = "DayBeforeReminder";
    public const string PaymentReminder = "PaymentReminder";
    public const string SquareLinkPurge = "SquareLinkPurge";
    public const string PiiPurge = "PiiPurge";
    public const string UlsWatcher = "UlsWatcher";
    public const string LicenseWatch = "LicenseWatch";

    /// <summary>Eastern hour the renewal-monitor refresh is pinned to — after FCC's 02:00 ET run, before the morning.</summary>
    public const int LicenseWatchStartHourEt = 6;

    public static IReadOnlyList<JobScheduleDescriptor> All { get; } =
    [
        new(SessionIngestion,
            "Session ingestion",
            "Polls ExamTools for each team's sessions and candidates, then runs the rest of the per-team pipeline.",
            JobCadenceKind.IntervalFromWorkerStart,
            "Jobs:SessionIngestionIntervalSeconds",
            DefaultIntervalSeconds: 300),

        new(HistoricalImport,
            "Historical import",
            "Works the queue of requested back-fills, one chunk at a time. Idle unless an import was requested.",
            JobCadenceKind.IntervalFromWorkerStart,
            "Jobs:HistoricalImportIntervalSeconds",
            DefaultIntervalSeconds: 60),

        new(DayBeforeReminder,
            "Day-before reminder",
            "Emails candidates whose session is tomorrow.",
            JobCadenceKind.IntervalFromWorkerStart,
            "Jobs:DayBeforeReminderIntervalHours",
            DefaultIntervalHours: 24),

        new(PaymentReminder,
            "Payment reminder",
            "Chases unpaid exam fees and expires payment links that have gone stale.",
            JobCadenceKind.IntervalFromWorkerStart,
            "Jobs:PaymentReminderIntervalHours",
            DefaultIntervalHours: 24),

        new(SquareLinkPurge,
            "Square link purge",
            "Removes payment links left unpaid past the team's retention window.",
            JobCadenceKind.IntervalFromWorkerStart,
            "Jobs:SquareLinkPurgeIntervalHours",
            DefaultIntervalHours: 24),

        new(PiiPurge,
            "PII purge",
            "Clears candidate personal data once past the retention period.",
            JobCadenceKind.IntervalFromWorkerStart,
            "Jobs:PiiPurgeIntervalHours",
            DefaultIntervalHours: 24),

        new(UlsWatcher,
            "ULS watcher",
            "Checks FCC ULS for candidates' applications and new licences.",
            JobCadenceKind.AnchoredToEasternHour,
            "Jobs:UlsWatcherIntervalHours",
            DefaultIntervalHours: 12,
            StartHourEt: 8,
            SettingsBacked: true),

        new(LicenseWatch,
            "Renewal monitor refresh",
            "Refreshes the watched call signs on the Renewal Monitor from ExamTools' ULS mirror.",
            JobCadenceKind.AnchoredToEasternHour,
            StartHourEt: LicenseWatchStartHourEt,
            DefaultIntervalHours: 24)
    ];

    public static JobScheduleDescriptor For(string jobName) =>
        All.FirstOrDefault(d => d.JobName == jobName)
        ?? throw new ArgumentOutOfRangeException(nameof(jobName), jobName, "No schedule descriptor is registered for this job.");
}
