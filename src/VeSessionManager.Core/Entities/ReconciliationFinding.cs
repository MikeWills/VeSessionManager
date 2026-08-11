namespace VeSessionManager.Core.Entities;

/// <summary>
/// One disagreement between ExamTools and this app's own data, found by the nightly reconciliation
/// sweep (built 2026-08-10).
///
/// <para><b>Why this table exists rather than just a Job History line.</b> The sweep could report
/// "3 sessions missing" in its run summary and stop there — but Job History rotates, a green row
/// saying "3 missing" reads as success, and a count in a sentence cannot be acted on. This app has
/// already paid for that lesson once: the Worker printed <c>sent 0, failed 1</c> all day while the
/// dashboard showed green, and an evening went into chasing "no emails are being sent".</para>
///
/// <para><b>A finding is a standing fact, not an event.</b> The same missing session found on ten
/// consecutive nights is one row whose <see cref="LastSeenUtc"/> moves, not ten rows — otherwise the
/// list grows without bound and its size stops meaning anything. When the discrepancy goes away
/// (usually because a re-import filled the gap) the row is stamped <see cref="ResolvedUtc"/> rather
/// than deleted, so "this was wrong and is now fixed" stays visible.</para>
/// </summary>
public class ReconciliationFinding
{
    public int Id { get; set; }

    public int TeamId { get; set; }
    public Team Team { get; set; } = null!;

    public ReconciliationFindingKind Kind { get; set; }

    /// <summary>
    /// The ExamTools session id the finding is about. Deliberately the <b>remote</b> id rather than a
    /// local <c>SessionId</c>: the whole point of the commonest finding is that no local row exists.
    /// </summary>
    public required string ExamToolsSessionId { get; set; }

    /// <summary>When the session was held, so the page can show it and derive an import range without a second lookup.</summary>
    public DateTime SessionDateUtc { get; set; }

    /// <summary>Human-readable specifics — e.g. "ExamTools reports 12 applicants, this app has 9".</summary>
    public required string Detail { get; set; }

    public DateTime FirstSeenUtc { get; set; }

    /// <summary>Refreshed every sweep that still sees it. A stale LastSeenUtc on an unresolved row means the sweep itself stopped running.</summary>
    public DateTime LastSeenUtc { get; set; }

    /// <summary>Set once a later sweep no longer sees the discrepancy. Kept rather than deleted so the history of what went wrong survives.</summary>
    public DateTime? ResolvedUtc { get; set; }
}

/// <summary>
/// <b>Persisted as an integer, so these values are pinned and must keep their numbers</b> — the rule
/// stated in <c>Enums.cs</c>, which this enum was missed by because it lives in its own file (issue
/// #285, pinned 2026-08-11).
///
/// <para>It is also a component of <c>IX_ReconciliationFindings_TeamId_Kind_ExamToolsSessionId</c>,
/// which is <b>unique</b>. Inserting a member alphabetically would not merely relabel existing rows;
/// it would re-point that index, so a stored <c>MissingSession</c> could start colliding with a
/// <c>CandidateCountMismatch</c> for the same session. Append new members, never insert.</para>
/// </summary>
public enum ReconciliationFindingKind
{
    /// <summary>ExamTools has a closed session this app never ingested. The month-end date-bound bug produced these by the dozen.</summary>
    MissingSession = 0,

    /// <summary>The session exists here, but ExamTools reports more applicants than this app holds — a partial ingestion.</summary>
    CandidateCountMismatch = 1
}
