using VeSessionManager.Core.Entities;

namespace VeSessionManager.Web;

/// <summary>
/// Formats a candidate's InitialLicenseClass/NewLicenseClass pair as e.g. "Technician → General" —
/// shared by CandidateDetail and ApplicantStatus, both of which show the same license-class
/// transition for a candidate. Extracted 2026-07-29 after a duplicate-code review found the two
/// pages had each independently reimplemented this (and already drifted: one returned "—" for the
/// unset case, the other returned null). Both are always null or both set — see
/// ExamResultSyncService.ResolveLicenseClasses.
/// </summary>
public static class LicenseClassFormatter
{
    public static string? FormatTransition(LicenseClass? initial, LicenseClass? newClass) =>
        initial is { } i && newClass is { } n ? $"{FormatClass(i)} → {FormatClass(n)}" : null;

    private static string FormatClass(LicenseClass licenseClass) =>
        licenseClass == LicenseClass.None ? "Unlicensed" : licenseClass.ToString();
}
