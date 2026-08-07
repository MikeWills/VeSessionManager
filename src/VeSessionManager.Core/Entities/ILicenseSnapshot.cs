namespace VeSessionManager.Core.Entities;

/// <summary>
/// A cached copy of one FCC licence record, as held by whichever entity is holding it.
///
/// <para>Two things now cache the same ULS fields for different reasons:
/// <see cref="WatchedLicense"/> (the Renewal Monitor's hand-curated watch list) and
/// <see cref="VolunteerExaminer"/> (issue #107, "can this person legally serve on Saturday?").
/// Without this, the 90-day renewal window, the two-year grace period and the rules for reading an
/// expiry would have been written twice and drifted — which is precisely the failure mode the
/// shared-helper convention in CLAUDE.md exists to prevent.</para>
///
/// <para><b>Deliberately covers only the record, not the lifecycle.</b> The renewal
/// request-through-issuance state machine (<c>RenewalPendingSinceUtc</c> and friends) stays on
/// <see cref="WatchedLicense"/> alone: it is the Renewal Monitor's whole purpose and means nothing
/// for a VE roster, where the question is a session date rather than a renewal.</para>
/// </summary>
public interface ILicenseSnapshot
{
    /// <summary>Null until the first successful lookup — "we have not looked" rather than "there is nothing to find".</summary>
    DateTime? LicenseLastCheckedUtc { get; }

    /// <summary>True when the last lookup answered <c>type: "notfound"</c>.</summary>
    bool LicenseNotFoundAtFcc { get; }

    /// <summary>Set only when FCC cancelled the licence outright — distinct from being past its expiry, which is still renewable.</summary>
    DateTime? LicenseCancellationDateUtc { get; }

    /// <summary>End of the current ten-year term.</summary>
    DateTime? LicenseExpiresUtc { get; }

    /// <summary>
    /// The call sign the lookup would use. Needed by the status rules rather than just the fetcher,
    /// because "there is no call sign to check" is a distinct answer from "we have not checked yet"
    /// and must not read as healthy — see <see cref="WatchedLicenseStatus.NoCallSign"/>.
    /// </summary>
    string? CallSign { get; }
}
