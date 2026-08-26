using VeSessionManager.Core.Entities;
using VeSessionManager.Core.ExamTools;

namespace VeSessionManager.Core.VecSubmissions;

/// <summary>
/// Everything the ARRL submission screen shows before anything is sent (issue #197) — every form
/// field with the value that would be posted, the archive that would go with it, and the review aids
/// that let a human judge whether it is right.
///
/// <para><b>This is the safeguard, not a convenience.</b> There is no sandbox on ARRL's side and no
/// dry-run mode: every exercise of the real path files a real session with a real VEC. The issue asks
/// for a preview *and* an explicit confirmation as two separate guards; making the preview the only
/// route to the POST collapses them into one that nobody can forget to use.</para>
/// </summary>
public sealed record ArrlSubmissionPreview
{
    public required ArrlSubmissionPreviewStatus Status { get; init; }

    public required int SessionId { get; init; }
    public string? SessionTitle { get; init; }
    public string? TeamName { get; init; }

    // ---- The form itself, field for field ----------------------------------------------------
    // Every one of these is editable on the screen. The team configuration and the derived values
    // are *prefill*: what is on screen is what gets sent, which is also why the archive stores the
    // submitted values rather than re-deriving them for display afterwards.

    public string? FullName { get; init; }
    public string? CallSign { get; init; }
    public string? Email { get; init; }
    public string? Phone { get; init; }

    /// <summary>ISO <c>yyyy-MM-dd</c>, because ARRL's field is an <c>&lt;input type="date"&gt;</c>.</summary>
    public string? SessionDate { get; init; }

    public string? Location { get; init; }
    public ArrlPaymentMethod? PaymentMethod { get; init; }

    /// <summary>Plain decimal, no <c>$</c> — <c>16.00</c>, exactly as the real receipts show it.</summary>
    public string AmountCharged { get; init; } = "0.00";

    public string? Note { get; init; }

    // ---- The archive -------------------------------------------------------------------------

    public VecArchiveDownloadOutcome? ArchiveOutcome { get; init; }
    public string? ArchiveFileName { get; init; }
    public int ArchiveByteCount { get; init; }

    /// <summary>ExamTools' own wording when the archive could not be fetched — shown rather than paraphrased.</summary>
    public string? ArchiveMessage { get; init; }

    // ---- Review aids -------------------------------------------------------------------------

    /// <summary>
    /// Required ARRL fields that resolved to nothing, named individually. Most often the lead's phone
    /// or email: ExamTools supplies no contact details at all, so those are only ever filled in by an
    /// admin or the VE, and the retention purge clears both.
    /// </summary>
    public IReadOnlyList<string> MissingRequiredFields { get; init; } = [];

    /// <summary>The arithmetic behind <see cref="AmountCharged"/>, shown rather than just its total.</summary>
    public SessionFeeSummary? Fees { get; init; }

    /// <summary>
    /// Payments feeding the amount that are not ordinary — refunded, or flagged because Square
    /// reported a different figure than was owed. A confident-looking total with no sign that two of
    /// its inputs are unusual is worse than no derivation at all.
    /// </summary>
    public IReadOnlyList<string> AmountWarnings { get; init; } = [];

    /// <summary>True when the session has a youth-rate payment, which is when ARRL also expects the youth grant program form.</summary>
    public bool YouthFormExpected { get; init; }

    /// <summary>Already filed. The submission path must refuse; ARRL cannot dedupe and has no unsend.</summary>
    public bool AlreadySubmitted { get; init; }

    public bool CanSubmit => Status == ArrlSubmissionPreviewStatus.Ready
                             && MissingRequiredFields.Count == 0
                             && ArchiveOutcome == VecArchiveDownloadOutcome.Succeeded
                             && !AlreadySubmitted;
}

public enum ArrlSubmissionPreviewStatus
{
    Ready = 0,
    SessionNotFound = 1,

    /// <summary>
    /// The session's VEC is not ARRL. <b>Not a fallback case</b> — per #197 there is exactly one
    /// submitter and no default, so a GLAARG or SANDARC session must find nothing and be told so
    /// rather than be handed the ARRL one.
    /// </summary>
    NotAnArrlSession = 2,

    /// <summary>The team has not filled in its ARRL submission settings, so no complete form can be built.</summary>
    TeamNotConfigured = 3,

    /// <summary>
    /// This team's effective ExamTools host is the test site (<c>examtools.dev</c>), not a production
    /// one — a team practicing against test data must not be able to file a real session with ARRL.
    /// </summary>
    TeamOnTestExamTools = 4
}
