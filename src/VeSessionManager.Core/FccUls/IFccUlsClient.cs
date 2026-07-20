namespace VeSessionManager.Core.FccUls;

/// <summary>
/// Downloads and parses FCC ULS amateur application/license transaction files. Unlike Zoom/
/// Discord/Square/Email, this is not a credential-gated "optional integration" — data.fcc.gov is
/// public, no auth needed — so there is no IsConfigured here; it always runs. The thing that does
/// vary at runtime is file *availability*: a null return means the requested file doesn't exist
/// yet (a genuinely normal gap — weekend maintenance windows, or a daily file not published until
/// ~5am ET — not an error), which callers should treat as "nothing to process this run," same
/// spirit as an optional integration's quiet skip but for a different reason.
/// </summary>
public interface IFccUlsClient
{
    Task<IReadOnlyList<FccUlsApplicationRecord>?> DownloadDailyApplicationsAsync(DayOfWeek day, CancellationToken cancellationToken);

    Task<IReadOnlyList<FccUlsLicenseRecord>?> DownloadDailyLicensesAsync(DayOfWeek day, CancellationToken cancellationToken);

    Task<IReadOnlyList<FccUlsApplicationRecord>?> DownloadWeeklyApplicationsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<FccUlsLicenseRecord>?> DownloadWeeklyLicensesAsync(CancellationToken cancellationToken);
}
