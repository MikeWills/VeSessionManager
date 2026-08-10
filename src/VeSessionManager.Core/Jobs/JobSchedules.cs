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
/// Which SystemSettings values (if any) override a job's configured schedule at runtime, so the admin
/// page reads the same numbers the Worker obeys instead of trusting configuration alone.
/// </summary>
public enum JobSettingsSource
{
    None,

    /// <summary>
    /// <c>UlsWatcherStartHourEt</c> / <c>UlsWatcherIntervalHours</c> — shared by the ULS watcher and
    /// the renewal monitor, which run on one schedule by design (2026-08-06): both read the same FCC
    /// data through the same mirror, so checking one four times a day and the other once meant a
    /// renewal could sit unnoticed for most of a day after it was already visible.
    /// </summary>
    UlsWatcher,

    /// <summary><c>SessionIngestionIntervalMinutes</c> — the per-team cadence, not the job's tick.</summary>
    SessionIngestion
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
/// <param name="SettingsSource">Which SystemSettings values override the configured schedule, if any.</param>
/// <param name="TickIntervalSeconds">
/// How often the job's timer wakes, when that differs from how often it actually *does* the work.
/// Session ingestion ticks every few minutes but only polls a team once its own interval has elapsed,
/// so reporting the tick as the cadence overstates it by an order of magnitude — the page shows both.
/// Stated for every job, including the ones whose tick *is* their run: the ticks differ job to job,
/// and "these two happen to coincide" is itself worth being able to read off the page.
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
    JobSettingsSource SettingsSource = JobSettingsSource.None,
    int? TickIntervalSeconds = null);

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
    public const string VeLicenseWatch = "VeLicenseWatch";
    public const string Reconciliation = "Reconciliation";

    /// <summary>Eastern hour the renewal-monitor refresh is pinned to — after FCC's 02:00 ET run, before the morning.</summary>
    public const int LicenseWatchStartHourEt = 6;

    public static IReadOnlyList<JobScheduleDescriptor> All { get; } =
    [
        // The configured seconds are the TICK. Each tick asks per team whether
        // SystemSettings.SessionIngestionIntervalMinutes has elapsed since that team's last run, so
        // the cadence a user cares about is the setting, not this number.
        new(SessionIngestion,
            "Session ingestion",
            "Polls ExamTools for each team's sessions and candidates, then runs the rest of the per-team pipeline.",
            JobCadenceKind.IntervalFromWorkerStart,
            "Jobs:SessionIngestionIntervalSeconds",
            DefaultIntervalSeconds: 300,
            SettingsSource: JobSettingsSource.SessionIngestion,
            TickIntervalSeconds: 300),

        new(Reconciliation,
            "ExamTools reconciliation",
            "Compares ExamTools' closed-session feed against this app's own data and records anything missing. Read-only — it reports, it never repairs.",
            JobCadenceKind.IntervalFromWorkerStart,
            "Jobs:ReconciliationIntervalHours",
            DefaultIntervalHours: 24,
            TickIntervalSeconds: 86400),

        new(HistoricalImport,
            "Historical import",
            "Works the queue of requested back-fills, one chunk at a time. Idle unless an import was requested.",
            JobCadenceKind.IntervalFromWorkerStart,
            "Jobs:HistoricalImportIntervalSeconds",
            DefaultIntervalSeconds: 60,
            TickIntervalSeconds: 60),

        new(DayBeforeReminder,
            "Day-before reminder",
            "Emails candidates whose session is tomorrow.",
            JobCadenceKind.IntervalFromWorkerStart,
            "Jobs:DayBeforeReminderIntervalHours",
            DefaultIntervalHours: 24,
            TickIntervalSeconds: 86400),

        new(PaymentReminder,
            "Payment reminder",
            "Chases unpaid exam fees and expires payment links that have gone stale.",
            JobCadenceKind.IntervalFromWorkerStart,
            "Jobs:PaymentReminderIntervalHours",
            DefaultIntervalHours: 24,
            TickIntervalSeconds: 86400),

        new(SquareLinkPurge,
            "Square link purge",
            "Removes payment links left unpaid past the team's retention window.",
            JobCadenceKind.IntervalFromWorkerStart,
            "Jobs:SquareLinkPurgeIntervalHours",
            DefaultIntervalHours: 24,
            TickIntervalSeconds: 86400),

        new(PiiPurge,
            "PII purge",
            "Clears candidate personal data once past the retention period.",
            JobCadenceKind.IntervalFromWorkerStart,
            "Jobs:PiiPurgeIntervalHours",
            DefaultIntervalHours: 24,
            TickIntervalSeconds: 86400),

        new(UlsWatcher,
            "ULS watcher",
            "Checks FCC ULS for candidates' applications and new licenses.",
            JobCadenceKind.AnchoredToEasternHour,
            "Jobs:UlsWatcherIntervalHours",
            DefaultIntervalHours: 12,
            StartHourEt: 8,
            SettingsSource: JobSettingsSource.UlsWatcher,
            TickIntervalSeconds: 3600),

        // Shares the ULS watcher's schedule outright (2026-08-06). It was 06:00 ET once a day while
        // the watcher ran every 6 hours, so a renewal already visible in the mirror could go unseen
        // for most of a day. Same data, same source — one schedule.
        new(LicenseWatch,
            "Renewal monitor refresh",
            "Refreshes the watched call signs on the Renewal Monitor from ExamTools' ULS mirror.",
            JobCadenceKind.AnchoredToEasternHour,
            "Jobs:UlsWatcherIntervalHours",
            DefaultIntervalHours: 12,
            StartHourEt: 8,
            SettingsSource: JobSettingsSource.UlsWatcher,
            TickIntervalSeconds: 3600),

        // Runs inside the same tick as the entry above and on the same slot — same nightly FCC data
        // through the same mirror. Listed separately because it writes its own JobRunHistory row, so
        // the page would otherwise report a job that visibly runs and has no schedule.
        new(VeLicenseWatch,
            "VE license refresh",
            "Refreshes the license state of VEs on active team rosters from ExamTools' ULS mirror.",
            JobCadenceKind.AnchoredToEasternHour,
            "Jobs:UlsWatcherIntervalHours",
            DefaultIntervalHours: 12,
            StartHourEt: 8,
            SettingsSource: JobSettingsSource.UlsWatcher,
            TickIntervalSeconds: 3600)
    ];

    /// <summary>
    /// Falls back when a stored schedule value is unusable. A SystemSettings row that was never filled
    /// in holds 0, which is not "run constantly" — it is a division by zero in the slot arithmetic, and
    /// it would take the whole Job Schedule page down as well as stopping the job. The admin form's
    /// <c>min="1"</c> only guards the browser.
    /// </summary>
    public static int IntervalOrDefault(int hours, int fallbackHours) => hours > 0 ? hours : fallbackHours;

    /// <summary>Same idea for an anchor hour, which must be a real hour of the day.</summary>
    public static int StartHourOrDefault(int hourEt, int fallbackHourEt) =>
        hourEt is >= 0 and < 24 ? hourEt : fallbackHourEt;

    public static JobScheduleDescriptor For(string jobName) =>
        All.FirstOrDefault(d => d.JobName == jobName)
        ?? throw new ArgumentOutOfRangeException(nameof(jobName), jobName, "No schedule descriptor is registered for this job.");
}
