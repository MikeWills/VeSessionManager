namespace VeSessionManager.Core.Entities;

/// <summary>
/// One stored entry of a candidate's FCC application timeline (#195) — an action code, when FCC
/// logged it, and FCC's own words for it.
///
/// <para><b>Why this is stored rather than fetched on demand.</b> <c>UlsWatcherService</c> already
/// receives these on every run, so persisting them costs no additional polling of someone else's
/// servers — which was the condition the issue set. Fetching at render time would put an
/// unauthenticated third-party call on a page load, and this endpoint is undocumented enough that
/// the app polls it on a schedule rather than in a request.</para>
///
/// <para><b>Not PII, deliberately.</b> Same class as <c>FccUlsLicenseKey</c>, <c>Frn</c> and
/// <c>FccHoldReason</c>, none of which the purge clears (Mike's 2026-08-03 ruling): these are public
/// FCC records about an application, and keeping them is what lets a question about that application
/// still be answered after the candidate's name and email are gone. Note <c>FccHoldReason</c> already
/// records the same Red Light / Basic Qualification facts these entries describe, so clearing the
/// timeline while retaining the flag would protect nothing while losing the explanation.</para>
/// </summary>
public class CandidateUlsHistoryEntry
{
    public int Id { get; set; }

    public int CandidateId { get; set; }
    public Candidate Candidate { get; set; } = null!;

    /// <summary>When FCC logged the action. Date-only at source, stamped at UTC midnight — never a local instant.</summary>
    public DateTime? LogDateUtc { get; set; }

    /// <summary>The ULS action code (RDLOFF/RDLCOM, BQOFF/BQCOM, FVPOFF/FVPCNF/FVPCOM …), upper-cased.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>FCC's human-readable description, exactly as returned. Null when the endpoint omits it — see <c>UlsHistoryEntry.Description</c> for why that must degrade to the code rather than to a blank row.</summary>
    public string? CodeText { get; set; }
}
