namespace VeSessionManager.Core.Entities;

public class Candidate
{
    public int Id { get; set; }

    public int SessionId { get; set; }
    public Session Session { get; set; } = null!;

    // Nullable because the PII purge job (Phase 10) and the immediate no-show/withdrawal
    // delete action (Phase 9) null these fields out while keeping the row for stats.
    public string? Name { get; set; }
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

    public List<Payment> Payments { get; } = [];
}
