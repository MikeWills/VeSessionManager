using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Uls;

/// <summary>
/// HttpClient wrapper for ExamTools' ULS mirror (<c>GET /api/uls/lookup2/{frnOrCallsign}</c>).
///
/// <para>**Unauthenticated and global, not per-Team** — unlike every other ExamTools call in this
/// codebase, this endpoint needs no login cookie and returns public FCC data, so it takes no
/// ExamToolsCredentials and is registered as a singleton with one HttpClient.</para>
///
/// <para>**Use lookup2, not lookup.** Both exist; `/lookup/` resolves an FRN against a *staler*
/// index — on 2026-07-31 it reported a candidate `license_status: "Pending"` with no call sign at
/// all while `/lookup2/` returned that same FRN's grant issued the same morning. Since this app only
/// ever holds FRNs, `/lookup/` is unusable here. See docs/uls-watcher.md.</para>
///
/// <para>Credentials are not involved, so there is nothing to validate in the constructor — but the
/// no-throwing-constructor rule from CLAUDE.md still applies by construction.</para>
/// </summary>
public class ExamToolsUlsLookupClient(
    IOptions<UlsLookupOptions> options,
    ILogger<ExamToolsUlsLookupClient> logger) : IUlsLookupClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly UlsLookupOptions _options = options.Value;

    public async Task<UlsLookupResult?> LookupByFrnAsync(string frn, CancellationToken cancellationToken)
    {
        var url = $"{_options.BaseUrl.TrimEnd('/')}/api/uls/lookup2/{Uri.EscapeDataString(frn)}";

        try
        {
            using var response = await _httpClient.GetAsync(url, cancellationToken);

            // A 404 on the endpoint itself is not the same as the endpoint saying "notfound" — the
            // latter is a 200 with a type field. Treat an HTTP-level miss as "learned nothing".
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                logger.LogWarning("ULS lookup returned HTTP 404 for the endpoint itself — has the API moved?");
                return null;
            }

            response.EnsureSuccessStatusCode();

            // Read as string first: an empty body is a plausible answer from an endpoint with no
            // published contract, and JsonSerializer would throw on it rather than yielding null.
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(body))
            {
                return UlsLookupResult.NotFound;
            }

            var payload = JsonSerializer.Deserialize<UlsLookupResponse>(body, JsonOptions);
            return payload is null ? UlsLookupResult.NotFound : UlsLookupMapper.Map(payload);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Deliberately not rethrown: this is an optional accelerator over a third-party endpoint,
            // and one FRN failing must not abort the whole scan. The candidate simply stays
            // non-terminal and is retried next run — the same "no tracking field set, so retry
            // automatically" idiom every other job here uses. Never log the FRN itself (PII rules).
            logger.LogWarning(ex, "ULS lookup failed; candidate will be retried on the next run");
            return null;
        }
    }

    private static class UlsLookupMapper
    {
        public static UlsLookupResult Map(UlsLookupResponse r)
        {
            if (string.Equals(r.Type, "notfound", StringComparison.OrdinalIgnoreCase))
            {
                return UlsLookupResult.NotFound;
            }

            return new UlsLookupResult
            {
                Found = true,
                UniqueSystemIdentifier = r.UId,
                CallSign = string.IsNullOrWhiteSpace(r.CallSign) ? null : r.CallSign.Trim().ToUpperInvariant(),
                LicenseStatus = r.LicenseStatus,
                OperatorClass = ParseLicenseClass(r.LicenseClass),
                PreviousOperatorClass = ParseLicenseClass(r.PrevLicenseClass),
                GrantDateUtc = AsUtcDate(r.GrantDate),
                EffectiveDateUtc = AsUtcDate(r.EffectiveDate),
                ExpiredDateUtc = AsUtcDate(r.ExpiredDate),
                CancellationDateUtc = AsUtcDate(r.CancellationDate),
                Frn = string.IsNullOrWhiteSpace(r.Frn) ? null : r.Frn.Trim(),
                LicenseeName = BuildLicenseeName(r),
                PendingApplications = (r.PendingApplications ?? [])
                    .Select(p => new UlsPendingApplication
                    {
                        UlsFileNumber = string.IsNullOrWhiteSpace(p.UlsFileNumber) ? p.LegacyId : p.UlsFileNumber,
                        ApplicationPurpose = string.IsNullOrWhiteSpace(p.ApplicationPurpose) ? null : p.ApplicationPurpose.Trim(),
                        ReceiptDateUtc = AsUtcDate(p.ReceiptDate),
                        History = (p.History ?? [])
                            .Where(h => !string.IsNullOrWhiteSpace(h.Code))
                            .Select(h => new UlsHistoryEntry(AsUtcDate(h.LogDate), h.Code!.Trim().ToUpperInvariant()))
                            .ToList()
                    })
                    .ToList()
            };
        }

        /// <summary>
        /// The response carries the licensee's name in four separate fields rather than one. A club
        /// licence leaves every one of them blank (verified on W1AW), so an empty result is normal
        /// and returns null rather than an empty or whitespace string.
        /// </summary>
        private static string? BuildLicenseeName(UlsLookupResponse r)
        {
            var name = string.Join(' ', new[] { r.FirstName, r.MiddleInitial, r.LastName, r.Suffix }
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .Select(part => part!.Trim()));

            return string.IsNullOrWhiteSpace(name) ? null : name;
        }

        /// <summary>
        /// Novice and Advanced deliberately fall through to None. Candidate.NewLicenseClass can only
        /// ever be Technician/General/Extra (the classes still testable), so mapping a legacy class
        /// to None means "never confirms an upgrade" — which is the conservative, correct outcome for
        /// e.g. an Advanced holder testing for Extra: they stay pending until FCC actually reports
        /// Amateur Extra.
        /// </summary>
        private static LicenseClass ParseLicenseClass(string? value) => value?.Trim().ToLowerInvariant() switch
        {
            "technician" or "technician plus" => LicenseClass.Technician,
            "general" => LicenseClass.General,
            "amateur extra" or "extra" => LicenseClass.Extra,
            _ => LicenseClass.None
        };

        /// <summary>
        /// The API returns date-only values as an instant (e.g. 2026-07-31T08:00:00.000Z). Only the
        /// calendar date is meaningful, and every comparison downstream is .Date-based, so normalise
        /// to a UTC-kind midnight rather than carrying a fake time-of-day around.
        /// </summary>
        private static DateTime? AsUtcDate(DateTime? value) =>
            value is null ? null : DateTime.SpecifyKind(value.Value.Date, DateTimeKind.Utc);
    }
}
