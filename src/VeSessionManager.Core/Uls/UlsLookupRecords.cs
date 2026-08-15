using System.Text.Json.Serialization;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Uls;

/// <summary>
/// One candidate's ULS state as returned by <c>GET /api/uls/lookup2/{frnOrCallsign}</c>, already
/// normalised out of the raw JSON. See docs/uls-watcher.md for the observed response shape and the
/// verification data behind each field's meaning.
/// </summary>
public sealed record UlsLookupResult
{
    /// <summary>False when the endpoint answered <c>type: "notfound"</c> — an unknown FRN is a normal, expected answer (a candidate FCC has never seen), not an error.</summary>
    public bool Found { get; init; }

    /// <summary>ULS "Unique System Identifier" for the *license* — maps to Candidate.FccUlsLicenseKey and powers the FCC ULS deep link.</summary>
    public long? UniqueSystemIdentifier { get; init; }

    public string? CallSign { get; init; }

    /// <summary>"Active", "Pending", … — only "Active" is treated as a grant, matching the old HD License Status "A" rule.</summary>
    public string? LicenseStatus { get; init; }

    /// <summary>Current operator class. Unrecognized/legacy values (Novice, Advanced) map to None, which conservatively means "won't confirm an upgrade" rather than guessing.</summary>
    public LicenseClass OperatorClass { get; init; }

    /// <summary>Original license grant. Does NOT advance on a class upgrade — FCC pins it to the first issuance (a 2026 upgrade can still report a 2021 grant date).</summary>
    public DateTime? GrantDateUtc { get; init; }

    /// <summary>**Does** advance on an upgrade — ExamTools' rendering of HD's Last Action Date, and the only positive same-record signal that an upgrade actually landed.</summary>
    public DateTime? EffectiveDateUtc { get; init; }

    /// <summary>End of the current 10-year term. **This advancing is the only positive confirmation that a renewal was actually issued** — a renewal leaves call sign, class and grant date untouched, so nothing else on the record changes. Verified live 2026-08-05 (W1AW: <c>expired_date</c> 2031-02-26).</summary>
    public DateTime? ExpiredDateUtc { get; init; }

    /// <summary>Set when FCC has cancelled the license outright. Distinct from simply being past <see cref="ExpiredDateUtc"/>, which is still renewable during the grace period.</summary>
    public DateTime? CancellationDateUtc { get; init; }

    /// <summary>FCC's own FRN for the record. Captured so a watch entry added by call sign gets its FRN filled in automatically — the two identify the same license and either can be looked up.</summary>
    public string? Frn { get; init; }

    /// <summary>Licensee name, assembled from the response's separate first/middle/last/suffix fields. A club record (W1AW) leaves all of them blank, so this can legitimately be null on a found record. The address the response also carries is deliberately **not** mapped — nothing here needs it, and not holding it is cheaper than justifying it.</summary>
    public string? LicenseeName { get; init; }

    public IReadOnlyList<UlsPendingApplication> PendingApplications { get; init; } = [];

    public static UlsLookupResult NotFound { get; } = new() { Found = false };
}

public sealed record UlsPendingApplication
{
    public string? UlsFileNumber { get; init; }

    /// <summary>
    /// What the applicant asked FCC for.
    ///
    /// <para><b>ExamTools returns the human-readable description, not FCC's raw code.</b> Observed
    /// live on 2026-08-06: a real renewal came back as <c>"Renewal/Modification"</c>, not <c>"RM"</c>.
    /// The original matcher tested only the two-letter codes, so <see cref="IsRenewal"/> was always
    /// false and the whole request-through-issuance lifecycle never fired — a renewed license just
    /// slid from "Expiring soon" to "Active" with a new expiry, never reporting a renewal at all.</para>
    ///
    /// <para>Matching now accepts either form: the codes, in case another endpoint or a future shape
    /// change returns them, and any description containing "renewal". A substring test is the right
    /// shape here because FCC's descriptions combine purposes ("Renewal/Modification"), so an exact
    /// list would have to enumerate every combination and would break on the next one.</para>
    /// </summary>
    public string? ApplicationPurpose { get; init; }

    /// <summary>True when <see cref="ApplicationPurpose"/> names a renewal, in code or description form.</summary>
    public bool IsRenewal
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ApplicationPurpose)) return false;
            var purpose = ApplicationPurpose.Trim();

            return RenewalPurposeCodes.Contains(purpose, StringComparer.OrdinalIgnoreCase)
                || purpose.Contains("renewal", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>FCC's two-letter purpose codes for renewals — kept alongside the description match, not replaced by it.</summary>
    private static readonly string[] RenewalPurposeCodes = ["RO", "RM"];

    /// <summary>When FCC received the application. Maps to Candidate.ApplicationDateEnteredUtc — verified to equal the value the old HD-Last-Action-Date rule produced for a real candidate.</summary>
    public DateTime? ReceiptDateUtc { get; init; }

    /// <summary>Chronological history entries, each carrying a ULS action code (RDLOFF/RDLCOM, BQOFF/BQCOM, FVPOFF/FVPCNF/FVPCOM …).</summary>
    public IReadOnlyList<UlsHistoryEntry> History { get; init; } = [];
}

public sealed record UlsHistoryEntry(DateTime? LogDateUtc, string Code);

/// <summary>Raw JSON shapes — kept separate from the normalised records above so the mapping stays in one place (UlsLookupMapper) and a shape change surfaces there rather than across the service.</summary>
internal sealed class UlsLookupResponse
{
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("u_id")] public long? UId { get; set; }
    [JsonPropertyName("callsign")] public string? CallSign { get; set; }
    [JsonPropertyName("license_status")] public string? LicenseStatus { get; set; }
    [JsonPropertyName("license_class")] public string? LicenseClass { get; set; }
    /// <summary>Parsed but not surfaced. Kept because this type's job is to describe ExamTools'
    /// response, and knowing the field exists is worth more than the line costs — the mapped
    /// UlsLookupResult property was removed 2026-08-11 as genuinely unread (audit T36). Restoring it
    /// is a one-line change if #195's timeline ever wants "General (was Technician)".</summary>
    [JsonPropertyName("prev_license_class")] public string? PrevLicenseClass { get; set; }
    [JsonPropertyName("grant_date")] public DateTime? GrantDate { get; set; }
    [JsonPropertyName("effective_date")] public DateTime? EffectiveDate { get; set; }
    [JsonPropertyName("expired_date")] public DateTime? ExpiredDate { get; set; }
    [JsonPropertyName("cancellation_date")] public DateTime? CancellationDate { get; set; }
    [JsonPropertyName("frn")] public string? Frn { get; set; }
    [JsonPropertyName("first_name")] public string? FirstName { get; set; }
    [JsonPropertyName("middle_initial")] public string? MiddleInitial { get; set; }
    [JsonPropertyName("last_name")] public string? LastName { get; set; }
    [JsonPropertyName("suffix")] public string? Suffix { get; set; }
    [JsonPropertyName("pendingApplications")] public List<UlsPendingApplicationResponse>? PendingApplications { get; set; }

    // The response also returns address/city/state/zip/pobox. Deliberately unmapped — see
    // UlsLookupResult.LicenseeName.
}

internal sealed class UlsPendingApplicationResponse
{
    [JsonPropertyName("uls_filenumber")] public string? UlsFileNumber { get; set; }

    /// <summary>`/lookup2/` names this `uls_filenumber`, but `/lookup/` used `_id` for the same value — accept both so a shape tweak doesn't silently blank the file number.</summary>
    [JsonPropertyName("_id")] public string? LegacyId { get; set; }

    [JsonPropertyName("application_purpose")] public string? ApplicationPurpose { get; set; }
    [JsonPropertyName("receipt_date")] public DateTime? ReceiptDate { get; set; }
    [JsonPropertyName("history")] public List<UlsHistoryResponse>? History { get; set; }
}

internal sealed class UlsHistoryResponse
{
    [JsonPropertyName("log_date")] public DateTime? LogDate { get; set; }
    [JsonPropertyName("code")] public string? Code { get; set; }
}
