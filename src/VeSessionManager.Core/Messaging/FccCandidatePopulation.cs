namespace VeSessionManager.Core.Messaging;

/// <summary>
/// Which candidate population a message subject belongs to, for the FCC-wide-issue suppression gate
/// in <see cref="MessageDispatchService"/> (2026-08-26). Only <see cref="Scanners.FccFeeOutstandingScanner"/>
/// sets this today — every other scanner leaves <see cref="MessageSubject.FccPopulation"/> null, which
/// the gate treats as "not subject to this switch at all."
///
/// <para>No <c>Renewal</c> member: this app has no renewal-candidate concept, so nothing could ever
/// construct a subject tagged that way. See <see cref="Entities.SystemSettings.FccIssueSuppressRenewalReminders"/>
/// for where that switch lives instead — stored, shown, never read.</para>
/// </summary>
public enum FccCandidatePopulation
{
    /// <summary>No prior license (Candidate.InitialLicenseClass null or None).</summary>
    NewLicense,

    /// <summary>Upgrading an existing license.</summary>
    Upgrade
}
