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

    /// <summary>
    /// Issue #463 — "who's local," shown as a single "City, ST" column on the session candidate list.
    /// Sourced from ExamTools' <c>city</c>/<c>state</c> registration fields (confirmed present on
    /// <c>export/basic.json</c>'s applicant rows, alongside <c>addr</c>/<c>zip</c>, neither of which
    /// is mapped — nothing needs a street address, and the issue asked for city/state only). PII,
    /// cleared by <see cref="CandidatePiiFields.Clear"/> alongside Name/Email.
    /// </summary>
    public string? City { get; set; }

    /// <inheritdoc cref="City"/>
    public string? State { get; set; }

    /// <summary>Normally required before testing, but VECs have allowed testing without one during exceptional circumstances (e.g. federal shutdowns).</summary>
    public string? Frn { get; set; }

    /// <summary>Flags the no-FRN-at-registration case for a later batch export/VEC follow-up.</summary>
    public bool FrnMissingAtRegistration { get; set; }

    /// <summary>Captured from the exam application data if the ExamTools/HamStudy API exposes it. Treated as sensitive PII, purged alongside Name/Email/Frn.</summary>
    public bool? HasFelonyDisclosure { get; set; }

    public DateTime DateRegisteredUtc { get; set; }

    public CandidateApplicationStatus ApplicationStatus { get; set; } = CandidateApplicationStatus.Unmatched;

    /// <summary>Flips to true when the Session Manager marks the whole session as completed, or automatically once ExamResultSyncService sees a graded exam result for this candidate — whichever happens first. Intentionally separate from ApplicationStatus.</summary>
    /// <remarks>Set through <see cref="MarkTested"/>, never assigned directly — see there.</remarks>
    public bool Tested { get; set; }

    /// <summary>
    /// When <see cref="Tested"/> first became true, or null for a candidate who has not tested — and
    /// for every candidate who tested before this field existed (#401 PR3).
    ///
    /// <para><b>Added because a bool cannot be a trigger point.</b> <c>MessageTrigger.CandidateTested</c>
    /// needs the moment the state changed, both to bound itself by the rule's own creation and to
    /// avoid reaching a year of backfilled history. The nearest existing candidates were
    /// <c>ResultMarkedUtc</c> — which only a Session Manager's explicit result sets, not the automatic
    /// path — and the session's own start, which is hours to days before grading actually happens.
    /// Neither answers "when did this become true".</para>
    ///
    /// <para><b>Deliberately not backfilled.</b> Everyone already tested keeps a null here, so a rule
    /// created later never fires for them. That is the same direction of safety as
    /// <c>MessageRule.CreatedUtc</c> itself, arrived at for free rather than by a second guard.</para>
    /// </summary>
    public DateTime? TestedUtc { get; set; }

    /// <summary>
    /// Marks this candidate as having tested, recording when. <b>The one place <see cref="Tested"/> is
    /// set</b>, called by all four paths that flip it — marking a session completed, marking one
    /// candidate failed, and both branches of the automatic exam-result sync.
    ///
    /// <para>A helper rather than two assignments at four call sites because the pair has to stay
    /// together: a site that sets the bool and forgets the timestamp leaves a candidate the
    /// <c>CandidateTested</c> trigger can never see, and nothing fails — they simply never get the
    /// email. <c>NoRawTestedAssignmentTests</c> fails the build if a raw assignment reappears.</para>
    ///
    /// <para><b>Idempotent on purpose.</b> The timestamp records the <i>first</i> time, so a second
    /// call — the result sync re-seeing an already-tested candidate on the next poll — leaves it
    /// alone. Refreshing it would let a rule fire for somebody whose real moment was months ago.</para>
    /// </summary>
    public void MarkTested(DateTime nowUtc)
    {
        TestedUtc ??= nowUtc;
        Tested = true;
    }

    /// <summary>
    /// Whether <see cref="Tested"/> has something behind it: a graded result, a terminal verdict, or a
    /// human marking <b>this specific candidate</b> (#419).
    ///
    /// <para>"Mark session completed" flips everyone still on the roster to Tested — including a
    /// no-show whose ExamTools removal has not ingested yet, since the app cannot know it is coming.
    /// That Tested is an <i>assertion about the roster</i>, not evidence about a person, and treating
    /// the two the same is what stranded a no-show as Tested + Unmatched forever: immune to
    /// withdrawal, immune to Delete, permanently on the Pending FCC grant list beside their real row
    /// on the session they actually sat.</para>
    ///
    /// <para>Not EF-mapped — evaluate on a materialized candidate. The withdrawal scan and DeleteAsync
    /// both do.</para>
    /// </summary>
    public bool TestedWithEvidence => Tested
        && (NewLicenseClass is not null            // a graded pass recorded the class it earned
            || ResultMarkedByUserId is not null    // a human marked this candidate by hand
            || ApplicationStatus.IsTerminal());    // Failed/Granted/NotTested — already adjudicated

    /// <summary>
    /// Undoes a completion-only <see cref="MarkTested"/> when the candidate is being withdrawn (#419).
    /// A row left NotTested while still reading Tested = true would haunt every Tested-keyed list and
    /// trigger. Lives here because this file is the one place allowed to assign <see cref="Tested"/> —
    /// see <c>NoRawTestedAssignmentTests</c>. Callers must have checked <see cref="TestedWithEvidence"/>
    /// first; unmaking a graded result is never correct.
    /// </summary>
    public void UndoCompletionTested()
    {
        Tested = false;
        TestedUtc = null;
    }

    /// <summary>From ULS HD status date — only applies to the Received/Granted path.</summary>
    public DateTime? ApplicationDateEnteredUtc { get; set; }

    public string? CallSign { get; set; }
    public DateTime? LicenseGrantDateUtc { get; set; }

    /// <summary>FCC ULS "Unique System Identifier" from the matched license record — the same value ULS's own web UI calls `licKey` (e.g. `https://wireless2.fcc.gov/UlsApp/UlsSearch/license.jsp?licKey=...`). Set alongside CallSign/LicenseGrantDateUtc by UlsWatcherService; not PII (a public FCC record locator, same privacy class as CallSign), so not cleared by CandidatePiiFields.Clear.</summary>
    public string? FccUlsLicenseKey { get; set; }

    /// <summary>ULS application file number for the candidate's pending application (e.g. `0012131564`), from the ULS lookup's pendingApplications block. Surfaced on Applicant Status so a Session Manager can look the application up in FCC's own Application Search — the pre-grant counterpart to FccUlsLicenseKey. Not PII, same reasoning as that field.</summary>
    public string? UlsApplicationFileNumber { get; set; }

    /// <summary>
    /// When <c>UlsWatcherService</c> last attempted a lookup for this candidate — <b>set on every
    /// attempt, including one that failed</b>, which is the point of it (issue #247, 2026-08-11).
    ///
    /// <para>It exists to make the scan bounded. The watcher had no per-run cap while both sibling
    /// sweeps did, so it made one sequential HTTP call per non-terminal candidate against a third
    /// party's undocumented mirror, twice a day, with no ceiling. Ordering by this column and taking
    /// a fixed number turns that into a fair round-robin: least-recently-attempted first, remainder
    /// next run.</para>
    ///
    /// <para><b>Stamped on failure too, deliberately.</b> Null sorts first, so a row that is never
    /// stamped stays permanently at the head of the queue — which is exactly how
    /// <c>VolunteerExaminerLicenseWatchService</c>'s skipped placeholders could starve its whole
    /// sweep. A failed lookup going to the back of the queue costs one cycle of delay; not stamping
    /// it costs everyone else their turn.</para>
    ///
    /// <para>Not PII, and not cleared by <c>CandidatePiiFields.Clear</c>: it records when this app
    /// talked to an API, not anything about the person.</para>
    /// </summary>
    public DateTime? UlsLastCheckedUtc { get; set; }

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

    /// <summary>
    /// Set when the candidate has been reminded that <b>FCC's</b> application fee is still
    /// outstanding — the one they pay directly at CORES, not the team's exam fee (#219).
    ///
    /// <para>Lives on Candidate rather than Payment because that is what it is about. The fee is
    /// FCC's, owed by the applicant to the FCC, and this app never sees the money; there may be no
    /// Payment row at all when a team does not collect fees. The old reminder hung off
    /// <c>Payment.PaymentReminderSentUtc</c> and chased the exam fee, which is collected before or at
    /// the session and so was always already paid by the time the trigger could fire.</para>
    ///
    /// <para>Both the "needs reminding" query filter and the idempotency guard, like every other
    /// <c>...SentUtc</c> here.</para>
    /// </summary>
    public DateTime? FccFeeReminderSentUtc { get; set; }

    /// <summary>Not in the original shared data model — added post-launch so the session detail page's "Email history" modal can show this send. Unlike RegistrationConfirmationSentUtc/DayBeforeReminderSentUtc this isn't an idempotency guard (the send itself is already one-shot, gated by SessionActionService.MarkCompletedAsync's own "candidates just tested" set) — purely a display timestamp.</summary>
    public DateTime? FelonyDisclosureInstructionsSentUtc { get; set; }

    /// <summary>See FelonyDisclosureInstructionsSentUtc — same "display timestamp, not a send guard" reasoning. Unlike that one, this action has no cap and can be clicked more than once; this always holds the *most recent* send, not the first.</summary>
    public DateTime? YouthProgramInstructionsSentUtc { get; set; }

    public List<Payment> Payments { get; } = [];

    /// <summary>
    /// The candidate's FCC application timeline, oldest first (#195) — refreshed by
    /// <c>UlsWatcherService</c> from data it already fetches, and reconciled rather than rewritten so
    /// an unchanged timeline costs no write. See <see cref="CandidateUlsHistoryEntry"/> for why these
    /// are not treated as PII.
    /// </summary>
    public List<CandidateUlsHistoryEntry> UlsHistory { get; } = [];

    /// <summary>
    /// Hand-composed emails this candidate has actually received (#144) — the ones somebody wrote on
    /// the Email candidates screen. A collection rather than another <c>...SentUtc</c> column because
    /// a team writes its own templates, so the set of things that can be sent is not fixed by the
    /// code. See <see cref="CandidateEmailSend"/>.
    /// </summary>
    public List<CandidateEmailSend> EmailSends { get; } = [];

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
