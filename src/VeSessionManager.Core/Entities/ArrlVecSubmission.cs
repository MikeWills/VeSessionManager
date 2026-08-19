using VeSessionManager.Core.VecSubmissions;

namespace VeSessionManager.Core.Entities;

/// <summary>
/// One filing with ARRL-VEC (issue #197) — what was sent, and what came back.
///
/// <para><b>The record is the point, not a side effect.</b> Mike has had to go back to one of these
/// after the fact, which is why the stored values are the ones actually submitted rather than
/// re-derived at display time: every field on the preview is editable, so what was filed and what the
/// configuration would produce today are two different questions. Re-deriving would answer the wrong
/// one.</para>
///
/// <para><b>A row exists even when the outcome is Unknown.</b> That is the case the record matters
/// most for: the submission may well have landed, and this is the only evidence of what went.</para>
/// </summary>
public class ArrlVecSubmission
{
    public int Id { get; set; }

    /// <summary>
    /// Nullable so a deleted session cannot take the record of its own filing with it — see
    /// <c>ArrlVecSubmissionConfiguration</c>. Null means the session is gone, not that none was filed;
    /// the snapshot below still says what went.
    /// </summary>
    public int? SessionId { get; set; }
    public Session? Session { get; set; }

    /// <summary>Denormalized from the session so an archive stays attributable after a session is deleted, and so the purge can scope without a join. Nullable for the same reason as <see cref="SessionId"/>.</summary>
    public int? TeamId { get; set; }
    public Team? Team { get; set; }

    public int? SubmittedByUserId { get; set; }
    public User? SubmittedByUser { get; set; }

    public DateTime SubmittedUtc { get; set; }

    // ---- Exactly what was posted -------------------------------------------------------------

    public required string FullName { get; set; }
    public required string CallSign { get; set; }
    public required string Email { get; set; }
    public required string Phone { get; set; }

    /// <summary>ISO <c>yyyy-MM-dd</c>, as posted.</summary>
    public required string SessionDate { get; set; }

    public required string Location { get; set; }
    public ArrlPaymentMethod PaymentMethod { get; set; }

    /// <summary>Stored as the string that was sent, not a decimal — a re-formatted amount is not what was filed.</summary>
    public required string AmountCharged { get; set; }

    public string? Note { get; set; }

    // ---- The files ---------------------------------------------------------------------------
    // Two names per file, deliberately: what ARRL was told it was called, and where it sits on disk.
    // They will normally be identical, which is exactly why the distinction has to be explicit — the
    // wire name comes from ExamTools or an operator's upload, and a third-party string must never be
    // the thing that shapes a path.

    public required string ArchiveFileName { get; set; }
    public string? ArchiveStoredPath { get; set; }
    public int ArchiveByteCount { get; set; }

    /// <summary>The youth grant program form, when one was attached. Null for the ordinary single-file submission.</summary>
    public string? AttachmentFileName { get; set; }
    public string? AttachmentStoredPath { get; set; }
    public int AttachmentByteCount { get; set; }

    // ---- What came back ----------------------------------------------------------------------

    /// <summary>
    /// ARRL's raw response, stored verbatim — this <b>is</b> the receipt the team keeps.
    ///
    /// <para><b>Never render it back into a page.</b> Offer it as a download or as text: a document
    /// fetched from a third party and echoed into an authenticated admin view is a stored-XSS vector
    /// regardless of how benign its content is, and this codebase has zero <c>Html.Raw</c> and should
    /// keep it.</para>
    ///
    /// <para>It carries PII, contrary to what #197 originally recorded: the submitter's name, call
    /// sign, email and phone, an IP address ARRL adds itself, and whatever is in the note — which in
    /// one real submission was a card's last four digits tied to a named person.</para>
    /// </summary>
    public string? ResponseBody { get; set; }

    /// <summary>Recorded but never used to decide success — both outcomes arrive on the same endpoint.</summary>
    public int? ResponseStatusCode { get; set; }

    public ArrlReceiptOutcome Outcome { get; set; }

    /// <summary>Filenames that did not come back confirmed, comma-separated. The whole investigation when an outcome is Unknown.</summary>
    public string? UnconfirmedFileNames { get; set; }

    /// <summary>Set if the request never completed — a transport failure, which is <b>not</b> proof nothing was filed.</summary>
    public string? TransportError { get; set; }

    /// <summary>When the retention purge cleared the stored files, or null. The row itself outlives them: it is the record that a filing happened.</summary>
    public DateTime? FilesPurgedUtc { get; set; }
}
