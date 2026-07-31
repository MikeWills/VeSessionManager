using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.FccUls;

/// <summary>A candidate's FRN appearing in a daily/weekly amateur application file, joined from HD+EN by USI. HoldReason/PaymentStatus come from a third join against the same zip's HS.dat (History) records — see FccUlsRecordParser.</summary>
public sealed record FccUlsApplicationRecord(
    string UniqueSystemIdentifier,
    string Frn,
    DateTime LastActionDateUtc,
    FccApplicationHoldReason HoldReason = FccApplicationHoldReason.None,
    FccApplicationPaymentStatus PaymentStatus = FccApplicationPaymentStatus.Unknown);

/// <summary>
/// A candidate's FRN appearing in a daily/weekly amateur license file, joined from HD+EN by USI
/// (plus AM for the operator class). LicenseStatus is the raw HD status code ("A" = Active,
/// "C" = Canceled, etc.) — callers decide which statuses count as a real grant, see
/// FccUlsWatcherService.
///
/// <para><b>GrantDateUtc vs LastActionDateUtc matters for upgrades.</b> FCC does not move Grant Date
/// when an existing licensee upgrades class — it stays pinned to the original license (verified
/// 2026-07-30 against real data: a General→Extra upgrade taken 2026-07-19 still reported a Grant Date
/// of 2021-04-30). Last Action Date <i>does</i> advance to the upgrade. So a new license is confirmed
/// by GrantDateUtc and an upgrade by OperatorClass + LastActionDateUtc together.</para>
/// </summary>
public sealed record FccUlsLicenseRecord(
    string UniqueSystemIdentifier,
    string Frn,
    string CallSign,
    string LicenseStatus,
    DateTime GrantDateUtc,
    DateTime LastActionDateUtc,
    LicenseClass OperatorClass = LicenseClass.None);
