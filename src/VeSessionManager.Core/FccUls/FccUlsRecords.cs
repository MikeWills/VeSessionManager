namespace VeSessionManager.Core.FccUls;

/// <summary>A candidate's FRN appearing in a daily/weekly amateur application file, joined from HD+EN by USI.</summary>
public sealed record FccUlsApplicationRecord(string UniqueSystemIdentifier, string Frn, DateTime LastActionDateUtc);

/// <summary>A candidate's FRN appearing in a daily/weekly amateur license file, joined from HD+EN by USI. LicenseStatus is the raw HD status code ("A" = Active, "C" = Canceled, etc.) — callers decide which statuses count as a real grant, see FccUlsWatcherService.</summary>
public sealed record FccUlsLicenseRecord(string UniqueSystemIdentifier, string Frn, string CallSign, string LicenseStatus, DateTime GrantDateUtc);
