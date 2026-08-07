namespace VeSessionManager.Core.Entities;

/// <summary>
/// A licence a team has asked the app to keep an eye on — club members, family, anyone at all. It is
/// deliberately **not** tied to a Candidate or a VolunteerExaminer: the whole point is that the
/// person need never have tested with this team, or be a VE. Tracking VEs' own licences is a
/// separate feature (see docs/renewal-monitor.md).
///
/// Scoped to a Team so every role can use it within their own team(s), the same scoping every list
/// page already applies.
///
/// <para><b>Everything below LastCheckedUtc is a cache of FCC's record, not this app's data.</b>
/// LicenseWatchService overwrites it wholesale from ULS on each refresh, so nothing here should ever
/// be hand-edited or treated as authoritative — the licence lives at FCC, and this row is a
/// screenshot of it. The one exception is <see cref="RenewalPendingSinceUtc"/>, which is genuinely
/// ours: it records when *we first saw* a renewal in flight, which FCC does not tell us.</para>
///
/// <para><b>The address is deliberately not stored.</b> The ULS lookup returns street/city/state/zip
/// alongside the name. None of it is needed to show whether a licence is expiring, and not holding
/// it avoids the question entirely. Call sign, FRN and licensee name are public FCC record data —
/// same privacy class as Candidate.CallSign, which the PII purge deliberately keeps.</para>
/// </summary>
public class WatchedLicense
{
    public int Id { get; set; }

    public int TeamId { get; set; }
    public Team? Team { get; set; }

    /// <summary>
    /// Always stored upper-invariant, matching VolunteerExaminer.CallSign's convention, and the key
    /// the (TeamId, CallSign) uniqueness is enforced on.
    /// <para>Non-null even when the row was added by FRN: the add flow resolves the entry against
    /// ULS before saving, so the call sign is always known by the time a row exists. That is what
    /// lets the list be keyed on something a human recognises.</para>
    /// </summary>
    public required string CallSign { get; set; }

    /// <summary>FCC Registration Number, filled in from the ULS record whether the row was added by call sign or by FRN. Nullable only because a record could in principle omit it.</summary>
    public string? Frn { get; set; }

    /// <summary>Free-text label from whoever added the row ("club secretary", "Dad") — the app's own data, never overwritten by a refresh.</summary>
    public string? Note { get; set; }

    public int AddedByUserId { get; set; }
    public User? AddedByUser { get; set; }
    public DateTime AddedUtc { get; set; }

    // ---- Cached ULS state, refreshed wholesale by LicenseWatchService ----------------------------

    /// <summary>Null until the first successful refresh. Also the "needs a refresh" query filter, in the usual scan-based idiom — a row that has never been checked sorts first.</summary>
    public DateTime? LastCheckedUtc { get; set; }

    /// <summary>True when the last lookup answered <c>type: "notfound"</c>. Kept as a flag rather than a null date so "we looked and FCC has never heard of this call sign" is distinguishable from "we have not looked yet".</summary>
    public bool NotFoundAtFcc { get; set; }

    public string? LicenseeName { get; set; }

    /// <summary>"Active", "Expired", "Cancelled", … straight from ULS.</summary>
    public string? LicenseStatus { get; set; }

    public LicenseClass OperatorClass { get; set; }

    public DateTime? GrantDateUtc { get; set; }

    /// <summary>
    /// End of the current 10-year term — the field this whole feature exists to watch.
    /// <para><b>Its advancing is the only confirmation a renewal was issued.</b> A renewal leaves the
    /// call sign, the operator class and the grant date exactly as they were, so there is no other
    /// positive signal on the record.</para>
    /// </summary>
    public DateTime? ExpiredDateUtc { get; set; }

    /// <summary>Set only when FCC has cancelled the licence outright — distinct from being past <see cref="ExpiredDateUtc"/>, which is still renewable during the grace period.</summary>
    public DateTime? CancellationDateUtc { get; set; }

    // ---- Renewal lifecycle ----------------------------------------------------------------------

    /// <summary>
    /// When this app first observed a renewal application pending at FCC. Ours, not FCC's: ULS
    /// reports that an application *is* pending, never since when we knew, and the receipt date on
    /// the application is FCC's clock rather than ours.
    /// <para>Cleared once the renewal lands (or the application disappears), which is what makes the
    /// request-through-issuance transition observable rather than just a current-state flag.</para>
    /// </summary>
    public DateTime? RenewalPendingSinceUtc { get; set; }

    /// <summary>
    /// ULS file number of the renewal application, so it can be quoted when chasing FCC. Cleared
    /// when an application is abandoned, but deliberately <b>kept</b> through a confirmation: FCC
    /// leaves a granted application in its pending list for days afterwards, and this is what
    /// LicenseWatchService matches it against so the row is not re-armed as pending by the very
    /// application it just watched land.
    /// </summary>
    public string? RenewalFileNumber { get; set; }

    /// <summary>
    /// The expiration date as it stood when a renewal was first seen pending. Kept so that "the
    /// renewal was issued" can be asserted against the value it actually replaced, rather than
    /// inferred from a date merely being in the future — a licence renewed years early would
    /// otherwise look unchanged. Cleared with the rest of the renewal fields.
    /// </summary>
    public DateTime? ExpiredDateWhenRenewalFiledUtc { get; set; }

    /// <summary>When this app last saw a renewal actually issued — i.e. <see cref="ExpiredDateUtc"/> advanced past <see cref="ExpiredDateWhenRenewalFiledUtc"/>. Retained after the renewal fields are cleared, so the page can show "renewed 3 weeks ago" rather than silently reverting to plain Active.</summary>
    public DateTime? RenewalConfirmedUtc { get; set; }
}
