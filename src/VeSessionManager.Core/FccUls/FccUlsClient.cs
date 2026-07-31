using System.IO.Compression;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VeSessionManager.Core.FccUls;

/// <summary>
/// HttpClient wrapper for FCC's public ULS daily/complete download folders
/// (https://data.fcc.gov/download/pub/uls/{daily,complete}/) — see docs/fcc-uls-watcher.md for the
/// verified file/field shapes. No auth, so — unlike Zoom/ExamTools — there's nothing to defer to
/// first use; the only real failure mode is the requested zip not existing yet (a 404, treated as
/// "not published yet," not an error) versus everything else (thrown, surfaced via
/// JobRunHistoryLogger like any other job failure).
///
/// Note: www.fcc.gov (the documentation/PDF pages) appears to block or heavily throttle
/// non-browser HTTP clients — plain requests there hang/reset. data.fcc.gov (the actual download
/// host used here) has no such issue; confirmed via direct testing before writing this client.
/// </summary>
public sealed class FccUlsClient : IFccUlsClient, IDisposable
{
    private static readonly IReadOnlyDictionary<DayOfWeek, string> DayAbbreviations = new Dictionary<DayOfWeek, string>
    {
        [DayOfWeek.Sunday] = "sun",
        [DayOfWeek.Monday] = "mon",
        [DayOfWeek.Tuesday] = "tue",
        [DayOfWeek.Wednesday] = "wed",
        [DayOfWeek.Thursday] = "thu",
        [DayOfWeek.Friday] = "fri",
        [DayOfWeek.Saturday] = "sat",
    };

    private readonly FccUlsOptions _options;
    private readonly ILogger<FccUlsClient> _logger;
    private readonly HttpClient _httpClient;

    public FccUlsClient(IOptions<FccUlsOptions> options, ILogger<FccUlsClient> logger)
    {
        _options = options.Value;
        _logger = logger;
        _httpClient = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15)
        });
    }

    public Task<IReadOnlyList<FccUlsApplicationRecord>?> DownloadDailyApplicationsAsync(DayOfWeek day, CancellationToken cancellationToken) =>
        DownloadApplicationsAsync($"{_options.BaseUrl}daily/a_am_{DayAbbreviations[day]}.zip", cancellationToken);

    public Task<IReadOnlyList<FccUlsLicenseRecord>?> DownloadDailyLicensesAsync(DayOfWeek day, CancellationToken cancellationToken) =>
        DownloadLicensesAsync($"{_options.BaseUrl}daily/l_am_{DayAbbreviations[day]}.zip", cancellationToken);

    public Task<IReadOnlyList<FccUlsApplicationRecord>?> DownloadWeeklyApplicationsAsync(CancellationToken cancellationToken) =>
        DownloadApplicationsAsync($"{_options.BaseUrl}complete/a_amat.zip", cancellationToken);

    public Task<IReadOnlyList<FccUlsLicenseRecord>?> DownloadWeeklyLicensesAsync(CancellationToken cancellationToken) =>
        DownloadLicensesAsync($"{_options.BaseUrl}complete/l_amat.zip", cancellationToken);

    private async Task<IReadOnlyList<FccUlsApplicationRecord>?> DownloadApplicationsAsync(string url, CancellationToken cancellationToken)
    {
        var files = await DownloadDatFilesAsync(url, cancellationToken);
        return files is null ? null : FccUlsRecordParser.ParseApplications(files.Value.HdContent, files.Value.EnContent, files.Value.HsContent);
    }

    private async Task<IReadOnlyList<FccUlsLicenseRecord>?> DownloadLicensesAsync(string url, CancellationToken cancellationToken)
    {
        var files = await DownloadDatFilesAsync(url, cancellationToken);
        return files is null ? null : FccUlsRecordParser.ParseLicenses(files.Value.HdContent, files.Value.EnContent, files.Value.AmContent);
    }

    private async Task<(string HdContent, string EnContent, string? HsContent, string? AmContent)?> DownloadDatFilesAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogInformation("FCC ULS file not available yet at {Url} — treating as a normal gap (maintenance window or not-yet-published); will be retried on the next scheduled run", url);
            return null;
        }

        response.EnsureSuccessStatusCode();

        await using var zipStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
        // HS.dat (History — Red Light/Basic Qualification hold codes) and AM.dat (Amateur — operator
        // class) are both read leniently: each feeds one enrichment rather than the core FRN join, so
        // a variant zip missing either shouldn't fail the whole download. Without AM.dat an upgrade
        // simply stays unconfirmed (exactly the pre-2026-07-30 behavior), which is the safe direction
        // — see FccUlsWatcherService.ProcessLicensesAsync. Both were confirmed present in the daily
        // (l_am_thu.zip) and weekly-complete (l_amat.zip) archives on 2026-07-30.
        return (
            ReadEntryText(archive, "HD.dat"),
            ReadEntryText(archive, "EN.dat"),
            TryReadEntryText(archive, "HS.dat"),
            TryReadEntryText(archive, "AM.dat"));
    }

    private static string ReadEntryText(ZipArchive archive, string entryName) =>
        TryReadEntryText(archive, entryName)
            ?? throw new InvalidOperationException($"FCC ULS zip did not contain the expected {entryName} entry.");

    private static string? TryReadEntryText(ZipArchive archive, string entryName)
    {
        var entry = archive.GetEntry(entryName);
        if (entry is null)
        {
            return null;
        }

        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public void Dispose() => _httpClient.Dispose();
}
