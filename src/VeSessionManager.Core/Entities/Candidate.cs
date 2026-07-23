namespace VeSessionManager.Core.Entities;

public class Candidate
{
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

    /// <summary>Flips to true when the Session Manager marks the whole session as completed. Intentionally separate from ApplicationStatus.</summary>
    public bool Tested { get; set; }

    /// <summary>From ULS HD status date — only applies to the Received/Granted path.</summary>
    public DateTime? ApplicationDateEnteredUtc { get; set; }

    public string? CallSign { get; set; }
    public DateTime? LicenseGrantDateUtc { get; set; }

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
}
