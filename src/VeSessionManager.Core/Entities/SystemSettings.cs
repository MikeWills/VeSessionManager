namespace VeSessionManager.Core.Entities;

/// <summary>Deployment-wide settings, single row (Id = 1). Not per-team — see Team for per-team credentials/settings.</summary>
public class SystemSettings
{
    public int Id { get; set; }

    /// <summary>Phase 10's PII purge job input. Null means "not yet set" per spec.md: no default is assumed, an admin must set this explicitly before the purge job can run.</summary>
    public int? PiiRetentionWindowDays { get; set; }

    /// <summary>
    /// How many years a VE may be inactive before the retention purge clears their contact details
    /// (#313 / L-07). Null means "not yet set", and the VE pass is skipped entirely — the same
    /// explicit-opt-in rule as <see cref="PiiRetentionWindowDays"/>, and for a stronger reason here:
    /// nobody expects a volunteer roster to start forgetting people because a job shipped.
    ///
    /// <para>Years, not days, because the two answer different questions. A candidate's window is
    /// tied to an FCC process that finishes in weeks; a VE's is tied to "have they stopped
    /// volunteering", which is only legible over years. Five is the documented suggestion.</para>
    /// </summary>
    public int? VeContactRetentionYears { get; set; }

    /// <summary>
    /// How many days of audit history to keep (#86). Null means keep forever, and that is the
    /// shipped default — every existing deployment stays exactly as it was until an admin chooses
    /// otherwise, which is the only safe default for a table whose value is that it is complete.
    ///
    /// <para>These rows carry no PII by design — ids, counts and config values, with secrets and
    /// candidate names/emails/FRNs deliberately excluded — so this is a growth control, not a
    /// privacy one. That is also why it is opt-in rather than a sensible-looking default: nothing
    /// here <i>needs</i> deleting, so nobody's history should start disappearing because a job
    /// shipped.</para>
    ///
    /// <para><b>Turning this on makes the first and only legitimate delete path against AuditLogs
    /// live.</b> See docs/audit-log.md — append-only is a convention enforced by the absence of such
    /// a path, and <c>AuditLogAppendOnlyTests</c> exempts exactly one call site by name.</para>
    /// </summary>
    public int? AuditLogRetentionDays { get; set; }

    /// <summary>
    /// How many days of job-run history to keep (#296). Null means keep forever, same opt-in rule as
    /// <see cref="AuditLogRetentionDays"/>, though the argument is weaker here: these rows are
    /// operational telemetry with no evidentiary value, and TeamPipeline writes six per team per
    /// tick — roughly 150k rows a year on this deployment.
    /// </summary>
    public int? JobRunHistoryRetentionDays { get; set; }

    /// <summary>
    /// How many days the files filed with ARRL-VEC are kept (#197) — the VEC archive and any second
    /// document that went with it. Null means keep forever, and that is the shipped default: the same
    /// opt-in rule as the settings above, and with a stronger case here, since these are the legal
    /// record of what was filed and Mike has had to go back to one after the fact.
    ///
    /// <para><b>Only the files age out; the submission row never does.</b> The row is the record that
    /// a filing happened, and for a submission whose receipt could not be read it is the only account
    /// of what went.</para>
    ///
    /// <para>⚠️ <b>A window longer than PiiRetentionWindowDays or VeContactRetentionYears means the
    /// archive outlives those purges</b> — the zip is the session's paperwork and carries candidate
    /// PII. That may be the right answer, since a filing record plausibly outranks a retention policy,
    /// but it is a deliberate exception rather than an oversight. Note the receipt stored in
    /// ArrlVecSubmission.ResponseBody is <b>not</b> covered by this window: it is a database column
    /// rather than a file, it carries the submitting VE's contact details, and whether it should age
    /// out with them is an open question on #197.</para>
    /// </summary>
    public int? VecSubmissionArchiveRetentionDays { get; set; }

    /// <summary>
    /// Hours between UlsWatcherJob checks, anchored to UlsWatcherStartHourEt rather than Worker start
    /// time (default 8/12 -> checks at 08:00 and 20:00 ET). Anchored to wall-clock ET because FCC
    /// issues licenses at 02:00 ET, so a morning slot lands after that day's grants exist.
    /// See docs/uls-watcher.md.
    /// </summary>
    public int UlsWatcherIntervalHours { get; set; }

    /// <summary>First ULS check of the day, in US Eastern hour-of-day (0-23). Default 8.</summary>
    public int UlsWatcherStartHourEt { get; set; }

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

    // ---- Deployment-wide ("system") SMTP, added 2026-08-01 for password reset ----
    // Every other email in this app is candidate-facing and sends from the Team that owns the
    // session, via Team.ToEmailCredentials(). A password reset is not team-scoped: it's addressed to
    // an app *user*, and a SystemAdmin may belong to no team at all, so there is no team credential
    // to reach for. These fields are that missing sender. Per-team SMTP is untouched and still owns
    // all candidate mail. See docs/password-reset.md.

    public string? SystemSmtpHost { get; set; }
    public int? SystemSmtpPort { get; set; }
    public string? SystemSmtpUsername { get; set; }

    /// <summary>Encrypted at rest via EncryptedStringConverter, same as Team's credential columns.</summary>
    public string? SystemSmtpPassword { get; set; }

    public bool? SystemSmtpUseStartTls { get; set; }

    /// <summary>Envelope From for system mail. Falls back to SystemSmtpUsername when unset.</summary>
    public string? SystemSmtpFromAddress { get; set; }

    public string? SystemSmtpFromDisplayName { get; set; }

    /// <summary>
    /// "An admin actually finished setup", not "a hostname is present" — the same distinction the
    /// SmtpUsername gotcha in CLAUDE.md's Known Constraints exists for. Password reset is gated on
    /// this: with no system sender configured, the forgot-password page says so plainly instead of
    /// throwing a MailKit authentication error on every attempt.
    /// </summary>
    public bool IsSystemEmailConfigured =>
        !string.IsNullOrWhiteSpace(SystemSmtpHost) && !string.IsNullOrWhiteSpace(SystemSmtpUsername);

    public int? UpdatedByUserId { get; set; }
    public User? UpdatedByUser { get; set; }
    public DateTime? UpdatedUtc { get; set; }
}
