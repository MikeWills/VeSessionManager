namespace VeSessionManager.Core.Entities;

public class Candidate
{
    /// <summary>
    /// Withdrew or did not show. Their row is kept for statistics but their personal details are
    /// purged immediately rather than on the retention schedule, so anything reading a name, email
    /// or FRN off a withdrawn candidate is reading a hole — see docs/pii-purge.md.
    ///
    /// <para>Not EF-mapped; <c>ApplicationStatus == NotTested</c> is the stored form and is what a
    /// query must spell out. This exists so the *meaning* has one definition: eight call sites made
    /// that comparison by hand, and a future status that also means "didn't test" would have to find
    /// every one of them.</para>
    /// </summary>
    public bool IsWithdrawn => ApplicationStatus == CandidateApplicationStatus.NotTested;

    public int Id { get; set; }

    public int SessionId { get; set; }
    public Session Session { get; set; } = null!;

    /// <summary>External applicant id from ExamTools/HamStudy — the stable key the ingestion job diffs against on re-polls. Null only for rows created manually.</summary>
    public string? ExamToolsApplicantId { get; set; }

    // Nullable because the PII purge job (Phase 10) and the immediate no-show/withdrawal
    // delete action (Phase 9) null these fields out while keeping the row for stats.
    public string? Name { get; set; }

    /// <summary>Not in the original shared data model — added in Phase 4 so candidate emails can open with "Hi {{CandidateFirstName}}," rather than the full "First Middle Last Suffix" from Name. Sourced directly from ExamTools' separate firstname field, not parsed back out of Name.</summary>
    public string? FirstName { get; set; }
    public string? Email { get; set; }

    /// <summary>Normally required before testing, but VECs have allowed testing without one during exceptional circumstances (e.g. federal shutdowns).</summary>
    public string? Frn { get; set; }

    /// <summary>Flags the no-FRN-at-registration case for a later batch export/VEC follow-up.</summary>
    public bool FrnMissingAtRegistration { get; set; }

    /// <summary>Captured from the exam application data if the ExamTools/HamStudy API exposes it. Treated as sensitive PII, purged alongside Name/Email/Frn.</summary>
    public bool? HasFelonyDisclosure { get; set; }

    public DateTime DateRegisteredUtc { get; set; }

    public CandidateApplicationStatus ApplicationStatus { get; set; } = CandidateApplicationStatus.Unmatched;

    /// <summary>Flips to true when the Session Manager marks the whole session as completed, or automatically once ExamResultSyncService sees a graded exam result for this candidate — whichever happens first. Intentionally separate from ApplicationStatus.</summary>
    public bool Tested { get; set; }

    /// <summary>From ULS HD status date — only applies to the Received/Granted path.</summary>
    public DateTime? ApplicationDateEnteredUtc { get; set; }

    public string? CallSign { get; set; }
    public DateTime? LicenseGrantDateUtc { get; set; }

    /// <summary>FCC ULS "Unique System Identifier" from the matched license record — the same value ULS's own web UI calls `licKey` (e.g. `https://wireless2.fcc.gov/UlsApp/UlsSearch/license.jsp?licKey=...`). Set alongside CallSign/LicenseGrantDateUtc by UlsWatcherService; not PII (a public FCC record locator, same privacy class as CallSign), so not cleared by CandidatePiiFields.Clear.</summary>
    public string? FccUlsLicenseKey { get; set; }

    /// <summary>ULS application file number for the candidate's pending application (e.g. `0012131564`), from the ULS lookup's pendingApplications block. Surfaced on Applicant Status so a Session Manager can look the application up in FCC's own Application Search — the pre-grant counterpart to FccUlsLicenseKey. Not PII, same reasoning as that field.</summary>
    public string? UlsApplicationFileNumber { get; set; }

    /// <summary>Whether FCC is currently holding this candidate's application for Red Light (unpaid fee, if lingering past normal) or Basic Qualification (character) review — refreshed every UlsWatcherService run from the ULS lookup's pending-application history codes (previously FCC's own HS.dat), cleared back to None once no longer reported (see ApplicationStatus). Only meaningful while ApplicationStatus is Unmatched/Received.</summary>
    public FccApplicationHoldReason FccHoldReason { get; set; } = FccApplicationHoldReason.None;

    /// <summary>Whether FCC's fee-payment verification step has confirmed this candidate's application fee — refreshed alongside FccHoldReason, same source (HS.dat) and same non-terminal-window caveat.</summary>
    public FccApplicationPaymentStatus FccPaymentStatus { get; set; } = FccApplicationPaymentStatus.Unknown;

    /// <summary>License class held walking into this session's exam (None = not previously licensed). Set alongside NewLicenseClass — see ExamResultSyncService.</summary>
    public LicenseClass? InitialLicenseClass { get; set; }

    /// <summary>License class earned by this sitting's passed element(s) — only set once every graded element this sitting passed (a candidate marked Failed never gets one). Set by ExamResultSyncService from the exam elements ExamTools reports as graded+passed; also backfills existing already-Tested candidates that predate this field.</summary>
    public LicenseClass? NewLicenseClass { get; set; }

    public int? ResultMarkedByUserId { get; set; }
    public User? ResultMarkedByUser { get; set; }
    public DateTime? ResultMarkedUtc { get; set; }

    public DateTime? PiiPurgedUtc { get; set; }

    /// <summary>Not in the original shared data model — added in Phase 4 so CandidateNotificationService's scans are idempotent (send-once) the same way Phase 2/3's ...SentUtc/SyncedUtc fields are, rather than needing a separate outbox table.</summary>
    public DateTime? RegistrationConfirmationSentUtc { get; set; }

    /// <summary>See RegistrationConfirmationSentUtc — prevents a daily job restart from re-sending the same day's reminder.</summary>
    public DateTime? DayBeforeReminderSentUtc { get; set; }

    /// <summary>Not in the original shared data model — added in Phase 6. Set once when ApplicationStatus has stayed Unmatched for longer than PaymentReminderOptions.UnmatchedReviewWindowDays past DateRegisteredUtc, per the spec's "flag separately for manual review" note (no FCC application date exists yet to gate a payment reminder on). Surfaced today only via a WARNING log line — Phase 9's admin UI doesn't exist yet to show it anywhere else.</summary>
    public DateTime? UnmatchedReviewFlaggedUtc { get; set; }

    /// <summary>Not in the original shared data model — added post-launch so the session detail page's "Email history" modal can show this send. Unlike RegistrationConfirmationSentUtc/DayBeforeReminderSentUtc this isn't an idempotency guard (the send itself is already one-shot, gated by SessionActionService.MarkCompletedAsync's own "candidates just tested" set) — purely a display timestamp.</summary>
    public DateTime? FelonyDisclosureInstructionsSentUtc { get; set; }

    /// <summary>See FelonyDisclosureInstructionsSentUtc — same "display timestamp, not a send guard" reasoning. Unlike that one, this action has no cap and can be clicked more than once; this always holds the *most recent* send, not the first.</summary>
    public DateTime? YouthProgramInstructionsSentUtc { get; set; }

    public List<Payment> Payments { get; } = [];

    /// <summary>
    /// True when this candidate already held an active license before this exact session started —
    /// the FCC's Grant Date predates Session.ScheduledStartUtc, so the Granted match reflects a
    /// pre-existing license (a repeat test or class upgrade), not a new grant from this session.
    /// Confirmed live 2026-07-28 against real ULS data that FCC's Grant Date does not change on a
    /// class upgrade, so this is a reliable signal independently of AM.dat's operator-class field
    /// (which the watcher does now read, as of 2026-07-30, to confirm upgrades — see
    /// docs/uls-watcher.md's "Confirming a class upgrade"). Note this stays meaningful on the
    /// upgrade path only for candidates granted before that change: UlsWatcherService now stores
    /// Last Action Date (the upgrade date) rather than the original Grant Date for a confirmed
    /// upgrade, so a newly-granted upgrade will not report true here. Requires Session loaded.
    /// </summary>
    public bool LicenseGrantPredatesSession() =>
        LicenseGrantDateUtc is { } grantedUtc && grantedUtc.Date < Session.ScheduledStartUtc.Date;
}
